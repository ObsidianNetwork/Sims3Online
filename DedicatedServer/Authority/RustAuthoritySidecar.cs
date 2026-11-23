using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Authority;

/// <summary>
/// Wave 7 · opt-in C# &#8596; Rust authority sidecar bridge (card 31 &#167;A / card 33).
/// A line-framed loopback TCP client of <c>simlink_authority_daemon.exe</c>: the controller
/// may ask the Rust kernel to adjudicate a command and read back the authoritative delta
/// frames (WELCOME / SNAP / WFC / PRIMARY / ACK / Hd / Rl / AS / Vh / Vs / Vp / Ve / DLG / DLGC / DLGA / ERR).
///
/// <para><b>Default-off.</b> Nothing constructs this unless <c>SIMSONLINE_RUST_SIDECAR=1</c>
/// is set in the environment; <see cref="TryOpen"/> and <see cref="TryCreate"/> return
/// <see langword="null"/> otherwise, so the Option-2 seam is additive (card 31 &#167;C) and every
/// existing C# test and join path runs unchanged. The daemon is the sidecar's own Rust process;
/// this adapter never touches <c>TS3W.exe</c>, never takes a stack claim, and connects to nothing
/// beyond <see cref="LoopbackHostText"/> — card 31 &#167;D and SECURITY.md: the sidecar endpoint
/// is an admin-surface class, loopback only, its host is deliberately not configurable.</para>
///
/// <para>The wire framing mirrors the daemon's <c>runtime_bridge</c> line grammar exactly:
/// <c>CONNECT|19|ring|worldFromServer|bridgeVer|simlinkVer|role</c> (the re-attach hello,
/// card 31 row 1), <c>HELLO|corr|ring|worldFromServer|bridgeVer|simlinkVer|role</c> (the card-19
/// CLIENT_ENV gate), <c>CMD|corr|Verb|args&#8230;</c> and <c>PING|corr</c>. A request is answered
/// with zero or more delta lines then the fence <c>PONG|corr</c>, which proves every preceding line
/// was adjudicated in order. A transport failure is never an authority answer: callers treat
/// <see langword="null"/> as "seam off, use the native answer", while a structured
/// <c>ERR|corr|&#60;KernelError&#62;|&#8230;</c> from the card-33 §C set is relayed verbatim.</para>
/// </summary>
public sealed class RustAuthoritySidecar : IDisposable
{
    /// <summary>The opt-in switch (card 31: the sidecar is additive, never required).</summary>
    public const string EnvVar = "SIMSONLINE_RUST_SIDECAR";

    /// <summary>Overrides the sidecar daemon's TCP port; the host stays 127.0.0.1 (SECURITY.md).</summary>
    public const string EnvPortVar = "SIMSONLINE_RUST_SIDECAR_PORT";

    /// <summary>The daemon's default listen port (<c>simlink_authority_daemon.rs --port</c>).</summary>
    public const int DefaultPort = 34019;

    /// <summary>The only address family the sidecar endpoint ever uses (card 31 §D).</summary>
    public const string LoopbackHostText = "127.0.0.1";

    /// <summary>The bridge version pair the daemon's hello gate expects (card 19 / card 46).</summary>
    public const int BridgeProtocolVersion = 17;

    /// <summary>The SimLink native version the daemon's hello gate expects (card 19 / card 46).</summary>
    public const int SimLinkNativeVersion = 15;

    /// <summary>The `CONNECT|19` compat-slot hello version (, unchanged framing).</summary>
    public const string ConnectCompatVersion = "19";

    /// <summary>
    /// Wave 11 · card 51: the clean-room A* replanner's authority verb. Card 30 row 1 owns it and
    /// `AUTHORITY_ROUTES` in the kernel currently maps it to <c>AuthorityOwner::NativeTs3W</c>, so
    /// the seam is wired ahead of the kernel implementation and every answer stays named (never a
    /// silent drop) on both sides of the cutover.
    /// </summary>
    public const string NavigateAStarVerb = "NavigateAStar";

    /// <summary>The frame prefix the kernel broadcasts one planned route's legs under (card 51).</summary>
    public const string RouteFramePrefix = "Rt|";

    /// <summary>True only when <c>SIMSONLINE_RUST_SIDECAR=1</c> is set; the controller checks this before use.</summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);

    /// <summary>The bounded IO clock every attach/consult uses: a wedged kernel answers with a
    /// transport failure, never a blocked join (card 32 §C.1).</summary>
    public static readonly TimeSpan DefaultIoTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The structured KernelError rejection names the controller must relay to the client verbatim
    /// (card 33 §C + card 31 §A row 4 — <c>StaleObjectHandle</c> is deliberately absent: card 31 §E
    /// keeps it a controller-log-only fact, never a client-facing message).
    /// </summary>
    public static readonly IReadOnlySet<string> AuthoritativeRejections = new HashSet<string>(StringComparer.Ordinal)
    {
        "UnauthorizedClientInsertion",
        "ClientWorldServiceSuppressed",
        "SlotAlreadyReserved",
        "native_oracle_owned",
        "ClientReselectForbidden",
    };

    private readonly TcpClient _tcp = new();
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private int _corr;
    // Command identity -> the corr its first send took (the attachment's whole life; see ForwardCommand).
    // Keyed by text so the public protocol's CommandGuid and the route seam's numeric command ids share
    // one monotonic corr band and one eviction rule (card 33 §D.2 + card 51).
    // Accessed under the caller's per-player Gate lock, like every other line-socket state here.
    private readonly Dictionary<string, uint> _corrByCommandId = new();

    private RustAuthoritySidecar(IPEndPoint ep, TimeSpan? ioTimeout)
    {
        if (!IPAddress.IsLoopback(ep.Address))
        {
            throw new ArgumentException($"the sidecar endpoint must be loopback-only (SECURITY.md): {ep}", nameof(ep));
        }

        if (ioTimeout is TimeSpan timeout)
        {
            _tcp.ConnectAsync(ep).WaitAsync(timeout).GetAwaiter().GetResult(); // TimeoutException on a wedged daemon
        }
        else
        {
            _tcp.Connect(ep); // loopback only
        }

        _tcp.NoDelay = true;
        if (ioTimeout is TimeSpan io)
        {
            int ms = Math.Max(1, (int)io.TotalMilliseconds);
            _tcp.ReceiveTimeout = ms; // a slow kernel answers with a transport failure, never a blocked join
            _tcp.SendTimeout = ms;    // (card 32 §C.1: the tick loop must never wait on the sidecar)
        }
        NetworkStream net = _tcp.GetStream();
        _reader = new StreamReader(net, Encoding.UTF8);
        _writer = new StreamWriter(net, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>
    /// The sidecar endpoint: loopback <see cref="LoopbackHostText"/> always, port from
    /// <see cref="EnvPortVar"/> else <see cref="DefaultPort"/> (the daemon's own default). An
    /// unparseable or out-of-range override falls back to the default rather than failing the boot.
    /// </summary>
    public static IPEndPoint ResolveEndpoint()
    {
        int port = DefaultPort;
        string? raw = Environment.GetEnvironmentVariable(EnvPortVar);
        if (raw is not null
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed is >= 1 and <= 65535)
        {
            port = parsed;
        }
        return new IPEndPoint(IPAddress.Parse(LoopbackHostText), port);
    }

    /// <summary>
    /// The card-19 CLIENT_ENV attestation the daemon's <c>hello_gate</c> grades (card 32 §A step 4):
    /// SimLink client ring, world-from-server, bridge v17, SimLink v15, and this connection's role tag.
    /// </summary>
    public readonly record struct ClientEnv(
        bool SimlinkClientRing,
        bool WorldFromServer,
        int BridgeVersion,
        int SimlinkNativeVersion,
        string Role)
    {
        /// <summary>An attestation the gate accepts (tests and the conforming client posture).</summary>
        public static ClientEnv Conforming(string role = "client") =>
            new(true, true, BridgeProtocolVersion, SimLinkNativeVersion, role);

        /// <summary>
        /// The controller's own posture for a joining client: the two flags ride the same env vars the
        /// patched TS3 clients launch with (SIMSONLINE_SIMLINK_CLIENT_RING / SIMSONLINE_WORLD_FROM_SERVER,
        /// card 19); an unpatched deployment therefore produces the card-32 §D named refusal.
        /// </summary>
        public static ClientEnv FromControllerEnvironment() => new(
            string.Equals(Environment.GetEnvironmentVariable("SIMSONLINE_SIMLINK_CLIENT_RING"), "1", StringComparison.Ordinal),
            string.Equals(Environment.GetEnvironmentVariable("SIMSONLINE_WORLD_FROM_SERVER"), "1", StringComparison.Ordinal),
            BridgeProtocolVersion,
            SimLinkNativeVersion,
            "client");

        private string RingFlag => SimlinkClientRing ? "1" : "0";
        private string WorldFlag => WorldFromServer ? "1" : "0";

        /// <summary>`CONNECT|19|ring|wfs|bv|sv|role` — the compat re-attach hello (card 31 row 1).</summary>
        public string ConnectLine() =>
            $"CONNECT|{ConnectCompatVersion}|{RingFlag}|{WorldFlag}|{BridgeVersion.ToString(CultureInfo.InvariantCulture)}|{SimlinkNativeVersion.ToString(CultureInfo.InvariantCulture)}|{Role}";

        /// <summary>`HELLO|corr|ring|wfs|bv|sv|role` — the card-19 CLIENT_ENV gate line.</summary>
        public string HelloLine(int corr = 1) =>
            $"HELLO|{corr.ToString(CultureInfo.InvariantCulture)}|{RingFlag}|{WorldFlag}|{BridgeVersion.ToString(CultureInfo.InvariantCulture)}|{SimlinkNativeVersion.ToString(CultureInfo.InvariantCulture)}|{Role}";
    }

    /// <summary>
    /// Returns a connected and hello-gated sidecar for the controller seam, or <see langword="null"/>:
    /// seam off (<see cref="Enabled"/> false), a transport failure (<paramref name="transportFailure"/>
    /// set), or an active hello_gate refusal (<paramref name="helloRejection"/> set to the named
    /// <c>hello_rejected|&#60;Reason&#62;</c> — card 32 §D, the joiner is refused BEFORE any chunk
    /// byte moves). Callers treat null with a null rejection as "seam off, use the native answer".
    /// Every path is bounded by <paramref name="ioTimeout"/> so a wedged daemon can never stall a join.
    /// </summary>
    public static RustAuthoritySidecar? TryOpen(
        ClientEnv env,
        TimeSpan ioTimeout,
        out string? helloRejection,
        out string? transportFailure) =>
        TryOpen(env, ResolveEndpoint(), ioTimeout, out helloRejection, out transportFailure);

    /// <summary>Endpoint-explicit form of <see cref="TryOpen(ClientEnv, TimeSpan, out string?, out string?)"/>.</summary>
    public static RustAuthoritySidecar? TryOpen(
        ClientEnv env,
        IPEndPoint ep,
        TimeSpan ioTimeout,
        out string? helloRejection,
        out string? transportFailure)
    {
        helloRejection = null;
        transportFailure = null;
        if (!Enabled)
        {
            return null;
        }

        RustAuthoritySidecar? sidecar = null;
        try
        {
            sidecar = new RustAuthoritySidecar(ep, ioTimeout);
            IReadOnlyList<string> frames = sidecar.Hello(env);
            // Only the gate's own refusal is an authority rejection; other ERR noise (a version
            // mismatch on the CONNECT line) never masquerades as one (card 19 §C names them).
            string? refusal = frames.FirstOrDefault(f =>
                f.StartsWith("ERR|", StringComparison.Ordinal)
                && f.Split('|').Length >= 3 && f.Split('|')[2] == "hello_rejected");
            if (refusal is not null)
            {
                // ERR|corr|hello_rejected|<NamedReason> — relay the kernel's name verbatim.
                string[] fields = refusal.Split('|');
                helloRejection = fields.Length >= 4
                    ? fields[2] + "|" + string.Join("|", fields.Skip(3))
                    : fields[2];
                sidecar.Dispose();
                return null;
            }
            if (!frames.Any(f => f.StartsWith("WELCOME|", StringComparison.Ordinal)))
            {
                transportFailure = "no WELCOME frame before the fence: " + string.Join(" ", frames);
                sidecar.Dispose();
                return null;
            }
            return sidecar;
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException
                                   or ObjectDisposedException or ArgumentException or OperationCanceledException)
        {
            transportFailure = ex.GetType().Name + ": " + ex.Message;
            try { sidecar?.Dispose(); } catch { /* already gone */ }
            return null;
        }
    }

    /// <summary>
    /// Returns a connected sidecar when the feature is enabled, or <see langword="null"/> when
    /// <c>SIMSONLINE_RUST_SIDECAR&#8800;1</c> (the default). Callers must treat null as "seam off,
    /// use the native answer" — never an error. No hello is sent; see <see cref="TryOpen"/> for the
    /// gated lifecycle entry point the controller uses.
    /// </summary>
    public static RustAuthoritySidecar? TryCreate(IPEndPoint daemonLoopback)
        => Enabled ? new RustAuthoritySidecar(daemonLoopback, ioTimeout: null) : null;

    /// <summary>
    /// The card-19 CLIENT_ENV HELLO (card 32 §A step 4) then the CONNECT|19 compat re-attach hello
    /// (, card 31 row 1) — the daemon's per-connection state machine greets on HELLO and
    /// grades the CONNECT line only once greeted, so HELLO goes first over the live socket.
    /// A refused attestation answers <c>ERR|1|hello_rejected|&#60;Reason&#62;</c> — a named
    /// diagnostic, never a bare reset (card 19 §C).
    /// </summary>
    public IReadOnlyList<string> Hello(ClientEnv env) =>
        Batch(env.HelloLine() + "\n" + env.ConnectLine() + "\n");

    /// <summary>Convenience hello with a conforming attestation (tests, tooling).</summary>
    public IReadOnlyList<string> Hello(string role = "client") => Hello(ClientEnv.Conforming(role));

    /// <summary>
    /// Card 32 bootstrap: fingerprint (the three <see cref="JoinWorldReuse"/> parts; the defaults
    /// always miss, exercising the checkpoint branch), then the WFC frames + read-only
    /// primary pin + SIMC claim replay + history reset ride the answer.
    /// </summary>
    public IReadOnlyList<string> RequestWorld(int p0 = 9, int p1 = 9, int p2 = 9) =>
        Batch($"CMD|2|RequestWorld|{p0.ToString(CultureInfo.InvariantCulture)}|{p1.ToString(CultureInfo.InvariantCulture)}|{p2.ToString(CultureInfo.InvariantCulture)}\n");

    /// <summary>Send one <c>CMD|corr|Verb|args…</c> and read until the ordered PONG fence.</summary>
    public IReadOnlyList<string> Command(string verb, params object[] args)
    {
        // Fresh-command corrs live in [1e9, ...): above the boot corrs (1, 2) and below the fence
        // band (2e9+), strictly increasing per attachment — what the daemon's monotonic corr gate
        // (wire.rs accept_command) requires: a lower corr than last_seen is dropped silently.
        int corr = Interlocked.Increment(ref _corr) + 1_000_000_000;
        string argText = args.Length == 0 ? "" : "|" + string.Join("|", args.Select(ToString));
        return Batch($"CMD|{corr.ToString(CultureInfo.InvariantCulture)}|{verb}{argText}\n");
    }

    /// <summary>
    /// The seven card-33 / card-31 §A authority verbs. The kernel owns each verdict; the controller
    /// forwards, relays the ACK + delta frames, and surfaces an authoritative rejection verbatim.
    /// </summary>
    public SidecarVerdict ClaimSim(object simId, object householdId) => Forward("ClaimSim", simId, householdId);
    public SidecarVerdict SwapClaim(object oldSimId, object newSimId) => Forward("SwapClaim", oldSimId, newSimId);
    public SidecarVerdict EnqueueInteraction(object actor, object family, object producer, object target, object priority) =>
        Forward("EnqueueInteraction", actor, family, producer, target, priority);
    public SidecarVerdict ModifyFunds(object householdId, object delta, object reason) =>
        Forward("ModifyFunds", householdId, delta, reason);
    public SidecarVerdict DialogAnswer(object requestId, object choice) => Forward("DialogAnswer", requestId, choice);
    public SidecarVerdict RouteSlotClaim(object simId, object objectGuid, object slotIndex, object generation) =>
        Forward("RouteSlotClaim", simId, objectGuid, slotIndex, generation);
    public SidecarVerdict SpawnService(object kind) => Forward("SpawnService", kind);

    /// <summary>
    /// Wave 11 · card 54 §C: ask the kernel to plan an A* route for one Sim between two nav-cell ids —
    /// <c>CMD|&#60;corr&#62;|NavigateAStar|&#60;simId&#62;|&#60;startCell&#62;|&#60;goalCell&#62;|&#60;flags&#62;</c>.
    /// The corr comes from the command identity, exactly like <see cref="ForwardCommand"/>, so a retry of
    /// one request replays the kernel's original answer while every NEW request takes a strictly higher
    /// corr (card 54 §D: the codec stale-drops a recycled corr). A route is a PLAN: nothing here moves a
    /// Sim or queues an interaction — the walk starts at card 25's leg handoff.
    /// </summary>
    /// <param name="commandId">Caller's request identity; the replay key, not the wire corr.</param>
    /// <param name="simId">The Sim's object id; narrowed to the kernel's u32 <c>SimId</c> the way every
    /// forwarded id is (see <see cref="RustAuthorityForward.ToKernelSimId"/>).</param>
    /// <param name="startCell">Start nav-cell index of the plan's navmesh (card 51 §B <c>cells[]</c>).</param>
    /// <param name="goalCell">Goal nav-cell index.</param>
    /// <param name="flags">The request-flags tail card 54 §C defines (0 = the plain plan).</param>
    public RoutePlanResult PlanRouteAStar(ulong commandId, ulong simId, uint startCell, uint goalCell, uint flags) =>
        RoutePlanResult.Parse(Batch(NavigateAStarLine(
            CorrForCommand(commandId), simId, startCell, goalCell, flags)));

    /// <summary>The 0-flags form (the plain plan).</summary>
    public RoutePlanResult PlanRouteAStar(ulong commandId, ulong simId, uint startCell, uint goalCell) =>
        PlanRouteAStar(commandId, simId, startCell, goalCell, 0u);

    /// <summary>Card 61's trusted planning channel, enabled independently of the public-command sidecar.</summary>
    public static RustAuthoritySidecar OpenRoadPlanning()
    {
        RustAuthoritySidecar sidecar = new(ResolveEndpoint(), TimeSpan.FromSeconds(0.5));
        try
        {
            IReadOnlyList<string> greeting = sidecar.Hello(ClientEnv.Conforming());
            if (!greeting.Any(f => f.StartsWith("WELCOME|", StringComparison.Ordinal)))
                throw new IOException("road planning hello refused: " + string.Join(" ", greeting));
            return sidecar;
        }
        catch
        {
            sidecar.Dispose();
            throw;
        }
    }

    public RoadPlaceResult PlaceOnRoadOffScreen(SimsOnline.DedicatedServer.HostBridge.RoadPlaceRequest request) =>
        RoadPlaceResult.Parse(Batch(PlaceOnRoadOffScreenLine(request)), request);

    public static string PlaceOnRoadOffScreenLine(SimsOnline.DedicatedServer.HostBridge.RoadPlaceRequest request) =>
        "CMD|" + request.Corr.ToString(CultureInfo.InvariantCulture) + "|PlaceOnRoadOffScreen|" + request.Arguments + "\n";

    /// <summary>
    /// The request line on its own, split out so a test pins the exact bytes rather than the intent:
    /// <c>CMD|&#60;corr&#62;|NavigateAStar|&#60;sim(u32)&#62;|&#60;start&#62;|&#60;goal&#62;|&#60;flags&#62;</c> and the
    /// line feed the fenced batch writes.
    /// </summary>
    public static string NavigateAStarLine(uint corr, ulong simId, uint startCell, uint goalCell, uint flags) =>
        "CMD|" + corr.ToString(CultureInfo.InvariantCulture) + "|" + NavigateAStarVerb + "|"
        + RustAuthorityForward.ToKernelSimId(simId).ToString(CultureInfo.InvariantCulture) + "|"
        + startCell.ToString(CultureInfo.InvariantCulture) + "|"
        + goalCell.ToString(CultureInfo.InvariantCulture) + "|"
        + flags.ToString(CultureInfo.InvariantCulture) + "\n";

    /// <summary>Forward one verb and classify the frames the fence returns (card 33 §A steps 4/9).</summary>
    public SidecarVerdict Forward(string verb, params object[] args) =>
        SidecarVerdict.Classify(Command(verb, args));

    /// <summary>
    /// Forward under an explicit correlation id. Kept for tests and tooling; the live seam uses
    /// <see cref="ForwardCommand"/>, because the daemon's corr gate is monotonic per player
    /// (wire.rs accept_command) — a fresh random corr per retry would be dropped, never replayed.
    /// </summary>
    public SidecarVerdict ForwardWithCorr(uint corr, string verb, params object[] args) =>
        SidecarVerdict.Classify(CommandWithCorr(corr, verb, args));

    /// <summary>
    /// The controller's forward path (card 33 §D.2 + card 35 gate 11). The daemon replays a
    /// duplicate corr from its CommandId window and drops any LOWER corr (monotonic gate), so the
    /// corr must be per-command-identity, not per-send: the first send of a CommandId takes the
    /// next increasing corr and every retry of the same CommandId rides that stored corr — the
    /// replay path — so a client retry (across a reconnect too, the attachment outlives the
    /// connection) returns the ORIGINAL result and never a second effect.
    /// </summary>
    public SidecarVerdict ForwardCommand(Guid commandId, string verb, params object[] args) =>
        SidecarVerdict.Classify(CommandWithCorr(CorrForCommand(commandId), verb, args));

    /// <summary>The request-world consult for one command's identity (same replay rule as <see cref="ForwardCommand"/>).</summary>
    public IReadOnlyList<string> RequestWorldFor(Guid commandId) =>
        Batch($"CMD|{CorrForCommand(commandId).ToString(CultureInfo.InvariantCulture)}|RequestWorld|9|9|9\n");

    private uint CorrForCommand(Guid commandId) => CorrForCommand(commandId.ToString("N"));

    /// <summary>The same per-identity rule for a numeric command id (the route-planning seam, card 51).</summary>
    private uint CorrForCommand(ulong commandId) =>
        CorrForCommand("r" + commandId.ToString(CultureInfo.InvariantCulture));

    private uint CorrForCommand(string commandKey)
    {
        if (_corrByCommandId.TryGetValue(commandKey, out uint known))
        {
            return known;
        }
        uint fresh = (uint)(Interlocked.Increment(ref _corr) + 1_000_000_000);
        if (_corrByCommandId.Count > 4096)
        {
            _corrByCommandId.Clear(); // coarse but safe: forgetting old ids only loses replay hits outside the daemon's own 32-entry window
        }
        _corrByCommandId[commandKey] = fresh;
        return fresh;
    }

    /// <summary>Send one caller-corr <c>CMD|corr|Verb|args…</c> and read until the ordered PONG fence.</summary>
    public IReadOnlyList<string> CommandWithCorr(uint corr, string verb, params object[] args)
    {
        string argText = args.Length == 0 ? "" : "|" + string.Join("|", args.Select(ToString));
        return Batch($"CMD|{corr.ToString(CultureInfo.InvariantCulture)}|{verb}{argText}\n");
    }

    /// <summary>
    /// Write the given pre-framed lines, then a trailing <c>PING|fence</c>, and return every
    /// delta line received before the matching <c>PONG|fence</c>. The fence is the daemon's
    /// own ordering guarantee, so no sleeps are needed.
    /// </summary>
    public IReadOnlyList<string> Batch(string lines)
    {
        uint fence = (uint)(Interlocked.Increment(ref _corr) + 2_000_000_000);
        _writer.Write(lines);
        _writer.Write("PING|");
        _writer.Write(fence.ToString(CultureInfo.InvariantCulture));
        _writer.Write('\n');
        string sentinel = "PONG|" + fence.ToString(CultureInfo.InvariantCulture);
        var frames = new List<string>();
        while (true)
        {
            string? line = _reader.ReadLine();
            if (line is null)
                throw new IOException("authority sidecar closed before the fence");
            line = line.TrimEnd('\r');
            if (line == sentinel) break;
            frames.Add(line);
        }
        return frames;
    }

    internal static string ToString(object value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    public void Dispose()
    {
        try { _reader.Dispose(); } catch { /* best effort */ }
        try { _writer.Dispose(); } catch { /* best effort */ }
        _tcp.Dispose();
    }
}

/// <summary>
/// One adjudication answer of the sidecar (card 33 §A step 9): Accepted with the authoritative
/// delta frames attached, Rejected naming the exact <c>KernelError</c> the kernel sent (never a
/// silent drop), or — no ACK and no authoritative ERR — the seam had no opinion (e.g. a
/// <c>bad_line</c> for a verb the demo kernel does not model), which callers must treat as
/// "use the native answer" because the seam is additive (card 31 §C).
/// </summary>
public sealed record SidecarVerdict(
    bool Accepted,
    string? RejectionName,
    string? RejectionDetail,
    IReadOnlyList<string> Frames)
{
    /// <summary>True when the kernel refused with a structured, client-facing KernelError name.</summary>
    public bool IsAuthoritativeRejection => RejectionName is not null;

    /// <summary>The rejection as the client must see it: the kernel's own frame, verbatim.</summary>
    public string RejectionText => "ERR|" + RejectionName + (RejectionDetail is null ? "" : "|" + RejectionDetail);

    /// <summary>Classify the frames one fenced batch returned (card 33 §C: exact names, never swallowed).</summary>
    public static SidecarVerdict Classify(IReadOnlyList<string> frames)
    {
        foreach (string frame in frames)
        {
            if (!frame.StartsWith("ERR|", StringComparison.Ordinal))
            {
                continue;
            }
            string[] fields = frame.Split('|');
            string name = fields.Length >= 3 ? fields[2] : string.Empty;
            if (RustAuthoritySidecar.AuthoritativeRejections.Contains(name))
            {
                return new SidecarVerdict(false, name,
                    fields.Length >= 4 ? string.Join("|", fields.Skip(3)) : null, frames);
            }
        }
        bool accepted = frames.Any(f =>
            f.StartsWith("ACK|", StringComparison.Ordinal) || f.StartsWith("Hd|", StringComparison.Ordinal));
        return new SidecarVerdict(accepted, null, null, frames);
    }
}

/// <summary>
/// Wave 11 · card 54 §C: one kernel answer to a <c>NavigateAStar</c> request. The plan frame is
/// <c>Rt|&#60;corr&#62;|&#60;sim_id&#62;|&#60;leg_count&#62;|&#60;path_hash_16hex&#62;|&#60;total_cost_mm&#62;</c> (exactly
/// six fields — card 54 §C.1 requires the field-count check, because the mod's memory-bridge channel
/// already uses <c>Rt|sim|destX|destY|destZ|facing|tag|destSlot|destObj</c> and a mis-routed frame must
/// become <c>bad_line</c> rather than a silently wrong route), with the geometry arriving as <c>Vp|</c>
/// segment frames on the tick-pump stream. The brief's key=value ACK shape
/// (<c>ACK|&#60;corr&#62;|NavigateAStar|legs=|cost_mm=|hash=|tick=</c>) is accepted too and fills the same
/// fields, so a reader of either grammar gets one result type.
///
/// <para>Parsing is total, the rule <see cref="SidecarJoinBootstrap"/> follows: a part the kernel did not
/// send stays <see langword="null"/> so the controller log names the gap instead of reading zero, and
/// no plan frame + no ERR is "the seam had no opinion" (card 31 §C additive) — never a plan. Today the
/// verb is still <c>AuthorityOwner::NativeTs3W</c> and card 54 §A's two gates are unmet, so a live
/// request answers <c>ERR|&#60;corr&#62;|bad_line|NavigateAStar</c> (the CMD arm has no case for it) or
/// <c>ERR|0|native_oracle_owned|NavigateAStar</c> (the bare-verb refusal) — both named, never silent.</para>
/// </summary>
public sealed record RoutePlanResult(
    bool Planned,
    uint Corr,
    ulong? SimId,
    uint? LegCount,
    string? PathHash,
    ulong? TotalCostMm,
    ulong? Tick,
    string? RejectionName,
    string? RejectionDetail,
    IReadOnlyList<string> SegmentFrames,
    IReadOnlyList<string> Frames)
{
    /// <summary>
    /// The routing refusals card 54 §C names. They are deliberately NOT added to
    /// <see cref="AuthoritativeRejections"/> here: that set is the client-facing relay contract of
    /// card 33 §C, and promoting them is a change for both sides of the wire plus docs/protocol.md in
    /// one unit (card 54 §C.1, AGENTS: a contract changed on one side only blocks a merge).
    /// </summary>
    public static readonly IReadOnlySet<string> RouteRejections = new HashSet<string>(StringComparer.Ordinal)
    {
        "RouteStartOutOfBounds",
        "RouteGoalUnreachable",
        "PortalLocked",
        "FormatUnsupported",
    };

    /// <summary>True when the kernel refused for a routing reason it models (card 54 §C's table).</summary>
    public bool IsRouteRejection => RejectionName is not null && RouteRejections.Contains(RejectionName);

    /// <summary>True on the card-30 row-1 answer: navmesh geometry still belongs to the native TS3W oracle.</summary>
    public bool IsNativeOracleRefusal =>
        string.Equals(RejectionName, "native_oracle_owned", StringComparison.Ordinal);

    /// <summary>True while the CMD arm has no NavigateAStar case yet (the promotion has not landed kernel-side).</summary>
    public bool IsKernelPending =>
        string.Equals(RejectionName, "bad_line", StringComparison.Ordinal)
        && string.Equals(RejectionDetail, RustAuthoritySidecar.NavigateAStarVerb, StringComparison.Ordinal);

    /// <summary>True when an <c>Rt|</c> frame arrived but did not match card 54 §C's shape — named, not guessed.</summary>
    public bool IsMalformedRouteFrame =>
        string.Equals(RejectionName, "bad_line", StringComparison.Ordinal)
        && RejectionDetail is not null && RejectionDetail.StartsWith("Rt", StringComparison.Ordinal);

    /// <summary>True when an answer arrived at all: a plan frame or a named ERR. Silence is a transport failure.</summary>
    public bool IsStructuredAnswer => Planned || RejectionName is not null;

    /// <summary>
    /// Card 55 E3: the plan is only comparable across clients when its identity is complete — same
    /// <c>path_hash</c> and <c>total_cost_mm</c> on every window, or the run fails even if the walk looks right.
    /// </summary>
    public bool HasPlanIdentity =>
        Planned && LegCount is not null && PathHash is not null && TotalCostMm is not null;

    /// <summary>True when the plan frame carries the corr this request sent (card 54 §D reorder rule).</summary>
    public bool MatchesCorr(uint expectedCorr) => Planned && Corr == expectedCorr;

    /// <summary>Parse the frames one fenced <c>NavigateAStar</c> request returned (never swallows a reason).</summary>
    public static RoutePlanResult Parse(IReadOnlyList<string> frames)
    {
        IReadOnlyList<string> segments = frames
            .Where(f => f.StartsWith("Vp|", StringComparison.Ordinal))
            .ToList();

        static bool IsHex16(string s) => s.Length == 16 && s.All(Uri.IsHexDigit);

        string? plan = frames.FirstOrDefault(f =>
            f.StartsWith(RustAuthoritySidecar.RouteFramePrefix, StringComparison.Ordinal));
        if (plan is not null)
        {
            // Card 54 §C.1: exactly six fields, each of the right shape. The mod's memory-bridge route
            // frame (nine fields, `Rt|sim|destX|destY|destZ|facing|tag|destSlot|destObj`) also arrives
            // here if it is ever mis-routed onto the sidecar, so a mismatch is NAMED as bad_line with
            // the reason rather than read as a route with wrong numbers.
            string[] f = plan.Split('|');
            if (f.Length != 6
                || !uint.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint corr)
                || !ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong simId)
                || !uint.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out uint legCount)
                || !IsHex16(f[4])
                || !ulong.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out ulong costMm))
            {
                return new RoutePlanResult(false, 0, null, null, null, null, null, "bad_line",
                    "Rt|fieldCount=" + f.Length.ToString(CultureInfo.InvariantCulture), segments, frames);
            }
            return new RoutePlanResult(true, corr, simId, legCount, f[4], costMm, null, null, null, segments, frames);
        }

        string? ack = frames.FirstOrDefault(f =>
            f.StartsWith("ACK|", StringComparison.Ordinal)
            && f.Contains("|" + RustAuthoritySidecar.NavigateAStarVerb + "|", StringComparison.Ordinal));
        if (ack is not null)
        {
            // The key=value shape the Wave 11 brief describes; the same fields as Rt, order-insensitive.
            string[] head = ack.Split('|');
            uint ackCorr = head.Length >= 2
                && uint.TryParse(head[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint c) ? c : 0u;
            ulong? simId = null;
            uint? legCount = null;
            ulong? totalCostMm = null;
            string? pathHash = null;
            ulong? tick = null;
            foreach (string part in ack.Split('|').Skip(3))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue; // a positional field of the ACK tail, not a key=value reading
                }
                string key = part.Substring(0, eq);
                string value = part.Substring(eq + 1);
                switch (key)
                {
                    case "sim":
                    case "sim_id":
                        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong s)) simId = s;
                        break;
                    case "legs":
                    case "leg_count":
                        if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint l)) legCount = l;
                        break;
                    case "cost_mm":
                    case "total_cost_mm":
                        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong m)) totalCostMm = m;
                        break;
                    case "hash":
                    case "path_hash":
                        pathHash = value.Length == 0 ? null : value;
                        break;
                    case "tick":
                        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong t)) tick = t;
                        break;
                }
            }
            return new RoutePlanResult(true, ackCorr, simId, legCount, pathHash, totalCostMm, tick, null, null, segments, frames);
        }

        foreach (string frame in frames)
        {
            if (!frame.StartsWith("ERR|", StringComparison.Ordinal))
            {
                continue;
            }
            string[] fields = frame.Split('|');
            string name = fields.Length >= 3 ? fields[2] : string.Empty;
            if (name.Length == 0)
            {
                continue;
            }
            return new RoutePlanResult(false, 0, null, null, null, null, null, name,
                fields.Length >= 4 ? string.Join("|", fields.Skip(3)) : null, segments, frames);
        }

        // No plan frame and no ERR: the seam had no opinion (card 31 §C additive) — never a plan.
        return new RoutePlanResult(false, 0, null, null, null, null, null, null, null, segments, frames);
    }
}

/// <summary>
/// The card-32 §A join-bootstrap answer the sidecar streamed for one <c>request_world</c>:
/// fingerprint verdict (reuse vs checkpoint — JOIN_REUSE), the eleven
/// <c>world_file_chunk</c> frames, the read-only primary pin, the <c>SIMC</c> claim
/// replay and the late-join history reset. Parsing is total: a missing family stays visible
/// as a zero/false so the controller log names the gap (never a silent drop).
/// </summary>
public sealed record SidecarJoinBootstrap(
    bool Reuse,
    string? Checkpoint,
    int ChunkFrames,
    string? PrimaryPin,
    int ClaimReplayFrames,
    bool HistoryReset)
{
    /// <summary>True when the answer carried the whole card-32 bootstrap family.</summary>
    public bool IsComplete =>
        (Reuse || Checkpoint is not null) && ChunkFrames == ExpectedChunks
        && PrimaryPin is not null && HistoryReset;

    /// <summary>The card-32 §A chunk count the daemon streams (its `WORLD_FILE_CHUNKS`).</summary>
    public const int ExpectedChunks = 11;

    public static SidecarJoinBootstrap Parse(IReadOnlyList<string> frames)
    {
        string? ack = frames.FirstOrDefault(f =>
            f.StartsWith("ACK|", StringComparison.Ordinal) && f.Contains("|RequestWorld|", StringComparison.Ordinal));
        int chunks = frames.Count(f => f.StartsWith("WFC|", StringComparison.Ordinal));
        string? primary = frames.FirstOrDefault(f => f.StartsWith("PRIMARY|", StringComparison.Ordinal));
        int replays = frames.Count(f => f.StartsWith("SIMC|", StringComparison.Ordinal));
        bool histReset = frames.Any(f => f.StartsWith("HISTRESET|", StringComparison.Ordinal));
        string? checkpoint = frames.FirstOrDefault(f => f.StartsWith("CHECKPOINT|", StringComparison.Ordinal));
        bool reuse = ack is not null && ack.Contains("|reuse|", StringComparison.Ordinal);
        return new SidecarJoinBootstrap(reuse, checkpoint, chunks, primary, replays, histReset);
    }
}

/// <summary>
/// The card 31 §A / card 33 mapping from a decoded public-protocol <see cref="ClientCommand"/>
/// to a sidecar authority verb. Only commands the kernel adjudicates map; everything else returns
/// null and runs the native path untouched. Client clicks arrive here already authenticated by
/// <c>ClientConnection.CheckCommandAuthority</c> (which therefore stays first), so the queue
/// producer rides as <c>server</c> — the autonomy-only families are still refused by the
/// daemon on the connection's <c>client</c> role tag. The four remaining deliverable verbs
/// (SwapClaim, ModifyFunds, RouteSlotClaim, SpawnService) have no public-protocol client command;
/// they enter through the sidecar's named forward methods on the bridge/admin path.
/// </summary>
public static class RustAuthorityForward
{
    /// <summary>The sidecar verb and args for one public command, or null when the seam has no verb for it.</summary>
    public static (string Verb, object[] Args)? TryBuild(ClientCommand command) => command switch
    {
        // The kernel's ClaimSim takes (sim, household); the public protocol carries only the Sim, so the
        // demo scope pins household = sim (one household per claimed Sim — the daemon's own demo cast).
        ClientCommand.ClaimSim c => ("ClaimSim", new object[] { ToKernelSimId(c.SimObjectId), ToKernelSimId(c.SimObjectId) }),
        ClientCommand.QueueInteraction q => ("EnqueueInteraction", new object[]
        {
            ToKernelSimId(q.Interaction.ActorObjectId),
            q.Interaction.InteractionKey,
            "server",
            q.Interaction.TargetObjectId is ulong target ? ToKernelSimId(target) : 0u,
            100,
        }),
        ClientCommand.AnswerDialog d => ("DialogAnswer", new object[] { d.RequestId, ChoiceOf(d.Answer) }),
        // PlayerRole.Server drives (the kernel's "player"); PlayerRole.Client mirrors (its "presenter").
        ClientCommand.SetClientRole r => ("SetClientRole", new object[]
        {
            r.Role == PlayerRole.Server ? "player" : "presenter",
        }),
        _ => null,
    };

    /// <summary>The kernel's SimId width is u32; a save-born ObjectGuid truncates into the demo id space.</summary>
    public static uint ToKernelSimId(ulong objectId) => unchecked((uint)objectId);

    /// <summary>'s answer text as the kernel's u32 choice (null/none = 0, else the leading integer).</summary>
    public static uint ChoiceOf(string? answer) =>
        answer is not null && uint.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out uint choice)
            ? choice
            : 0u;
}
