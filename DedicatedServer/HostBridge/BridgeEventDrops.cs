using System.Collections.Concurrent;
using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>
/// Mod EVENT lines of a kind <see cref="BridgeLineCodec.TryDecodeEvent(string)"/> decodes that did not decode, counted
/// per kind. The first drop of each kind is logged with the reason and the start of the line; later drops of that kind
/// are only counted. The admin listener answers EVENTS_DROPPED with them on any controller
/// (<see cref="Network.BridgeAdminListener"/>, <c>bridgectl.py EVENTS_DROPPED</c>).
/// </summary>
public sealed class BridgeEventDrops
{
    private const int SampleLength = 160;
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly Action<string> _log;

    public BridgeEventDrops(Action<string> log)
    {
        _log = log;
    }

    /// <summary>The process-wide counts the codec uses; logs to stdout.</summary>
    public static BridgeEventDrops Shared { get; } = new(line => Console.WriteLine(line));

    public void Note(string kind, string reason, string line)
    {
        if (_counts.TryAdd(kind, 1))
        {
            string sample = line.Length > SampleLength ? line[..SampleLength] + "…" : line;
            _log($"[bridge] dropped a mod event {kind} that did not decode ({reason}): {sample} (logged once per kind; bridgectl.py EVENTS_DROPPED counts them)");
            return;
        }

        _counts.AddOrUpdate(kind, 1, static (_, count) => count + 1);
    }

    public long Count(string kind) => _counts.TryGetValue(kind, out long count) ? count : 0;

    /// <summary>"InteractionQueued:3/PickFailed:1" in kind order; "-" before any drop.</summary>
    public string Describe() => LineKindCounts.Format(new Dictionary<string, long>(_counts));
}
