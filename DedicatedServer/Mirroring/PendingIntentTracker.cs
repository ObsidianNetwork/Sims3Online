namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// echo-confirmation bookkeeping for P4.4 input capture.
///
/// When the client TS3 captures a player click, the local interaction is DELETED and the
/// intent is forwarded to the authoritative server. The server's <c>InteractionQueued</c>
/// broadcast is therefore the player's own command coming back as CONFIRMATION — but the
/// P4.2.5 self-id filter (correctly, for non-capture paths) skips self-owned-actor echoes
/// as duplicates. This tracker disambiguates: an echo that matches a recently-forwarded
/// intent (actor + interaction key, FIFO) is consumed and applied to the local TS3; anything
/// else keeps the P4.2.5 skip semantics.
///
/// Entries expire after <see cref="DefaultTtl"/> so a server that never answers cannot leak
/// matches into unrelated future echoes; <see cref="RemoveByCommandId"/> drops an entry when
/// the server rejects the forwarded command. Time is injected for testability.
/// Thread-safety: locked — the receive loop and the bridge intent handler run concurrently.
/// </summary>
public sealed class PendingIntentTracker
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _ttl;
    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();

    private sealed record Entry(Guid CommandId, ulong ActorObjectId, string InteractionKey, DateTime RecordedUtc);

    public PendingIntentTracker(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? DefaultTtl;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public void RecordForwarded(Guid commandId, ulong actorObjectId, string interactionKey, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            _entries.Add(new Entry(commandId, actorObjectId, interactionKey, utcNow));
        }
    }

    /// <summary>Consume the OLDEST pending intent matching actor + key. True = this echo is our confirmation.</summary>
    public bool TryConsumeMatch(ulong actorObjectId, string interactionKey, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].ActorObjectId == actorObjectId && _entries[i].InteractionKey == interactionKey)
                {
                    _entries.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Drop a pending intent whose forwarded command the server rejected.</summary>
    public bool RemoveByCommandId(Guid commandId)
    {
        lock (_lock)
        {
            return _entries.RemoveAll(e => e.CommandId == commandId) > 0;
        }
    }

    private void Prune(DateTime utcNow)
    {
        _entries.RemoveAll(e => utcNow - e.RecordedUtc > _ttl);
    }
}
