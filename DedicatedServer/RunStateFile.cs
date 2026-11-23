using System.Text.Json;

namespace SimsOnline.DedicatedServer;

/// <summary>
/// Machine-readable controller lifecycle state (2026-07-03 dev-tooling redesign, phase 1).
/// The controller's only readiness signal used to be the "Authoritative session is running"
/// stdout line — grep-based, buffered, and indistinguishable from a stall when logs are empty
/// (a live session was once misdiagnosed off a 0-byte server.log). Every phase transition is
/// written atomically to a JSON file that dev-session.ps1 / wait-stack-ready.ps1 / agents can
/// poll instead of grepping logs. Best-effort by design: state-file IO must never take down
/// the session, and a crash-killed controller leaves the last phase behind (readers treat a
/// dead pid as authoritative over the file).
/// </summary>
public sealed class RunStateFile
{
    private readonly string _path;
    private readonly object _writeLock = new();
    private readonly List<Dictionary<string, object?>> _history = new();
    private readonly Dictionary<string, object?> _fields = new();
    private readonly string _startedUtc = UtcNow();

    public RunStateFile(string path)
    {
        _path = path;
    }

    private static string UtcNow() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    /// <summary>Set a long-lived field (save name, endpoints, pids) included in every write.</summary>
    public void Set(string key, object? value)
    {
        lock (_writeLock)
        {
            _fields[key] = value;
        }
    }

    public void Transition(string phase, string? detail = null)
    {
        lock (_writeLock)
        {
            _history.Add(new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["utc"] = UtcNow(),
                ["detail"] = detail,
            });
            // The history exists to answer "where did it stall" — a handful of entries suffices.
            if (_history.Count > 24)
            {
                _history.RemoveAt(0);
            }

            Dictionary<string, object?> document = new()
            {
                ["phase"] = phase,
                ["detail"] = detail,
                ["updatedUtc"] = UtcNow(),
                ["startedUtc"] = _startedUtc,
                ["pid"] = Environment.ProcessId,
            };
            foreach (KeyValuePair<string, object?> field in _fields)
            {
                document[field.Key] = field.Value;
            }

            document["history"] = _history;

            try
            {
                string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
                string temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                // On Windows, a concurrent stackctl/agent reader may have the
                // destination open without FileShare.Delete. A single failed
                // atomic rename left phase=host-scanning permanently while
                // the controller had already reached inWorld=True. Retry only
                // this transient I/O failure; never stall gameplay on disk IO.
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        File.Move(temp, _path, overwrite: true);
                        return;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(25);
                    }
                }
            }
            catch (Exception ex)
            {
                // State files are best-effort, but a stuck stale-phase file
                // must no longer fail silently and mislead the test harness.
                Console.Error.WriteLine($"[state-file] Failed to publish {phase}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
