namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// Gate 0 -- session-keyed probe-origin observation / isolation registry.
/// Modeled on <see cref="PendingIntentTracker"/> (TTL + prune) and
/// <see cref="ActiveOrderTracker"/> (actor lifecycle window), but keyed by
/// (sessionId, actor, target) with an explicit phase. Does NOT introduce a global
/// suppress boolean.
/// <para>
/// <b>Unwired:</b> Production capture / repair / coalesce / APPLY_POSITIONS paths do
/// not call this class. Decisions below are pure invariants for a future wire-up
/// slice; until then they have no runtime mirror effect.
/// </para>
/// </summary>
public sealed class SocialProbeSessionGuard
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(90);

    public enum Phase
    {
        InjectQueued = 0,
        HostApproach = 1,
        HostSocialRunning = 2,
        HostUnwind = 3,
        Settled = 4,
        Failed = 5,
    }

    private sealed class Entry
    {
        public Entry(
            string sessionId,
            ulong actorObjectId,
            ulong targetObjectId,
            Phase phase,
            DateTime deadlineUtc)
        {
            SessionId = sessionId;
            ActorObjectId = actorObjectId;
            TargetObjectId = targetObjectId;
            Phase = phase;
            DeadlineUtc = deadlineUtc;
        }

        public string SessionId { get; }
        public ulong ActorObjectId { get; }
        public ulong TargetObjectId { get; }
        public Phase Phase { get; set; }
        public DateTime DeadlineUtc { get; set; }
    }

    private readonly TimeSpan _ttl;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public SocialProbeSessionGuard(TimeSpan? ttl = null)
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

    /// <summary>
    /// Register a probe session. Rejects empty session id, zero actor/target,
    /// actor==target, begin-in-terminal phase, duplicate session id, or either Sim
    /// already participating in another <em>active</em> (non-terminal) session.
    /// </summary>
    public bool TryBegin(
        string sessionId,
        ulong actorObjectId,
        ulong targetObjectId,
        Phase phase,
        DateTime utcNow)
    {
        if (string.IsNullOrEmpty(sessionId)
            || actorObjectId == 0
            || targetObjectId == 0
            || actorObjectId == targetObjectId
            || IsTerminal(phase))
        {
            return false;
        }

        lock (_lock)
        {
            Prune(utcNow);
            if (_entries.ContainsKey(sessionId))
            {
                return false;
            }

            if (IsSimInActiveSession(actorObjectId) || IsSimInActiveSession(targetObjectId))
            {
                return false;
            }

            _entries[sessionId] = new Entry(
                sessionId,
                actorObjectId,
                targetObjectId,
                phase,
                utcNow + _ttl);
            return true;
        }
    }

    /// <summary>
    /// Advance (or refresh) phase. Rejects unknown session, backward transitions,
    /// and any change once Settled/Failed (terminals remain terminal). Jumping to
    /// Settled/Failed from any non-terminal phase is allowed. Same-phase calls
    /// refresh TTL.
    /// </summary>
    public bool TrySetPhase(string sessionId, Phase phase, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            if (!_entries.TryGetValue(sessionId, out Entry? entry))
            {
                return false;
            }

            if (IsTerminal(entry.Phase))
            {
                return false;
            }

            if (!IsTerminal(phase) && (int)phase < (int)entry.Phase)
            {
                return false;
            }

            entry.Phase = phase;
            entry.DeadlineUtc = utcNow + _ttl;
            return true;
        }
    }

    /// <summary>Remove one session. Idempotent: missing id is a successful no-op.</summary>
    public bool Clear(string sessionId)
    {
        lock (_lock)
        {
            _entries.Remove(sessionId);
            return true;
        }
    }

    /// <summary>Clear every session that references the actor (claim release / ungating). Idempotent.</summary>
    public int ClearActor(ulong actorObjectId)
    {
        lock (_lock)
        {
            return RemoveWhere(e => e.ActorObjectId == actorObjectId || e.TargetObjectId == actorObjectId);
        }
    }

    /// <summary>Clear all entries (SET_ROLE / disconnect / RELOAD_HANDLERS). Idempotent.</summary>
    public int ClearAll()
    {
        lock (_lock)
        {
            int n = _entries.Count;
            _entries.Clear();
            return n;
        }
    }

    public bool IsSessionLive(string sessionId, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            return _entries.ContainsKey(sessionId);
        }
    }

    /// <summary>True when the session is registered and not Settled/Failed.</summary>
    public bool IsSessionActive(string sessionId, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            return _entries.TryGetValue(sessionId, out Entry? entry) && !IsTerminal(entry.Phase);
        }
    }

    public bool TryGetPhase(string sessionId, DateTime utcNow, out Phase phase)
    {
        lock (_lock)
        {
            Prune(utcNow);
            if (_entries.TryGetValue(sessionId, out Entry? entry))
            {
                phase = entry.Phase;
                return true;
            }

            phase = default;
            return false;
        }
    }

    /// <summary>
    /// True when APPLY_POSITIONS / snapshot strip should omit this Sim because it is the
    /// probe actor or target during approach / running (foreign partner included).
    /// Unwired proposal -- callers do not exist in production yet.
    /// </summary>
    public bool ShouldStripPosition(ulong simObjectId, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            foreach (Entry entry in _entries.Values)
            {
                if (!IsIsolationPhase(entry.Phase))
                {
                    continue;
                }

                if (entry.ActorObjectId == simObjectId || entry.TargetObjectId == simObjectId)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// True when orphan SocializingPosture repair should defer for this Sim
    /// (session-scoped; never a global disable). Unwired proposal.
    /// </summary>
    public bool ShouldDeferOrphanRepair(ulong simObjectId, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Phase is not (Phase.HostApproach or Phase.HostSocialRunning))
                {
                    continue;
                }

                if (entry.ActorObjectId == simObjectId || entry.TargetObjectId == simObjectId)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// True when client capture should treat new GoHere/social-route on this actor as
    /// probe-origin (session known-set), not IntentCaptured. Unwired proposal.
    /// </summary>
    public bool ShouldSuppressCapture(ulong actorObjectId, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            foreach (Entry entry in _entries.Values)
            {
                if (!IsIsolationPhase(entry.Phase))
                {
                    continue;
                }

                if (entry.ActorObjectId == actorObjectId)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Actor + target ids currently under isolation (for strip-set merge). Unwired proposal.</summary>
    public IReadOnlySet<ulong> IsolatedActorsSnapshot(DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            HashSet<ulong> set = new();
            foreach (Entry entry in _entries.Values)
            {
                if (!IsIsolationPhase(entry.Phase))
                {
                    continue;
                }

                set.Add(entry.ActorObjectId);
                set.Add(entry.TargetObjectId);
            }

            return set;
        }
    }

    private static bool IsTerminal(Phase phase) =>
        phase is Phase.Settled or Phase.Failed;

    private static bool IsIsolationPhase(Phase phase) =>
        phase is Phase.InjectQueued or Phase.HostApproach or Phase.HostSocialRunning or Phase.HostUnwind;

    private bool IsSimInActiveSession(ulong simObjectId)
    {
        foreach (Entry entry in _entries.Values)
        {
            if (IsTerminal(entry.Phase))
            {
                continue;
            }

            if (entry.ActorObjectId == simObjectId || entry.TargetObjectId == simObjectId)
            {
                return true;
            }
        }

        return false;
    }

    private int RemoveWhere(Func<Entry, bool> predicate)
    {
        List<string>? keys = null;
        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            if (predicate(pair.Value))
            {
                (keys ??= new List<string>()).Add(pair.Key);
            }
        }

        if (keys == null)
        {
            return 0;
        }

        foreach (string key in keys)
        {
            _entries.Remove(key);
        }

        return keys.Count;
    }

    private void Prune(DateTime utcNow)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        List<string>? expired = null;
        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            if (utcNow >= pair.Value.DeadlineUtc)
            {
                (expired ??= new List<string>()).Add(pair.Key);
            }
        }

        if (expired == null)
        {
            return;
        }

        foreach (string key in expired)
        {
            _entries.Remove(key);
        }
    }
}
