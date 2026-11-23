namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// tracks which owned Sims currently have an in-flight player-directed order so the
/// client position-mirror can be SUPPRESSED for exactly those Sims while their local
/// <c>Terrain.GoHere</c> route plays out and animates.
///
/// Why a separate tracker from <see cref="PendingIntentTracker"/>: the pending-intent entry is
/// CONSUMED the moment the authoritative echo arrives ( echo confirmation,
/// <c>Program.cs</c> <c>TryConsumeMatch</c>), which is precisely when the local route STARTS. So
/// "has a pending intent" is false for the entire window the route is actually walking — it is the
/// wrong gate. This tracker is keyed on the actor and lives across the whole order lifecycle:
///
///   echo-confirmed (route AddNext'd locally) → <see cref="MarkActive"/>
///   InteractionCompleted / InteractionFailed for that actor → <see cref="Clear"/>
///
/// A <see cref="DefaultTtl"/> backstop drops a stale entry so a never-completing order (lost
/// completion echo, host-side abort with no broadcast) cannot strand a Sim permanently unmirrored:
/// once the TTL lapses the Sim re-enters the position-mirror and the drift-resync can correct it.
///
/// While a Sim is active here it is stripped from <c>ApplyPositions</c> AND, because the snapshot
/// filter runs before <c>DriftDetector.HasDrift</c>, from drift detection too — so an owner's Sim
/// walking its own directed route toward the host's target does not count as "drift" until the
/// order resolves. idle-Sim convergence is unaffected: an idle owned Sim is NOT in this
/// set, so it still position-mirrors the host exactly as before.
///
/// Thread-safety: locked. The receive loop marks/clears entries; <see cref="ActiveActorsSnapshot"/>
/// reads them on the same loop, but the bridge intent handler also touches adjacent state, so the
/// lock keeps this consistent with <see cref="PendingIntentTracker"/>'s discipline.
/// </summary>
public sealed class ActiveOrderTracker
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _ttl;
    private readonly object _lock = new();
    private readonly Dictionary<ulong, DateTime> _activeUntil = new();

    public ActiveOrderTracker(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? DefaultTtl;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _activeUntil.Count;
            }
        }
    }

    /// <summary>Mark an actor as having an in-flight directed order (route just started locally).</summary>
    public void MarkActive(ulong actorObjectId, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            _activeUntil[actorObjectId] = utcNow + _ttl;
        }
    }

    /// <summary>Drop the in-flight marker for an actor (order completed / failed). Idempotent.</summary>
    public bool Clear(ulong actorObjectId)
    {
        lock (_lock)
        {
            return _activeUntil.Remove(actorObjectId);
        }
    }

    /// <summary>True if the actor currently has a (non-expired) in-flight directed order.</summary>
    public bool IsActive(ulong actorObjectId, DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            return _activeUntil.ContainsKey(actorObjectId);
        }
    }

    /// <summary>Snapshot of the non-expired active actor ids (for building the position-mirror strip set).</summary>
    public IReadOnlySet<ulong> ActiveActorsSnapshot(DateTime utcNow)
    {
        lock (_lock)
        {
            Prune(utcNow);
            return new HashSet<ulong>(_activeUntil.Keys);
        }
    }

    private void Prune(DateTime utcNow)
    {
        if (_activeUntil.Count == 0) return;
        List<ulong>? expired = null;
        foreach (KeyValuePair<ulong, DateTime> entry in _activeUntil)
        {
            if (utcNow >= entry.Value)
            {
                (expired ??= new List<ulong>()).Add(entry.Key);
            }
        }

        if (expired != null)
        {
            foreach (ulong key in expired) _activeUntil.Remove(key);
        }
    }
}
