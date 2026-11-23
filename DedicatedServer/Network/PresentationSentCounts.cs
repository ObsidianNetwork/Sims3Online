using System.Globalization;
using SimsOnline.DedicatedServer.Protocol;
using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Network;

/// <summary>
/// the presentation lines (SimAnimation) queued for each player, by kind. Live pages go to every player joined
/// at the time, a joiner's history to it alone, and nothing sent before a player joined ever reaches it, so the mod's
/// one sentByKind total cannot be set against one client of two; this can. The admin listener answers
/// PRESENTATION_SENT with it, and scripts/stream_kinds.py sets a player's row against that client's received count
/// (replay-skips receivedByKind). A gap left there is the client controller's (receive queue, replay, SimLink ring).
/// </summary>
internal sealed class PresentationSentCounts
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Dictionary<string, long>> _byPlayer = new();

    /// <summary>A batch the transport accepted for these players: its SimAnimation lines count for each of them.</summary>
    public void Add(IReadOnlyList<ServerEvent> serverEvents, IEnumerable<int> players)
    {
        Dictionary<string, long>? batch = null;
        foreach (ServerEvent serverEvent in serverEvents)
        {
            if (serverEvent is ServerEvent.SimAnimation animation)
            {
                batch ??= new Dictionary<string, long>();
                foreach (string line in animation.Lines)
                {
                    LineKindCounts.Add(batch, LineKindCounts.KindOf(line));
                }
            }
        }

        if (batch == null)
        {
            return;
        }

        lock (_lock)
        {
            foreach (int player in players)
            {
                if (!_byPlayer.TryGetValue(player, out Dictionary<string, long>? counts))
                {
                    counts = new Dictionary<string, long>();
                    _byPlayer[player] = counts;
                }

                foreach (KeyValuePair<string, long> kind in batch)
                {
                    // LineKindCounts' bound: kinds past the cap count under "?".
                    string key = counts.ContainsKey(kind.Key) || counts.Count < LineKindCounts.MaxKinds ? kind.Key : LineKindCounts.Other;
                    counts.TryGetValue(key, out long seen);
                    counts[key] = seen + kind.Value;
                }
            }
        }
    }

    /// <summary>"p1=AS:2/AW:5/P:300;p2=AS:1/P:120" in player order; "-" before anything was queued.</summary>
    public string Describe()
    {
        lock (_lock)
        {
            if (_byPlayer.Count == 0)
            {
                return "-";
            }

            return string.Join(";", _byPlayer.Keys.OrderBy(player => player).Select(player =>
                "p" + player.ToString(CultureInfo.InvariantCulture) + "=" + LineKindCounts.Format(_byPlayer[player])));
        }
    }
}
