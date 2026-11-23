using System.Diagnostics;
using System.Runtime.InteropServices;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Mirroring;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// n40: HeadlessSims3Host runs the physical fork census before it launches or attaches TS3, over the profiles
/// stackctl.launch_fork_census covers. Every case uses a fake census, a fake fork_census module in a temp checkout,
/// or a TS3 path that cannot start a game; none reads a real profile.
/// </summary>
internal static partial class Program
{
    private static readonly string MissingTs3 = Path.Combine(Path.GetTempPath(), "simsonline-fork-census-test", "TS3W.exe");

    [Test("ForkCensus.LaunchRefusesUnknownForkBeforeLaunch")]
    private static void ForkCensusLaunchRefusesUnknownForkBeforeLaunch()
    {
        List<ForkCensusScope> scopes = new();
        Exception? error = StartWithCensus(new[] { "--ts3", MissingTs3, "--ts3-sandboxie-box", "Client2Box" }, scope =>
        {
            scopes.Add(scope);
            return new ForkCensusResult("unknown", "fork: state=unknown selected=cold-test-1");
        }, noForkOk: "1");
        Assert(error is ForkCensusException && error.Message.StartsWith("FORK-UNSAFE:", StringComparison.Ordinal),
            $"An unknown fork must refuse with FORK-UNSAFE before the TS3 path is even checked, override or not; got {Describe(error)}.");
        Assert(scopes.Count == 1 && scopes[0].Boxes.SequenceEqual(new[] { "Client2Box" }) && scopes[0].WinePrefixes.Count == 0 &&
               scopes[0].ClientSaves == "", "The census must cover the box the TS3 launches in.");
    }

    [Test("ForkCensus.StockNeedsExplicitOverride")]
    private static void ForkCensusStockNeedsExplicitOverride()
    {
        Func<ForkCensusScope, ForkCensusResult> stock = _ => new ForkCensusResult("stock", "fork: state=stock selected=none");
        Exception? refused = StartWithCensus(new[] { "--ts3", MissingTs3 }, stock, noForkOk: null);
        Assert(refused is ForkCensusException && refused.Message.StartsWith("FORK-MISSING:", StringComparison.Ordinal),
            $"A missing fork must refuse with FORK-MISSING; got {Describe(refused)}.");

        Exception? allowed = StartWithCensus(new[] { "--ts3", MissingTs3 }, stock, noForkOk: "1");
        Assert(allowed is FileNotFoundException,
            $"SIMSONLINE_NO_FORK_OK=1 must let a stock census through to the launch (missing TS3 here); got {Describe(allowed)}.");

        Exception? fork = StartWithCensus(new[] { "--ts3", MissingTs3 }, _ => new ForkCensusResult("fork", "fork: state=fork"), noForkOk: null);
        Assert(fork is FileNotFoundException, $"An installed fork must reach the launch; got {Describe(fork)}.");
    }

    [Test("ForkCensus.CensusFailureFailsClosed")]
    private static void ForkCensusCensusFailureFailsClosed()
    {
        Exception? error = StartWithCensus(new[] { "--ts3", MissingTs3 },
            _ => throw new System.ComponentModel.Win32Exception("python not found"), noForkOk: "1");
        Assert(error is ForkCensusException && error.Message.StartsWith("FORK-UNSAFE:", StringComparison.Ordinal) &&
               error.Message.Contains("python not found", StringComparison.Ordinal),
            $"A census that cannot run must refuse, override or not; got {Describe(error)}.");
    }

    [Test("ForkCensus.AttachScopeCoversTheAttachedProfile")]
    private static void ForkCensusAttachScopeCoversTheAttachedProfile()
    {
        SessionConfig server = SessionConfig.FromArgs(new[] { "--attach-existing", "--attach-pid", "4242" });
        ForkCensusScope native = ForkCensusScope.For(server, AttachedProfile.Native);
        Assert(native.Boxes.Count == 0 && native.WinePrefixes.Count == 0 && native.ClientSaves == "",
            "An unsandboxed server attach has stackctl's server scope (server profile only).");

        ForkCensusScope boxed = ForkCensusScope.For(server, new AttachedProfile("Client2Box", null));
        Assert(boxed.Boxes.SequenceEqual(new[] { "Client2Box" }),
            "A server attach to a sandboxed TS3 must add that box's profile, the one the attached game reads.");

        ForkCensusScope wine = ForkCensusScope.For(server, new AttachedProfile(null, "/home/user/.sims3-client/"));
        Assert(wine.WinePrefixes.SequenceEqual(new[] { "/home/user/.sims3-client" }), "A Wine attach adds the target's prefix.");

        SessionConfig client = ClientCensusConfig(box: "Client3Box", attachPid: 4242, saves: null);
        AssertRefused(() => ForkCensusScope.For(client, new AttachedProfile("Client2Box", null)), "an attach target in another box");
        AssertRefused(() => ForkCensusScope.For(client, AttachedProfile.Native), "an unsandboxed target for a boxed client");
        AssertRefused(() => ForkCensusScope.For(server, new AttachedProfile(null, null, "driver said no")), "a target whose profile is unknown");
        Assert(ForkCensusScope.For(client, new AttachedProfile("client3box", null)).Boxes.SequenceEqual(new[] { "Client3Box" }),
            "Box names compare like Sandboxie's (case-insensitively).");
    }

    [Test("ForkCensus.ClientScopeMatchesStackctl")]
    private static void ForkCensusClientScopeMatchesStackctl()
    {
        // stackctl.launch_fork_census for a client: boxes=[--client-ts3-sandboxie-box], wineprefixes=[--client-ts3-wineprefix],
        // client_saves=--client-saves-dir or "".
        string saves = Path.Combine("C:", "Sandbox", "u", "Client2Box", "Saves");
        ForkCensusScope launch = ForkCensusScope.For(ClientCensusConfig(box: "Client2Box", attachPid: null, saves: saves), null);
        Assert(launch.Boxes.SequenceEqual(new[] { "Client2Box" }) && launch.WinePrefixes.Count == 0 && launch.ClientSaves == saves,
            $"A client launch census must carry its box and --client-saves-dir; got boxes={string.Join(",", launch.Boxes)} saves={launch.ClientSaves}.");
        ForkCensusScope attach = ForkCensusScope.For(ClientCensusConfig(box: "Client2Box", attachPid: 4242, saves: saves), new AttachedProfile("Client2Box", null));
        Assert(attach.Boxes.SequenceEqual(new[] { "Client2Box" }) && attach.ClientSaves == saves, "A client warm attach keeps the same scope.");
        ForkCensusScope wine = ForkCensusScope.For(ClientMirrorHostFactory.BuildClientSessionConfig(ClientCensusOptions(null, null, null) with
            { ClientTs3WineprefixPath = "/home/user/.sims3-client2" }), null);
        Assert(wine.WinePrefixes.SequenceEqual(new[] { "/home/user/.sims3-client2" }) && wine.Boxes.Count == 0, "A Wine client carries its prefix.");
    }

    [Test("ForkCensus.RefusesInsideACheckoutWithoutTheScript")]
    private static void ForkCensusRefusesInsideACheckoutWithoutTheScript()
    {
        ForkCensusScope scope = new(Array.Empty<string>(), Array.Empty<string>(), "");
        WithTempTree(root =>
        {
            string bin = Directory.CreateDirectory(Path.Combine(root, "kit", "client")).FullName;
            ForkCensusResult kit = ForkCensusGuard.RunScript(scope, bin, "python-not-used");
            Assert(kit.State == ForkCensusResult.NotApplicableState && ForkCensusGuard.Refusal(kit, false) == null,
                "No checkout marker above the build (a published tester kit): the census does not apply.");

            Directory.CreateDirectory(Path.Combine(root, "kit", ".git"));
            Assert(ForkCensusGuard.FindCheckoutRoot(bin) == null, "A .git without the controller project is not a SimsOnline checkout.");

            string checkout = MakeCheckout(Path.Combine(root, "repo"), gitFile: false);
            string build = Directory.CreateDirectory(Path.Combine(checkout, "DedicatedServer", "bin", "Debug", "net8.0")).FullName;
            ForkCensusResult missing = ForkCensusGuard.RunScript(scope, build, "python-not-used");
            Assert(missing.State == "unreadable" && ForkCensusGuard.Refusal(missing, allowStock: true) is { } refusal &&
                   refusal.StartsWith("FORK-UNSAFE:", StringComparison.Ordinal) && refusal.Contains("fork_census.py", StringComparison.Ordinal),
                $"Inside a checkout a missing census script must refuse, override or not; got {missing.State}: {missing.Summary}.");

            string worktree = MakeCheckout(Path.Combine(root, "worktree"), gitFile: true);
            Assert(ForkCensusGuard.FindCheckoutRoot(Path.Combine(worktree, "DedicatedServer")) == worktree,
                "A git worktree (.git is a file) is a checkout too.");
        });
    }

    [Test("ForkCensus.RunsTheCensusFunctionWithTheScope")]
    private static void ForkCensusRunsTheCensusFunctionWithTheScope()
    {
        WithTempTree(root =>
        {
            string checkout = MakeCheckout(Path.Combine(root, "repo"), gitFile: true);
            string scripts = Directory.CreateDirectory(Path.Combine(checkout, "scripts")).FullName;
            // A stand-in fork_census module: it echoes the arguments it was called with, so no profile is read.
            File.WriteAllText(Path.Combine(scripts, "fork_census.py"),
                "import json\n" +
                "def receipt_checkout():\n    return 'receipts'\n" +
                "def census(checkout, boxes=None, wineprefixes=None, client_saves=None):\n" +
                "    return {'state': 'fork', 'selectedAttempt': json.dumps([checkout, boxes, wineprefixes, client_saves])}\n");
            ForkCensusScope scope = new(new[] { "Client2Box" }, new[] { "/p" }, "C:/saves");
            ForkCensusResult result = RunScriptOrSkip(scope, checkout);
            Assert(result.State == "fork" &&
                   result.Summary.Contains("[\"receipts\", [\"Client2Box\"], [\"/p\"], \"C:/saves\"]", StringComparison.Ordinal),
                $"The controller must call census(receipt_checkout(), boxes, wineprefixes, client_saves) with its scope; got {result.State}: {result.Summary}.");

            File.WriteAllText(Path.Combine(scripts, "fork_census.py"), "raise SystemExit(3)\n");
            ForkCensusResult failed = RunScriptOrSkip(scope, checkout);
            Assert(failed.State == "unreadable" && failed.Summary.Contains("exited", StringComparison.Ordinal),
                $"A census process that fails is unreadable; got {failed.State}: {failed.Summary}.");
        });
    }

    [Test("ForkCensus.ResolvesThisProcessAsNotSandboxed")]
    private static void ForkCensusResolvesThisProcessAsNotSandboxed()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Skipped("Sandboxie lookup is Windows only");
        }

        // The real driver query on this (unsandboxed) test process: no box, and certain of it.
        AttachedProfile profile = ForkCensusGuard.ResolveAttachedProfile(Process.GetCurrentProcess());
        Assert(profile == AttachedProfile.Native, $"The test process is not sandboxed; got {profile}.");
    }

    private static readonly string FakeSbieLongBox = new('B', ForkCensusGuard.SbieBoxNameCount - 1);
    private static int _fakeSbieStatus;
    private static int _fakeSbieCalls;
    private static nint _fakeSbiePid;

    // A native-callable stand-in for SbieApi_QueryProcess: it fills each output to the size Sandboxie documents
    // (BOXNAME_COUNT 40, image 96, SID 96), a 39-character box name and its NUL being the most the driver returns.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int FakeSbieQueryProcess(nint processId, nint boxName, nint imageName, nint sid, nint sessionId)
    {
        _fakeSbieCalls++;
        _fakeSbiePid = processId;
        Marshal.Copy((FakeSbieLongBox + '\0').ToCharArray(), 0, boxName, ForkCensusGuard.SbieBoxNameCount);
        Marshal.Copy((new string('i', 95) + '\0').ToCharArray(), 0, imageName, ForkCensusGuard.SbieImageNameCount);
        Marshal.Copy((new string('s', 95) + '\0').ToCharArray(), 0, sid, ForkCensusGuard.SbieSidCount);
        Marshal.WriteInt32(sessionId, 1);
        return _fakeSbieStatus;
    }

    [Test("ForkCensus.SandboxieQueryTakesFullSizeOutputs")]
    private static void ForkCensusSandboxieQueryTakesFullSizeOutputs()
    {
        // The fake is a real native function pointer, so the call goes through the production delegate marshalling:
        // a caller buffer smaller than BOXNAME_COUNT truncates the name (and is overrun natively).
        string longBox = FakeSbieLongBox;
        _fakeSbieStatus = 0;
        _fakeSbieCalls = 0;
        nint export = typeof(Program).GetMethod(nameof(FakeSbieQueryProcess), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MethodHandle.GetFunctionPointer();
        AttachedProfile boxed = ForkCensusGuard.QuerySandboxieProcess(export, 4242);
        Assert(boxed.SandboxieBox == longBox && boxed.Unknown == null,
            $"A {longBox.Length}-character box name must come back whole; got '{boxed.SandboxieBox}' ({boxed.SandboxieBox?.Length ?? 0} chars).");

        Assert(_fakeSbiePid == 4242, "The query must name the attached process.");

        _fakeSbieStatus = unchecked((int)0xC000000B); // STATUS_INVALID_CID: the driver does not know the process
        Assert(ForkCensusGuard.QuerySandboxieProcess(export, 4242) == AttachedProfile.Native, "STATUS_INVALID_CID means not sandboxed.");
        _fakeSbieStatus = unchecked((int)0xC0000022);
        Assert(ForkCensusGuard.QuerySandboxieProcess(export, 4242).Unknown is { } why && why.Contains("C0000022", StringComparison.Ordinal),
            "Any other status leaves the attach profile unknown (refused).");
        Assert(_fakeSbieCalls == 3, "Every query goes through the native export.");
    }

    [Test("ForkCensus.ParsesCensusJson")]
    private static void ForkCensusParsesCensusJson()
    {
        ForkCensusResult fork = ForkCensusResult.Parse(
            "{\"state\": \"mismatch\", \"selectedAttempt\": \"cold-test-7\", \"errors\": [], \"mismatches\": [\"C:/box/p.package\"], " +
            "\"packages\": [{\"path\": \"C:/srv/p.package\", \"present\": true, \"sha256\": \"ab\", \"provenance\": \"cold-test-7\"}, " +
            "{\"path\": \"C:/box/p.package\", \"present\": false}]}");
        Assert(fork.State == "mismatch" && fork.Summary.Contains("selected=cold-test-7", StringComparison.Ordinal) &&
               fork.Summary.Contains("required build missing or different: C:/box/p.package", StringComparison.Ordinal) &&
               fork.Summary.Contains("C:/srv/p.package sha256=ab (cold-test-7)", StringComparison.Ordinal),
            "The census JSON should yield its state and a summary naming the attempt, mismatches and present packages: " + fork.Summary);
        Assert(ForkCensusResult.Parse("not json").State == "unreadable", "Output that is not JSON is unreadable.");
        Assert(ForkCensusResult.Parse("{\"packages\": []}").State == "unreadable", "JSON without a state is unreadable.");
        Assert(ForkCensusGuard.Refusal(ForkCensusResult.Parse("{\"state\": \"unreadable\"}"), allowStock: true) is { } refusal &&
               refusal.StartsWith("FORK-UNSAFE:", StringComparison.Ordinal), "Unreadable state refuses even with the stock override.");
    }

    private static ClientMirrorHostFactory.Options ClientCensusOptions(string? box, int? attachPid, string? saves) => new(
        EnableMirror: true, JsonStdout: false, Writer: TextWriter.Null, ClientTs3Path: MissingTs3, ClientSaveName: "Boot.sims3",
        ClientHostBridgeEndpoint: null, ClientHostTimeoutSeconds: 5, ClientTs3SandboxieBoxName: box, ClientTs3SandboxieStartPath: null,
        ClientAttachPid: attachPid, ClientSavesDirectory: saves);

    private static SessionConfig ClientCensusConfig(string? box, int? attachPid, string? saves) =>
        ClientMirrorHostFactory.BuildClientSessionConfig(ClientCensusOptions(box, attachPid, saves));

    private static void AssertRefused(Func<ForkCensusScope> scope, string what)
    {
        try
        {
            ForkCensusScope result = scope();
            Assert(false, $"The census scope must refuse {what}; got boxes={string.Join(",", result.Boxes)}.");
        }
        catch (ForkCensusException error)
        {
            Assert(error.Message.StartsWith("FORK-UNSAFE:", StringComparison.Ordinal), $"Refusing {what} should be FORK-UNSAFE: {error.Message}");
        }
    }

    private static ForkCensusResult RunScriptOrSkip(ForkCensusScope scope, string checkout)
    {
        try
        {
            return ForkCensusGuard.RunScript(scope, checkout, OperatingSystem.IsWindows() ? "python" : "python3");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw Skipped("python is not on PATH: " + error.Message);
        }
    }

    private static string MakeCheckout(string root, bool gitFile)
    {
        Directory.CreateDirectory(Path.Combine(root, "DedicatedServer"));
        File.WriteAllText(Path.Combine(root, "DedicatedServer", "SimsOnline.DedicatedServer.csproj"), "<Project />");
        if (gitFile)
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere\n");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
        }

        return root;
    }

    private static void WithTempTree(Action<string> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "simsonline-fork-census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            body(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Exception? StartWithCensus(string[] args, Func<ForkCensusScope, ForkCensusResult> census, string? noForkOk)
    {
        string? previous = Environment.GetEnvironmentVariable(ForkCensusGuard.NoForkOkVariable);
        Environment.SetEnvironmentVariable(ForkCensusGuard.NoForkOkVariable, noForkOk);
        try
        {
            HeadlessSims3Host host = new(SessionConfig.FromArgs(args)) { ForkCensus = census };
            try
            {
                host.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
            finally
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ForkCensusGuard.NoForkOkVariable, previous);
        }
    }

    private static string Describe(Exception? error) => error == null ? "no exception" : $"{error.GetType().Name}: {error.Message}";
}
