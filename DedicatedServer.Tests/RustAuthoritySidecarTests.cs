using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;

namespace DedicatedServer.Tests;

/// <summary>
/// Wave 7 · C# &#8596; Rust round-trip over the real <c>simlink_authority_daemon.exe</c>
/// (cards 31/32/33). Spawns the daemon on an ephemeral loopback port in an isolated
/// target-dir, drives <see cref="RustAuthoritySidecar"/> through the wire framing, the
/// CLIENT_ENV hello gate, bootstrap, accept, rejection, idempotent replay and the
/// verbatim KernelError relay, then asserts the authoritative frames.
/// Never touches TS3W.exe, never takes a stack claim, loopback only (SECURITY.md).
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RustAuthoritySidecarTests
{
    private Process _daemon = null!;
    private IPEndPoint _ep = null!;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimsOnline.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("SimsOnline.sln not found above the test assembly.");
    }

    private static string DaemonExe()
    {
        string repo = RepoRoot();
        string exe = OperatingSystem.IsWindows() ? "simlink_authority_daemon.exe" : "simlink_authority_daemon";
        var candidates = new List<string>();
        string? fromEnv = Environment.GetEnvironmentVariable("SIMSONLINE_SIDECAR_DAEMON");
        if (!string.IsNullOrEmpty(fromEnv)) candidates.Add(fromEnv);
        candidates.Add(Path.Combine(repo, "rust/sim96_kernel_cleanroom/target/wave7_daemon/release", exe));
        candidates.Add(Path.Combine(repo, "rust/sim96_kernel_cleanroom/target/release", exe));
        candidates.Add(Path.Combine(repo, "rust/sim96_kernel_cleanroom/target-sol/release", exe));
        foreach (string c in candidates)
            if (File.Exists(c)) return c;
        // Build into the isolated referee target-dir (never Worker A1/A2's target).
        string isolated = Path.Combine(repo, "rust/sim96_kernel_cleanroom/target/wave7_daemon");
        Directory.CreateDirectory(isolated);
        var psi = new ProcessStartInfo("cargo",
            "build --release --manifest-path \"" + Path.Combine(repo, "rust/sim96_kernel_cleanroom/Cargo.toml") +
            "\" --bin simlink_authority_daemon --target-dir \"" + isolated + "\"")
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        using (var b = Process.Start(psi)!) { b.WaitForExit(240_000); if (b.ExitCode != 0) throw new InvalidOperationException("cargo build daemon failed: " + b.StandardError.ReadToEnd()); }
        string built = Path.Combine(isolated, "release", exe);
        if (!File.Exists(built)) throw new FileNotFoundException("daemon not built/found", built);
        return built;
    }

    // A fresh daemon per test: the daemon assigns PlayerId by connection order and keeps
    // slot/sim state for its whole life, so isolation must come from a new process, not
    // a new socket. Each spawn is milliseconds and is killed in TearDown.
    [SetUp]
    public void StartDaemon()
    {
        Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvVar, "1");   // tests opt in explicitly
        _daemon = Process.Start(new ProcessStartInfo(DaemonExe(),
            "--bind 127.0.0.1 --port 0 --ticks 400000 --connections 64")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        string? line;
        while ((line = _daemon.StandardOutput.ReadLine()) is not null)
        {
            int i = line.IndexOf("listening on ", StringComparison.Ordinal);
            if (i >= 0)
            {
                string addr = line.Substring(i + "listening on ".Length).Trim();
                string host = addr.Substring(0, addr.LastIndexOf(':'));
                int port = int.Parse(addr.AsSpan(addr.LastIndexOf(':') + 1), CultureInfo.InvariantCulture);
                _ep = new IPEndPoint(IPAddress.Parse(host), port);
                return;
            }
        }
        throw new InvalidOperationException("daemon never reported a listening port: " + _daemon.StandardError.ReadToEnd());
    }

    [TearDown]
    public void StopDaemon()
    {
        try { if (!_daemon.HasExited) _daemon.Kill(entireProcessTree: true); } catch { /* gone */ }
        _daemon?.Dispose();
        Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvVar, null);
        Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvPortVar, null);
    }

    private static bool Any(IEnumerable<string> frames, string prefix) => frames.Any(f => f.StartsWith(prefix, StringComparison.Ordinal));
    private static string? Find(IEnumerable<string> frames, string prefix) => frames.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal));

    private RustAuthoritySidecar ConnectHelloBootstrap()
    {
        var s = RustAuthoritySidecar.TryCreate(_ep) ?? throw new InvalidOperationException("sidecar disabled: " + RustAuthoritySidecar.EnvVar + " not set");
        Assert.That(Any(s.Hello(), "WELCOME|"), Is.True, "a conforming CLIENT_ENV HELLO must yield WELCOME");
        var world = s.RequestWorld();
        Assert.That(Any(world, "CHECKPOINT|"), Is.True, "five-phase checkpoint frame");
        Assert.That(Any(world, "WFC|10|11|"), Is.True, "the 11th world_file_chunk frame");
        Assert.That(Any(world, "PRIMARY|100|1"), Is.True, "Martinez read-only primary pin");
        Assert.That(Any(world, "HISTRESET|"), Is.True, "late-join history reset");
        return s;
    }

    [Test]
    public void Sidecar_Is_Inert_Unless_Env_Enabled()
    {
        Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvVar, null);
        try { Assert.That(RustAuthoritySidecar.TryCreate(_ep), Is.Null, "default-off when SIMSONLINE_RUST_SIDECAR is unset"); }
        finally { Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvVar, "1"); }
    }

    [Test]
    public void ResolveEndpoint_Is_Loopback_34019_By_Default_And_Honors_The_Port_Env()
    {
        Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvPortVar, null);
        IPEndPoint byDefault = RustAuthoritySidecar.ResolveEndpoint();
        Assert.That(byDefault.Address, Is.EqualTo(IPAddress.Loopback), "card 31 §D: the sidecar endpoint is loopback-only");
        Assert.That(byDefault.Port, Is.EqualTo(RustAuthoritySidecar.DefaultPort), "the daemon's own default --port");

        Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvPortVar, "34567");
        Assert.That(RustAuthoritySidecar.ResolveEndpoint().Port, Is.EqualTo(34567), "SIMSONLINE_RUST_SIDECAR_PORT configures the port");

        foreach (string bad in new[] { "not-a-port", "0", "70000", "" })
        {
            Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvPortVar, bad);
            Assert.That(RustAuthoritySidecar.ResolveEndpoint().Port, Is.EqualTo(RustAuthoritySidecar.DefaultPort),
                $"an unusable override ({bad}) falls back to the default instead of failing the boot");
        }
        Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvPortVar, null);
    }

    [Test]
    public void TryOpen_Refuses_Non_Loopback_And_Fails_Open_On_A_Down_Daemon()
    {
        // Card 31 §D / SECURITY.md: a beyond-loopback endpoint is refused, not attempted.
        Assert.That(RustAuthoritySidecar.TryOpen(RustAuthoritySidecar.ClientEnv.Conforming(),
            new IPEndPoint(IPAddress.Parse("10.1.2.3"), 34019), TimeSpan.FromMilliseconds(200),
            out string? refusedRejection, out string? refusedFailure), Is.Null);
        Assert.That(refusedRejection, Is.Null);
        Assert.That(refusedFailure, Does.Contain("loopback-only"), "the host is never configurable (SECURITY.md)");

        // A wedged/absent daemon must produce a transport failure within the bound, never a throw
        // and never a blocked join (the C# mirror of dropped_ticks_during_join == 0, card 32 §C.1).
        int freePort;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var watch = Stopwatch.StartNew();
        Assert.That(RustAuthoritySidecar.TryOpen(RustAuthoritySidecar.ClientEnv.Conforming(),
            new IPEndPoint(IPAddress.Loopback, freePort), TimeSpan.FromMilliseconds(500),
            out string? downRejection, out string? downFailure), Is.Null);
        watch.Stop();
        Assert.That(downRejection, Is.Null, "no hello happened, so the kernel issued no rejection");
        Assert.That(downFailure, Is.Not.Null, "a transport failure is recorded, not swallowed");
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "bounded: a dead sidecar never stalls the caller");
    }

    [Test]
    public void Connect19_And_ClientEnv_Framing_Are_Graded_By_The_Hello_Gate()
    {
        using var s = RustAuthoritySidecar.TryCreate(_ep)!;
        // The card-19 CLIENT_ENV HELLO greets, then the CONNECT|19 compat line (card 31 row 1,
        // framing) is graded too — the daemon's state machine answers both with WELCOME.
        IReadOnlyList<string> conforming = s.Hello();
        Assert.That(Any(conforming, "WELCOME|1|"), Is.True, "the HELLO gate passes");
        Assert.That(Any(conforming, "WELCOME|0|"), Is.True, "the CONNECT|19 compat hello is graded once greeted");
        // A client without SIMSONLINE_SIMLINK_CLIENT_RING=1 is refused with the named reason,
        // verbatim (card 19 §C: a named diagnostic, never a bare reset) — card 32 §D row 1.
        IReadOnlyList<string> noRing = s.Hello(new RustAuthoritySidecar.ClientEnv(false, true, 17, 15, "client"));
        Assert.That(noRing, Does.Contain("ERR|1|hello_rejected|MissingSimlinkRing"));
        Assert.That(noRing, Has.None.StartsWith("WELCOME|"), "a refused gate must not also welcome");
        // Version mismatch is named too (card 32 §D row 2).
        Assert.That(s.Hello(new RustAuthoritySidecar.ClientEnv(true, true, 16, 15, "client")),
            Does.Contain("ERR|1|hello_rejected|WrongBridgeVersion"));
    }

    [Test]
    public void TryOpen_Attaches_Connect_Hello_Gated_For_The_Controller()
    {
        // The controller's lifecycle entry point: open + CLIENT_ENV hello in one bounded call.
        Assert.That(RustAuthoritySidecar.TryOpen(RustAuthoritySidecar.ClientEnv.Conforming(), _ep,
            TimeSpan.FromSeconds(2), out string? rejection, out string? failure), Is.Not.Null,
            $"a conforming controller posture attaches (rejection={rejection} failure={failure})");

        var unpatched = new RustAuthoritySidecar.ClientEnv(false, true, 17, 15, "client");
        Assert.That(RustAuthoritySidecar.TryOpen(unpatched, _ep, TimeSpan.FromSeconds(2),
            out rejection, out _), Is.Null);
        Assert.That(rejection, Is.EqualTo("hello_rejected|MissingSimlinkRing"),
            "the named gate refusal is handed to the caller verbatim for its CommandRejected");
    }

    [Test]
    public void RequestWorld_Bootstrap_Verdict_Parses_The_Card32_Families()
    {
        using var s = ConnectHelloBootstrap();
        SidecarJoinBootstrap first = SidecarJoinBootstrap.Parse(s.RequestWorld());
        Assert.That(first.Reuse, Is.False, "the demo fingerprint misses -> the checkpoint branch");
        Assert.That(first.Checkpoint, Is.EqualTo("CHECKPOINT|2|five_phase"), "card 11 five-phase FSM marker");
        Assert.That(first.ChunkFrames, Is.EqualTo(SidecarJoinBootstrap.ExpectedChunks), "eleven world_file_chunk frames");
        Assert.That(first.PrimaryPin, Is.EqualTo("PRIMARY|100|1"), "Martinez pin, read-only");
        Assert.That(first.HistoryReset, Is.True, "HISTRESET rides the bootstrap tail");
        Assert.That(first.IsComplete, Is.True);

        // Card 32 §A step 8: the claim set replays to a later bootstrap as SIMC lines. The
        // CommandId identity gives it a fresh, higher corr (the daemon's monotonic gate drops a
        // lower one; the fixed-corr RequestWorld() would only replay the first answer).
        Assert.That(s.ClaimSim(1, 100).Accepted, Is.True, "a claim the kernel adjudicates");
        SidecarJoinBootstrap replay = SidecarJoinBootstrap.Parse(s.RequestWorldFor(Guid.NewGuid()));
        Assert.That(replay.ClaimReplayFrames, Is.EqualTo(1), "one SIMC|1|100 claim replay frame");
    }

    [Test]
    public void Command_Accept_Path_Round_Trip()
    {
        using var a = ConnectHelloBootstrap();
        Assert.That(Any(a.Command("ClaimSim", 1, 100), "ACK|"), Is.True, "claim of a player-owned sim is accepted");
        Assert.That(Any(a.Command("EnqueueInteraction", 1, "SocializeWith", "server", 1, 100), "ACK|"), Is.True, "server-origin social accepted");
        Assert.That(Any(a.Command("ModifyFunds", 100, -500, "purchase"), "Hd|"), Is.True, "funds delta frame");
        Assert.That(Any(a.Command("DialogAnswer", 1, 1), "ACK|"), Is.True, "dialog answer accepted");
    }

    [Test]
    public void The_Seven_Kernel_Verbs_Forward_And_Relay_Ack_Deltas_And_Named_Errs()
    {
        using var a = ConnectHelloBootstrap();
        Assert.That(a.ClaimSim(1, 100).Accepted, Is.True, "ClaimSim -> ACK");
        Assert.That(a.EnqueueInteraction(1, "SocializeWith", "server", 0, 100).Accepted, Is.True, "EnqueueInteraction -> ACK");
        SidecarVerdict funds = a.ModifyFunds(100, -500, "purchase");
        Assert.That(funds.Accepted, Is.True, "ModifyFunds -> Hd delta");
        Assert.That(Any(funds.Frames, "Hd|"), Is.True, "the Hd ledger frame rides the verdict (never dropped)");
        Assert.That(a.RouteSlotClaim(1, 8, 0, 1).Accepted, Is.True, "RouteSlotClaim -> ACK");
        Assert.That(a.DialogAnswer(1, 1).Frames.Count, Is.GreaterThan(0), "DialogAnswer answers, ACK or named ERR");
        // last: after the swap the connection owns sim 3, not sim 1.
        Assert.That(a.SwapClaim(1, 3).Accepted, Is.True, "SwapClaim -> ACK");
        SidecarVerdict spawn = a.SpawnService("Maid");
        Assert.That(spawn.IsAuthoritativeRejection, Is.True, "a client-role SpawnService is the suppression");
        Assert.That(spawn.RejectionName, Is.EqualTo("ClientWorldServiceSuppressed"), "the exact KernelError name");
        Assert.That(spawn.RejectionText, Does.Contain("Maid"), "and its detail, verbatim");
    }

    [Test]
    public void Unauthorized_And_Suppressed_Rejections_Are_Named()
    {
        using var a = ConnectHelloBootstrap();
        string? bed = Find(a.Command("EnqueueInteraction", 1, "BedSleep", "client", 0, 100), "ERR|");
        Assert.That(bed, Is.Not.Null, "client-origin BedSleep rejected");
        Assert.That(bed, Does.Contain("UnauthorizedClientInsertion"), "named, never silent");
        Assert.That(Find(a.Command("EnqueueInteraction", 1, "RouteAway", "client", 0, 100), "ERR|"),
            Does.Contain("UnauthorizedClientInsertion"), "client-origin RouteAway rejected");
        Assert.That(Find(a.Command("SpawnService", "Maid"), "ERR|"),
            Does.Contain("ClientWorldServiceSuppressed"), "client world-service spawn suppressed");
    }

    [Test]
    public void Verdict_Classification_Relays_The_Exact_KernelError_Names()
    {
        // Card 33 §C + card 31 §E: these five names block the native path and reach the client verbatim.
        foreach (string name in new[]
                 {
                     "UnauthorizedClientInsertion", "ClientWorldServiceSuppressed", "SlotAlreadyReserved",
                     "native_oracle_owned", "ClientReselectForbidden",
                 })
        {
            SidecarVerdict v = SidecarVerdict.Classify(new[] { $"ERR|7|{name}|detail-x" });
            Assert.That(v.IsAuthoritativeRejection, Is.True, $"{name} is a structured rejection");
            Assert.That(v.RejectionText, Is.EqualTo($"ERR|{name}|detail-x"), "relayed verbatim");
        }

        // StaleObjectHandle is a controller-log fact only (card 31 §E row 4) — never client-facing.
        Assert.That(RustAuthoritySidecar.AuthoritativeRejections, Has.No.Member("StaleObjectHandle"));

        // A delta-only answer is accepted; an unmodelled answer is no opinion, not a rejection.
        Assert.That(SidecarVerdict.Classify(new[] { "ACK|7|RouteSlotClaim|1|8|0|tick=3", "Rl|7|" }).Accepted, Is.True);
        SidecarVerdict unmodelled = SidecarVerdict.Classify(new[] { "ERR|7|bad_line|interact" });
        Assert.That(unmodelled.IsAuthoritativeRejection, Is.False, "the additive seam must not invent authority (card 31 §C)");
        Assert.That(unmodelled.Accepted, Is.False);
    }

    [Test]
    public void Public_Commands_Map_To_Kernel_Verbs()
    {
        Guid id = Guid.NewGuid();

        var claim = RustAuthorityForward.TryBuild(new ClientCommand.ClaimSim(id, 1, 42));
        Assert.That(claim!.Value.Verb, Is.EqualTo("ClaimSim"));
        Assert.That(claim!.Value.Args.Select(a => a.ToString()), Is.EqualTo(new[] { "42", "42" }),
            "demo scope pins household = sim");

        var eq = RustAuthorityForward.TryBuild(new ClientCommand.QueueInteraction(id, 1,
            new InteractionRequest(7, "SocializeWith", 8, null, new Dictionary<string, string>())));
        Assert.That(eq!.Value.Verb, Is.EqualTo("EnqueueInteraction"));
        Assert.That(eq!.Value.Args.Select(a => a.ToString()), Is.EqualTo(new[] { "7", "SocializeWith", "server", "8", "100" }),
            "a CheckCommandAuthority-passed player click is the kernel's server-origin producer");

        var noTarget = RustAuthorityForward.TryBuild(new ClientCommand.QueueInteraction(id, 1,
            new InteractionRequest(7, "GoHome", null, null, new Dictionary<string, string>())));
        Assert.That(noTarget!.Value.Args.Select(a => a.ToString()), Is.EqualTo(new[] { "7", "GoHome", "server", "0", "100" }));

        var dialog = RustAuthorityForward.TryBuild(new ClientCommand.AnswerDialog(id, 1, 5, 9, "2"));
        Assert.That(dialog!.Value.Verb, Is.EqualTo("DialogAnswer"));
        Assert.That(dialog!.Value.Args.Select(a => a.ToString()), Is.EqualTo(new[] { "5", "2" }));

        var player = RustAuthorityForward.TryBuild(new ClientCommand.SetClientRole(id, 1, PlayerRole.Server));
        Assert.That(player!.Value.Verb, Is.EqualTo("SetClientRole"));
        Assert.That(player!.Value.Args, Is.EqualTo(new object[] { "player" }), "DEV-75 Server drives = the kernel's player");
        var presenter = RustAuthorityForward.TryBuild(new ClientCommand.SetClientRole(id, 1, PlayerRole.Client));
        Assert.That(presenter!.Value.Args, Is.EqualTo(new object[] { "presenter" }), "Client mirrors = the kernel's presenter");

        // Everything else keeps the native path untouched (the seam is additive).
        Assert.That(RustAuthorityForward.TryBuild(new ClientCommand.LeaveSession(id, 1)), Is.Null);
        Assert.That(RustAuthorityForward.TryBuild(new ClientCommand.LeaveConversation(id, 1, 7)), Is.Null, "leave_conversation rides the native path");
        Assert.That(RustAuthorityForward.TryBuild(new ClientCommand.RequestWorld(id, 1)), Is.Null, "request_world rides the bootstrap consult");
        Assert.That(RustAuthorityForward.TryBuild(new ClientCommand.RequestSnapshot(id, 1, null)), Is.Null);
    }

    [Test]
    public void ForwardCommand_Corrs_Grow_Monotonically_And_Replay_By_CommandId()
    {
        // The daemon's corr gate is monotonic per player (wire.rs accept_command drops any corr
        // <= last seen, no reply), so ForwardCommand must: fresh CommandIds take strictly
        // increasing corrs (every command is heard), and a RETRIED CommandId rides its stored
        // corr (the replay path) -- the two properties gate 11's reclaim proof rests on.
        using var a = ConnectHelloBootstrap();
        Assert.That(a.ClaimSim(1, 100).Accepted, Is.True, "claim first so the funds path is owned");
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Assert.That(a.ForwardCommand(first, "ModifyFunds", 100, -100, "purchase").Accepted, Is.True,
            "a fresh CommandId's corr clears the monotonic gate");
        Assert.That(a.ForwardCommand(second, "ModifyFunds", 100, -100, "sale").Accepted, Is.True,
            "the next fresh CommandId's corr is higher, not dropped");
        SidecarVerdict original = a.ForwardCommand(second, "ModifyFunds", 100, -100, "sale");
        Assert.That(original.RejectionName, Is.Null, "a retry of the same CommandId never errors");
        Assert.That(Any(original.Frames, "Hd|"), Is.True, "and replays the ORIGINAL Hd row, no second effect");
    }

    [Test]
    public void Sim_Claimed_On_One_Attachment_Is_Refused_To_Another()
    {
        // The daemon's ownership is per sidecar connection -- this is exactly why the controller
        // keeps ONE attachment PER PLAYER across reconnects (card 35 gate 11): a reclaim that
        // arrived on a new attachment would meet this rejection for its own Sim.
        using var a = ConnectHelloBootstrap();
        using var b = ConnectHelloBootstrap();
        Assert.That(a.ClaimSim(1, 100).Accepted, Is.True, "A claims sim 1");
        SidecarVerdict reclaim = b.ClaimSim(1, 100);
        Assert.That(reclaim.IsAuthoritativeRejection, Is.True, "a different daemon player cannot take it");
        Assert.That(reclaim.RejectionName, Is.EqualTo("UnauthorizedClientInsertion"), "named, verbatim");
    }

    [Test]
    public void Slot_Conflict_Between_Two_Clients_Is_Refused()
    {
        using var a = ConnectHelloBootstrap();
        using var b = ConnectHelloBootstrap();
        Assert.That(Any(a.Command("ClaimSim", 1, 100), "ACK|"), Is.True, "A claims");
        Assert.That(Any(b.Command("ClaimSim", 2, 200), "ACK|"), Is.True, "B claims");
        Assert.That(Any(a.Command("RouteSlotClaim", 1, 8, 0, 1), "ACK|"), Is.True, "A reserves slot 8");
        Assert.That(Any(b.Command("RouteSlotClaim", 2, 8, 0, 1), "ERR|"), Is.True, "B's conflicting reservation refused (card 33 §C)");
    }

    [Test]
    public void Duplicate_CommandId_Replays_The_Original_Result()
    {
        using var a = ConnectHelloBootstrap();
        Assert.That(Any(a.Command("ClaimSim", 1, 100), "ACK|"), Is.True, "claim so player1 exists");
        // One CommandId sent twice (what a reconnecting client's retry becomes): both sends ride
        // the SAME corr (ForwardCommand's CommandId map) and return the ORIGINAL ACK -- one
        // reservation, no second effect, no clash rejection (card 33 §D.2 + card 35 gate 11).
        Guid dup = Guid.NewGuid();
        SidecarVerdict first = a.ForwardCommand(dup, "RouteSlotClaim", 1, 9, 0, 1);
        Assert.That(first.Accepted, Is.True, "original accepted");
        string ack = first.Frames.First(f => f.StartsWith("ACK|", StringComparison.Ordinal)
                                             && f.Contains("|RouteSlotClaim|", StringComparison.Ordinal));
        uint corr = uint.Parse(ack.Split('|')[1], System.Globalization.CultureInfo.InvariantCulture);
        SidecarVerdict replay = a.ForwardCommand(dup, "RouteSlotClaim", 1, 9, 0, 1);
        Assert.That(replay.Frames.Count(f => f.StartsWith("ACK|" + corr + "|", StringComparison.Ordinal)),
            Is.EqualTo(1), "replay returns the original result, not a second effect");
        Assert.That(replay.Frames.Any(f => f.StartsWith("ERR|" + corr + "|", StringComparison.Ordinal)),
            Is.False, "a duplicate must not become a second-clash rejection");
    }

    [Test]
    public void Sacs_Seeds_Arrive_Over_The_Live_Socket()
    {
        using var a = ConnectHelloBootstrap();
        // PONG fences can all return before the daemon's independently scheduled tick.
        Assert.That(() => Any(a.Batch(""), "AS|"), Is.True.After(2_000, 10),
            "the tick pump streams AS SACS-seed lines to the C# bridge");
    }

    // ---- Wave 11 · cards 51/54: the NavigateAStar seam ------------------------------------------
    // The kernel side (the clean-room replanner, card 30 row 1) is the Rust lane's; these pin the
    // C#/bridge half: the exact request framing, the corr identity rule, and total parsing of the
    // plan ACK + Rt legs against every named refusal — before and after the cutover.

    [Test]
    public void PlanRouteAStar_Never_Invents_A_Plan_And_Never_Answers_Silently()
    {
        const ulong simId = 1;
        using var a = ConnectHelloBootstrap();
        Assert.That(a.ClaimSim(simId, 100).Accepted, Is.True, "claim the demo Sim to reach the planner");

        // Cell 9999 is outside the demo navmesh and 1024 is a valid goal.
        RoutePlanResult r = a.PlanRouteAStar(1, simId, 9999, 1024);
        Assert.That(r.IsStructuredAnswer, Is.True, $"a fenced route request must answer ACK or named ERR, got [{string.Join(" ", r.Frames)}]");
        Assert.That(r.Planned, Is.False, "an invalid start must never produce a plan");
        Assert.That(r.RejectionName, Is.EqualTo("RouteStartOutOfBounds"));
        Assert.That(r.IsRouteRejection, Is.True);
    }

    [Test]
    public void PlanRouteAStar_Rides_One_Corr_Per_Command_Identity_And_Replays_On_A_Retry()
    {
        // Card 33 §D.2 applied to routing: the corr comes from the command id, not from the send, so a
        // retry of one request replays the kernel's original answer instead of falling under the
        // daemon's monotonic gate (which would drop it and leave the caller with nothing).
        using var a = ConnectHelloBootstrap();
        RoutePlanResult first = a.PlanRouteAStar(7001, 1, 0, 12);
        RoutePlanResult retry = a.PlanRouteAStar(7001, 1, 0, 12);
        uint CorrOf(RoutePlanResult r) =>
            uint.Parse(r.Frames.First(f => f.StartsWith("ERR|", StringComparison.Ordinal)).Split('|')[1],
                CultureInfo.InvariantCulture);
        Assert.That(first.IsStructuredAnswer && retry.IsStructuredAnswer, Is.True, "both sends answered");
        Assert.That(CorrOf(retry), Is.EqualTo(CorrOf(first)),
            "the same command id rides the same corr — the replay path, not a second request");
    }

    [Test]
    public void PlanRouteAStar_Distinct_Commands_All_Clear_The_Monotonic_Corr_Gate()
    {
        // Each new command id must take a HIGHER corr; a repeat/lower one is dropped with no reply,
        // which shows up here as an unstructured (empty) answer.
        using var a = ConnectHelloBootstrap();
        var corrs = new List<uint>();
        for (ulong id = 1; id <= 5; id++)
        {
            RoutePlanResult r = a.PlanRouteAStar(id, 1, 0, 1);
            Assert.That(r.IsStructuredAnswer, Is.True, $"request {id} got an answer (a dropped corr gets none)");
            corrs.Add(uint.Parse(r.Frames.First(f => f.StartsWith("ERR|", StringComparison.Ordinal)).Split('|')[1],
                CultureInfo.InvariantCulture));
        }
        Assert.That(corrs.Distinct().Count(), Is.EqualTo(5), "five distinct corrs");
        Assert.That(corrs, Is.EqualTo(corrs.Order().ToList()), "and strictly increasing");
    }

    [Test]
    public void NavigateAStar_Line_Is_Exactly_Card_54_Section_C()
    {
        // The wire bytes, pinned: CMD|corr|NavigateAStar|<sim(u32)>|<start>|<goal>|<flags> + the line
        // feed. A save-born object id narrows the way every forwarded id does, so the kernel's u32
        // SimId never arrives as 64 bits.
        Assert.That(RustAuthoritySidecar.NavigateAStarLine(1234u, 42ul, 7u, 19u, 0u),
            Is.EqualTo("CMD|1234|NavigateAStar|42|7|19|0\n"));
        ulong saveBorn = 14167198646267834864ul;
        Assert.That(RustAuthoritySidecar.NavigateAStarLine(9u, saveBorn, 1u, 2u, 1u),
            Is.EqualTo($"CMD|9|NavigateAStar|{RustAuthorityForward.ToKernelSimId(saveBorn)}|1|2|1\n"),
            "the sim id is the kernel's u32 width, the flags tail is present");
    }

    [Test]
    public void RoutePlanResult_Parses_The_Card_54_Rt_Plan_And_Its_Vp_Segments()
    {
        var frames = new[]
        {
            "Rt|1000000001|42|3|a81d47c9beef0011|41250",
            "Vp|9001|14167198646267834864|0|1270.4,49.7,1208.6|1271.0,49.7,1210.2",
            "Vp|9002|14167198646267834864|1|1271.0,49.7,1210.2|1273.8,49.7,1214.9",
            "Vp|9003|14167198646267834864|2|1273.8,49.7,1214.9|1275.2,49.7,1218.4",
        };
        RoutePlanResult r = RoutePlanResult.Parse(frames);
        Assert.That(r.Planned, Is.True);
        Assert.That(r.Corr, Is.EqualTo(1000000001u), "the same corr rides back (card 54 §D)");
        Assert.That(r.MatchesCorr(1000000001u), Is.True);
        Assert.That(r.SimId, Is.EqualTo(42ul));
        Assert.That(r.LegCount, Is.EqualTo(3u));
        Assert.That(r.PathHash, Is.EqualTo("a81d47c9beef0011"), "the 16-hex plan identity card 55 E3 compares");
        Assert.That(r.TotalCostMm, Is.EqualTo(41250ul), "millimetres, unsigned");
        Assert.That(r.SegmentFrames.Count, Is.EqualTo(3), "every Vp segment frame, in arrival order; the Rt itself is the plan, not geometry");
        Assert.That(r.HasPlanIdentity, Is.True);
        Assert.That(r.RejectionName, Is.Null);
    }

    [Test]
    public void RoutePlanResult_Rejects_The_Bridge_Rt_Shape_As_Bad_Line_Not_A_Wrong_Route()
    {
        // Card 54 §C.1: `Rt` already means something on the mod's memory bridge
        // (Rt|sim|destX|destY|destZ|facing|tag|destSlot|destObj). A frame that arrives with that shape
        // must become a named bad_line, never a plan read out of the wrong columns.
        RoutePlanResult wrongShape = RoutePlanResult.Parse(
            new[] { "Rt|14167198646267834880|1270.5|49.7|1209.0|0.62|4|0|11539349196871923504" });
        Assert.That(wrongShape.Planned, Is.False, "nine fields is not a plan");
        Assert.That(wrongShape.IsMalformedRouteFrame, Is.True, "named bad_line|Rt... with the field count");
        Assert.That(wrongShape.RejectionDetail, Does.Contain("fieldCount=9"));

        // A six-field frame whose hash column is not 16 hex is the same failure class.
        Assert.That(RoutePlanResult.Parse(new[] { "Rt|5|42|3|a81d47c9|41250" }).IsMalformedRouteFrame, Is.True,
            "the brief's 8-hex hash is not the card-54 16-hex path_hash");
    }

    [Test]
    public void RoutePlanResult_Parses_The_Brief_s_ACK_Form_Into_The_Same_Fields()
    {
        RoutePlanResult r = RoutePlanResult.Parse(
            new[] { "ACK|77|NavigateAStar|legs=3|cost_mm=41250|hash=a81d47c9|tick=142523" });
        Assert.That(r.Planned, Is.True);
        Assert.That(r.Corr, Is.EqualTo(77u));
        Assert.That(r.LegCount, Is.EqualTo(3u));
        Assert.That(r.TotalCostMm, Is.EqualTo(41250ul));
        Assert.That(r.PathHash, Is.EqualTo("a81d47c9"));
        Assert.That(r.Tick, Is.EqualTo(142523ul));
        // The generic verdict path agrees: a route ACK is an accepted answer, not "no opinion".
        Assert.That(SidecarVerdict.Classify(new[] { "ACK|77|NavigateAStar|legs=1" }).Accepted, Is.True);
    }

    [Test]
    public void RoutePlanResult_Parsing_Is_Total_Order_Insensitive_And_Never_Zero_Substitutes()
    {
        // key=value parts may arrive in any order; a part the kernel did not send stays null so the
        // controller log names the gap (the SidecarJoinBootstrap rule, card 32).
        RoutePlanResult reordered = RoutePlanResult.Parse(
            new[] { "ACK|5|NavigateAStar|tick=99|hash=beef|cost_mm=12|legs=2" });
        Assert.That(reordered.Tick, Is.EqualTo(99ul));
        Assert.That(reordered.PathHash, Is.EqualTo("beef"));
        Assert.That(reordered.TotalCostMm, Is.EqualTo(12ul));
        Assert.That(reordered.LegCount, Is.EqualTo(2u));

        RoutePlanResult partial = RoutePlanResult.Parse(new[] { "ACK|5|NavigateAStar|legs=4|tick=99" });
        Assert.That(partial.Planned, Is.True, "a plan is still a plan");
        Assert.That(partial.LegCount, Is.EqualTo(4u));
        Assert.That(partial.TotalCostMm, Is.Null, "cost_mm absent stays null, not 0");
        Assert.That(partial.PathHash, Is.Null, "hash absent stays null, not \"\"");
        Assert.That(partial.HasPlanIdentity, Is.False, "an incomplete identity is not comparable (card 55 E3)");

        Assert.That(RoutePlanResult.Parse(new[] { "ACK|5|NavigateAStar|legs=abc" }).LegCount, Is.Null,
            "an unparseable value is a gap, not a guess");
    }

    [Test]
    public void RoutePlanResult_Classifies_Every_Card_54_Rejection()
    {
        foreach (string name in new[] { "RouteStartOutOfBounds", "RouteGoalUnreachable", "PortalLocked", "FormatUnsupported" })
        {
            RoutePlanResult r = RoutePlanResult.Parse(new[] { $"ERR|9|{name}|7" });
            Assert.That(r.IsRouteRejection, Is.True, $"{name} is card 54 §C's named refusal");
            Assert.That(r.Planned, Is.False);
            Assert.That(r.RejectionDetail, Is.EqualTo("7"), "and its detail rides back");
        }

        // The two shapes the seam answers with today, named apart (runtime_bridge.rs AUTHORITY_ROUTES
        // still owns the verb for the native oracle; the CMD arm has no case yet).
        Assert.That(RoutePlanResult.Parse(new[] { "ERR|0|native_oracle_owned|NavigateAStar" }).IsNativeOracleRefusal, Is.True);
        Assert.That(RoutePlanResult.Parse(new[] { "ERR|9|bad_line|NavigateAStar" }).IsKernelPending, Is.True);

        // Delta frames with no plan and no ERR are not a verdict.
        RoutePlanResult opinionless = RoutePlanResult.Parse(new[] { "Vp|9|14167198646267834880|0|0,0,0|1,1,1" });
        Assert.That(opinionless.Planned, Is.False, "no Rt, no plan");
        Assert.That(opinionless.IsStructuredAnswer, Is.False, "no ERR either");
        Assert.That(opinionless.SegmentFrames.Count, Is.EqualTo(1), "but the segments are still carried");
    }

    [Test]
    public void Route_Refusals_Are_Not_Yet_Part_Of_The_Client_Relay_Contract()
    {
        // Card 33 §C's set is what reaches a client verbatim. Card 54 §C.1 makes promoting these a
        // both-sides-plus-docs change (DedicatedServer/Protocol/, SimsOnline.Client/, docs/protocol.md
        // together), so this pins today's boundary rather than crossing it unilaterally.
        foreach (string name in RoutePlanResult.RouteRejections)
            Assert.That(RustAuthoritySidecar.AuthoritativeRejections, Has.No.Member(name),
                $"{name} is parsed by the route seam but not yet relayed to clients");
        Assert.That(RoutePlanResult.RouteRejections, Is.SupersetOf(new[]
        {
            "RouteStartOutOfBounds", "RouteGoalUnreachable", "PortalLocked", "FormatUnsupported",
        }));
    }
}

/// <summary>
/// Card 33 §C silent-drop remediation (follow-up forensics §4): every refusal the public listener
/// makes — unjoined player, unknown type, truncated JSON, duplicate join — must answer with a
/// named event carrying the client's own commandId, never a silent void. These run a REAL
/// <see cref="ClientListenerServer"/> on an ephemeral loopback port over a fake host (no TS3),
/// the same harness the listener smoke uses, and read the wire like a client does.
/// </summary>
[TestFixture]
public sealed class SidecarCommandReplyTests
{
    private sealed class FakeReplyHost : IAuthoritativeSimulationHost
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task LoadSaveAsync(string s, CancellationToken ct) => Task.CompletedTask;
        public Task LoadSaveViaMenuAsync(string s, CancellationToken ct) => Task.CompletedTask;
        public Task SaveGameAsync(string s, CancellationToken ct) => Task.CompletedTask;
        public Task MergeLotStateAsync(SimsOnline.DedicatedServer.Protocol.LotExportBundle bundle, CancellationToken ct) => Task.CompletedTask;
        public Task<CommandResult> ApplyCommandAsync(ClientCommand c, CancellationToken ct) =>
            Task.FromResult(CommandResult.Accepted(c.CommandId));
        public Task<SimulationSnapshot> GetSnapshotAsync(CancellationToken ct) => Snapshot(ct);
        public Task<SimulationSnapshot> GetSnapshotSinceAsync(long since, CancellationToken ct) => Snapshot(ct);
        private static Task<SimulationSnapshot> Snapshot(CancellationToken ct) => Task.FromResult(
            new SimulationSnapshot(1L, "ReplyWorld", "Reply.sims3", GameSpeed.Normal, new List<SimSnapshot>(), Array.Empty<ObjectSnapshot>()));
        public Task SetServerAutonomyAsync(ulong sim, bool disabled, CancellationToken ct) => Task.CompletedTask;
        public Task RegisterAutonomyGateAsync(ulong sim, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAutonomyGateAsync(ulong sim, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>One listener + session + raw client socket, disposed with the test.</summary>
    private static async Task<(AuthoritativeSession session, ClientListenerServer listener, TcpClient client, StreamReader reader, StreamWriter writer)> AttachAsync()
    {
        var session = new AuthoritativeSession(new FakeReplyHost());
        await session.StartAsync("Reply.sims3", CancellationToken.None);
        var listener = new ClientListenerServer(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.BoundEndpoint.Port, CancellationToken.None);
        var stream = client.GetStream();
        return (session, listener, client,
            new StreamReader(stream, Encoding.UTF8), new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true });
    }

    private static async Task<string> ReadLineTimedAsync(StreamReader reader)
    {
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8))
            ?? throw new IOException("connection closed before a reply");
    }

    /// <summary>Read events until one mentions the id (skipping whatever else arrives first).</summary>
    private static async Task<string> ReadUntilAsync(StreamReader reader, Guid id)
    {
        for (int i = 0; i < 12; i++)
        {
            string line = await ReadLineTimedAsync(reader);
            if (line.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase)
                || line.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }
        throw new NUnit.Framework.AssertionException("12 events arrived and none carried the commandId");
    }

    [Test]
    public async Task UnjoinedClaim_AnswersNamedCommandRejected_CarryingTheCommandId()
    {
        var (_, listener, client, reader, writer) = await AttachAsync();
        try
        {
            Guid id = Guid.NewGuid();
            await writer.WriteAsync(PublicProtocolCodec.EncodeCommand(
                new ClientCommand.ClaimSim(id, 77, 4242)) + "\n");
            string reply = await ReadUntilAsync(reader, id);
            Assert.That(reply, Does.Contain("command_rejected"), "the refusal is named and seen");
            Assert.That(reply, Does.Contain("not joined"), "with a clear reason");
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Test]
    public async Task DuplicateJoin_AnswersCommandRejected_CarryingTheCommandId()
    {
        var (_, listener, client, reader, writer) = await AttachAsync();
        try
        {
            await writer.WriteAsync(PublicProtocolCodec.EncodeCommand(
                new ClientCommand.JoinSession(Guid.NewGuid(), 3, "first")) + "\n");
            Guid second = Guid.NewGuid();
            await writer.WriteAsync(PublicProtocolCodec.EncodeCommand(
                new ClientCommand.JoinSession(second, 4, "again")) + "\n");
            // The first join's accepted + session_joined + snapshot may arrive first; correlate by id.
            string reply = await ReadUntilAsync(reader, second);
            Assert.That(reply, Does.Contain("command_rejected"));
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Test]
    public async Task TruncatedCommand_AnswersCommandRejected_CarryingTheCommandId()
    {
        // This case is the hardening itself: before it the client only ever saw a generic
        // server_error (or nothing it could correlate), which the forensics read as a silent drop.
        var (_, listener, client, reader, writer) = await AttachAsync();
        try
        {
            Guid id = Guid.NewGuid();
            await writer.WriteAsync("{\"$type\":\"claim_sim\",\"commandId\":\"" + id +
                "\",\"playerId\":77,\"simObjectId\":4242\n"); // truncated: no closing brace
            string reply = await ReadUntilAsync(reader, id);
            Assert.That(reply, Does.Contain("command_rejected"), "a malformed line with a recoverable id is still answered");
            Assert.That(reply, Does.Contain("Malformed"), "and named as such");
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Test]
    public async Task UnknownTypeCommand_AnswersCommandRejected_CarryingTheCommandId()
    {
        var (_, listener, client, reader, writer) = await AttachAsync();
        try
        {
            Guid id = Guid.NewGuid();
            await writer.WriteAsync("{\"$type\":\"launch_the_missiles\",\"commandId\":\"" + id +
                "\",\"playerId\":77}\n");
            string reply = await ReadUntilAsync(reader, id);
            Assert.That(reply, Does.Contain("command_rejected"));
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Test]
    public async Task IdlessGarbage_StillAnswersServerError_NeverSilence()
    {
        var (_, listener, client, reader, writer) = await AttachAsync();
        try
        {
            await writer.WriteAsync("not json at all\n");
            string reply = await ReadLineTimedAsync(reader);
            Assert.That(reply, Does.Contain("server_error"), "no id to correlate on — still a named, seen refusal");
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }
}

/// <summary>Card 57 gate 4: the five reply contracts stay five; this guard is outside the pinned fixture.</summary>
[TestFixture]
public sealed class SidecarCommandReplyTestsCountTests
{
    [Test]
    public void ReplyContractFixture_HasExactlyFiveTests()
    {
        var methods = typeof(SidecarCommandReplyTests).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly);
        int count = methods.Count(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length != 0);
        Assert.That(count, Is.EqualTo(5), "Card 57 §H row 4 pins five reply contracts, not the broad Sidecar filter.");
    }
}
