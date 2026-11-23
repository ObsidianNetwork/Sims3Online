using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SimsOnline.DedicatedServer.Authority;

/// <summary>
/// The profiles a census covers, the arguments stackctl.launch_fork_census passes: the boxes and Wine prefixes the
/// controller's TS3 uses and its --client-saves-dir ("" when none). The census always adds the server profile.
/// </summary>
public sealed record ForkCensusScope(IReadOnlyList<string> Boxes, IReadOnlyList<string> WinePrefixes, string ClientSaves)
{
    /// <summary>
    /// The scope for this host: its configured box, Wine prefix and saves folder, plus, on attach, the box or prefix
    /// the attached TS3 actually runs in. An attach whose profile is unknown or differs from the configured one refuses.
    /// </summary>
    public static ForkCensusScope For(SessionConfig config, AttachedProfile? attached)
    {
        List<string> boxes = string.IsNullOrEmpty(config.Ts3SandboxieBoxName) ? new() : new() { config.Ts3SandboxieBoxName };
        List<string> prefixes = string.IsNullOrEmpty(config.ClientMirrorWineprefixPath) ? new() : new() { config.ClientMirrorWineprefixPath };
        if (attached != null)
        {
            if (attached.Unknown != null)
            {
                throw new ForkCensusException($"FORK-UNSAFE: cannot establish the attached TS3's profile ({attached.Unknown}); the census cannot cover it.");
            }

            if (attached.SandboxieBox != null && !Merge(boxes, attached.SandboxieBox, StringComparer.OrdinalIgnoreCase))
            {
                throw new ForkCensusException($"FORK-UNSAFE: the attached TS3 runs in Sandboxie box '{attached.SandboxieBox}', not the configured '{boxes[0]}'.");
            }

            if (attached.SandboxieBox == null && boxes.Count > 0)
            {
                throw new ForkCensusException($"FORK-UNSAFE: the attached TS3 is not in the configured Sandboxie box '{boxes[0]}'.");
            }

            if (attached.WinePrefix != null && !Merge(prefixes, attached.WinePrefix, StringComparer.Ordinal))
            {
                throw new ForkCensusException($"FORK-UNSAFE: the attached TS3 runs in Wine prefix '{attached.WinePrefix}', not the configured '{prefixes[0]}'.");
            }
        }

        return new ForkCensusScope(boxes, prefixes, config.ClientSavesDirectory ?? "");
    }

    // Adds the attached value when nothing is configured; false when it differs from the configured one.
    private static bool Merge(List<string> configured, string attached, StringComparer comparer)
    {
        string normalized = attached.TrimEnd('/', '\\');
        if (configured.Count == 0)
        {
            configured.Add(normalized);
            return true;
        }

        return comparer.Equals(configured[0].TrimEnd('/', '\\'), normalized);
    }
}

/// <summary>
/// Where an attached TS3 runs: its Sandboxie box (null: not sandboxed) or Wine prefix (null: not Wine), or why that
/// could not be established (<see cref="Unknown"/>, which refuses the attach).
/// </summary>
public sealed record AttachedProfile(string? SandboxieBox, string? WinePrefix, string? Unknown = null)
{
    public static readonly AttachedProfile Native = new(null, null);
}

/// <summary>
/// One census answer: <c>fork</c>, <c>stock</c>, <c>unknown</c>, <c>mismatch</c> or <c>unreadable</c> (scripts/fork_census.py),
/// or <c>not-applicable</c> when the controller was not built in a SimsOnline checkout (a published tester kit has no receipts).
/// </summary>
public sealed record ForkCensusResult(string State, string Summary)
{
    public const string NotApplicableState = "not-applicable";

    public static ForkCensusResult Unreadable(string why) => new("unreadable", $"fork: census unavailable ({why})");

    /// <summary>Reads the census JSON (the dict fork_census.census returns); anything else is unreadable.</summary>
    public static ForkCensusResult Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out JsonElement stateElement) ||
                stateElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(stateElement.GetString()))
            {
                return Unreadable("census JSON has no state");
            }

            string state = stateElement.GetString()!;
            string selected = root.TryGetProperty("selectedAttempt", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "none";
            List<string> parts = new() { $"fork: state={state} selected={selected}" };
            parts.AddRange(Strings(root, "errors").Select(error => "error: " + error));
            parts.AddRange(Strings(root, "mismatches").Select(path => "required build missing or different: " + path));
            if (root.TryGetProperty("packages", out JsonElement packages) && packages.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement package in packages.EnumerateArray())
                {
                    if (package.ValueKind == JsonValueKind.Object && package.TryGetProperty("present", out JsonElement present) &&
                        present.ValueKind == JsonValueKind.True)
                    {
                        parts.Add($"{Text(package, "path")} sha256={Text(package, "sha256")} ({Text(package, "provenance")})");
                    }
                }
            }

            return new ForkCensusResult(state, string.Join("; ", parts));
        }
        catch (JsonException error)
        {
            return Unreadable("census output is not JSON: " + error.Message);
        }
    }

    private static IEnumerable<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : Enumerable.Empty<string>();

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "?";
}

/// <summary>A launch or attach the physical fork census refused (FORK-MISSING or FORK-UNSAFE).</summary>
public sealed class ForkCensusException : InvalidOperationException
{
    public ForkCensusException(string message) : base(message)
    {
    }
}

/// <summary>
/// The controller's copy of the Python launch guard (fork_census.guard): before HeadlessSims3Host launches or attaches
/// TS3, the one physical census (scripts/fork_census.py) must report the installed fork. SIMSONLINE_NO_FORK_OK=1
/// allows only a physically stock census; unknown, mismatched or unreadable state always refuses.
/// </summary>
public static class ForkCensusGuard
{
    public const string NoForkOkVariable = "SIMSONLINE_NO_FORK_OK";
    private static readonly TimeSpan CensusTimeout = TimeSpan.FromSeconds(120);

    // A SimsOnline checkout root holds .git (a directory, or a file in a worktree) and this project file.
    private static readonly string CheckoutMarker = Path.Combine("DedicatedServer", "SimsOnline.DedicatedServer.csproj");

    // Calls the census function stackctl.launch_fork_census calls, with its arguments (the CLI cannot take them).
    private const string CensusProgram =
        "import json,sys\n" +
        "sys.path.insert(0, sys.argv[1])\n" +
        "import fork_census\n" +
        "scope = json.loads(sys.argv[2])\n" +
        "print(json.dumps(fork_census.census(fork_census.receipt_checkout(), boxes=scope['boxes'], " +
        "wineprefixes=scope['wineprefixes'], client_saves=scope['clientSaves'])))\n";

    /// <summary>Null when the launch may go on, else the refusal (the same FORK-MISSING / FORK-UNSAFE wording as Python).</summary>
    public static string? Refusal(ForkCensusResult result, bool allowStock)
    {
        if (result.State is "fork" or ForkCensusResult.NotApplicableState)
        {
            return null;
        }

        if (result.State == "stock")
        {
            return allowStock
                ? null
                : $"FORK-MISSING: {result.Summary}. Install with quick_loop.py fork-install <attempt> (or fork-cycle --boot). " +
                  $"Deliberate stock: {NoForkOkVariable}=1.";
        }

        return $"FORK-UNSAFE: {result.Summary}. {NoForkOkVariable} cannot override physical forks or uncertain state.";
    }

    /// <summary>Runs the census and throws <see cref="ForkCensusException"/> when it refuses; logs the census line either way.</summary>
    public static void Enforce(Func<ForkCensusScope, ForkCensusResult> census, ForkCensusScope scope, Action<string> log)
    {
        ForkCensusResult result;
        try
        {
            result = census(scope);
        }
        catch (Exception error) when (error is not ForkCensusException)
        {
            result = ForkCensusResult.Unreadable($"{error.GetType().Name}: {error.Message}");
        }

        bool allowStock = Environment.GetEnvironmentVariable(NoForkOkVariable) == "1";
        string? refusal = Refusal(result, allowStock);
        if (refusal != null)
        {
            throw new ForkCensusException(refusal);
        }

        log(result.State == "stock" ? $"FORK-MISSING (allowed by {NoForkOkVariable}=1): {result.Summary}" : result.Summary);
    }

    /// <summary>The real census, run from the checkout this controller was built in.</summary>
    public static ForkCensusResult RunScript(ForkCensusScope scope) => RunScript(scope, AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "python" : "python3");

    /// <summary>
    /// Outside a SimsOnline checkout (a published tester kit: no checkout marker above <paramref name="start"/>) there
    /// are no receipts, so the census does not apply. Inside one, a missing script or a census that cannot run is unreadable.
    /// </summary>
    public static ForkCensusResult RunScript(ForkCensusScope scope, string start, string python)
    {
        string? checkout = FindCheckoutRoot(start);
        if (checkout == null)
        {
            return new ForkCensusResult(ForkCensusResult.NotApplicableState,
                $"fork: census not applicable (no SimsOnline checkout above {start}; a published build has no receipts)");
        }

        string scripts = Path.Combine(checkout, "scripts");
        if (!File.Exists(Path.Combine(scripts, "fork_census.py")))
        {
            return ForkCensusResult.Unreadable($"checkout {checkout} has no scripts/fork_census.py");
        }

        ProcessStartInfo startInfo = new(python)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        string arguments = JsonSerializer.Serialize(new { boxes = scope.Boxes, wineprefixes = scope.WinePrefixes, clientSaves = scope.ClientSaves });
        foreach (string argument in new[] { "-I", "-c", CensusProgram, scripts, arguments })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{python} did not start");
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CensusTimeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return ForkCensusResult.Unreadable($"census timed out after {CensusTimeout.TotalSeconds:0} s");
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            return ForkCensusResult.Unreadable($"census exited {process.ExitCode}: {stderr.Result.Trim()}");
        }

        return ForkCensusResult.Parse(stdout.Result);
    }

    /// <summary>The nearest directory at or above <paramref name="start"/> holding .git and the controller project.</summary>
    public static string? FindCheckoutRoot(string start)
    {
        for (DirectoryInfo? directory = new(start); directory != null; directory = directory.Parent)
        {
            string git = Path.Combine(directory.FullName, ".git");
            if ((Directory.Exists(git) || File.Exists(git)) && File.Exists(Path.Combine(directory.FullName, CheckoutMarker)))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// The profile an attached TS3 runs in: on Windows its Sandboxie box (asked of Sandboxie's driver; no SbieSvc
    /// service means none can be sandboxed), on Linux its WINEPREFIX from /proc. Anything uncertain is Unknown.
    /// </summary>
    public static AttachedProfile ResolveAttachedProfile(Process process)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException error)
        {
            return new AttachedProfile(null, null, "attach process id unavailable: " + error.Message);
        }

        if (OperatingSystem.IsWindows())
        {
            return SandboxieProfile(pid);
        }

        if (OperatingSystem.IsLinux())
        {
            return WineProfile(pid);
        }

        return new AttachedProfile(null, null, $"attach profile lookup unsupported on {RuntimeInformation.OSDescription}");
    }

    private const int StatusInvalidCid = unchecked((int)0xC000000B);

    // SbieApi_QueryProcess output buffer sizes in WCHARs, from sandboxie-plus/Sandboxie master 70669909:
    // Sandboxie/core/dll/sbieapi.h:104-109 documents WCHAR [BOXNAME_COUNT], [96] and [96] (the box parameter's
    // "wchar34" name is stale); Sandboxie/common/defines.h:54 sets BOXNAME_COUNT to (38 + 2); and
    // Sandboxie/core/dll/sbieapi.c:525-535 passes 96 as the image-name length to SbieApi_QueryProcessEx2.
    public const int SbieBoxNameCount = 38 + 2;
    public const int SbieImageNameCount = 96;
    public const int SbieSidCount = 96;

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int SbieQueryProcess(nint processId, [Out] char[] boxName, [Out] char[] imageName, [Out] char[] sid, out uint sessionId);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static AttachedProfile SandboxieProfile(int pid)
    {
        string? image;
        try
        {
            using RegistryKey? service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SbieSvc");
            if (service == null)
            {
                return AttachedProfile.Native; // Sandboxie is not installed, so no process is sandboxed.
            }

            image = service.GetValue("ImagePath") as string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new AttachedProfile(null, null, "Sandboxie service unreadable: " + error.Message);
        }

        string? directory = string.IsNullOrWhiteSpace(image) ? null : Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(image.Trim().Trim('"')));
        if (directory == null)
        {
            return new AttachedProfile(null, null, "Sandboxie service has no ImagePath");
        }

        string dll = Environment.Is64BitProcess ? Path.Combine(directory, "SbieDll.dll") : Path.Combine(directory, "32", "SbieDll.dll");
        if (!NativeLibrary.TryLoad(dll, out nint library))
        {
            return new AttachedProfile(null, null, "cannot load " + dll);
        }

        if (!NativeLibrary.TryGetExport(library, "SbieApi_QueryProcess", out nint export))
        {
            return new AttachedProfile(null, null, dll + " has no SbieApi_QueryProcess");
        }

        return QuerySandboxieProcess(export, pid);
    }

    /// <summary>
    /// Calls SbieApi_QueryProcess at <paramref name="queryProcessExport"/> with buffers of the documented sizes (a
    /// test passes a fake native export here) and reads the box: empty or STATUS_INVALID_CID means not sandboxed.
    /// </summary>
    public static AttachedProfile QuerySandboxieProcess(nint queryProcessExport, int pid)
    {
        char[] box = new char[SbieBoxNameCount];
        int status = Marshal.GetDelegateForFunctionPointer<SbieQueryProcess>(queryProcessExport)(
            pid, box, new char[SbieImageNameCount], new char[SbieSidCount], out _);
        int end = Array.IndexOf(box, '\0');
        string boxName = new string(box, 0, end < 0 ? box.Length : end);
        if (status == 0)
        {
            return boxName.Length == 0 ? AttachedProfile.Native : new AttachedProfile(boxName, null);
        }

        return status == StatusInvalidCid
            ? AttachedProfile.Native // the driver does not know the process: it is not sandboxed
            : new AttachedProfile(null, null, $"SbieApi_QueryProcess({pid}) returned 0x{status:X8}");
    }

    private static AttachedProfile WineProfile(int pid)
    {
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes($"/proc/{pid}/environ");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new AttachedProfile(null, null, $"/proc/{pid}/environ unreadable: {error.Message}");
        }

        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        foreach (string entry in Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = entry.IndexOf('=');
            if (equals > 0)
            {
                environment[entry[..equals]] = entry[(equals + 1)..];
            }
        }

        if (environment.TryGetValue("WINEPREFIX", out string? prefix) && prefix.Length > 0)
        {
            return new AttachedProfile(null, prefix);
        }

        return environment.TryGetValue("HOME", out string? home) && home.Length > 0
            ? new AttachedProfile(null, Path.Combine(home, ".wine")) // Wine's default prefix
            : new AttachedProfile(null, null, $"process {pid} has neither WINEPREFIX nor HOME");
    }
}
