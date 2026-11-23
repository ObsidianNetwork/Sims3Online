// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 via build-server-host-mod.ps1 (non-recursive
//      *.cs glob over this directory — keep this file in the mod ROOT, never a subfolder).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">
//      so the four gate tests exercise this exact decision logic.
// Therefore: NO var, NO LINQ, NO lambdas, NO async, NO C#3+ syntax, NO engine types — only
// System.Collections.Generic + plain types + an enum. A C#3+ construct accidentally added here
// compiles green in the test project but silently breaks the csc-2.0 mod build.
//
// NOTE: deliberately NO `#nullable` directive — that pragma is C# 8 and csc 2.0 rejects it
// (CS1024). The file uses only value-type keys/values (ulong/bool), so nullable-reference
// analysis in the net8.0 test build produces no warnings to suppress anyway.
using System.Collections.Generic;

namespace SimsOnlineServerHost
{
    /// <summary>
    /// the action the gate's owner (the mod pump, or a unit test) should take on the
    /// Sim's engine autonomy ref-count in response to a gate observation.
    /// </summary>
    public enum GateAction
    {
        /// <summary>No transition: leave the engine autonomy ref-count untouched.</summary>
        None = 0,

        /// <summary>A player command just entered the queue: IncrementAutonomyDisabled (gate engages).</summary>
        Disable = 1,

        /// <summary>The player's directed queue just drained: DecrementAutonomyDisabled (gate disengages).</summary>
        Enable = 2,
    }

    /// <summary>
    /// command-lifecycle autonomy gate — the PURE decision core, free of engine types so
    /// it is unit-testable in DedicatedServer.Tests. The mod owns the side effects (counting the
    /// Sim's live UserDirected queue depth and calling Increment/DecrementAutonomyDisabled); this
    /// class owns the WHEN, encoding the policy:
    ///
    ///   * Claiming a Sim does NOT disable autonomy — Register() never engages on its own unless a
    ///     command is already in flight at registration time.
    ///   * When a claimed Sim's queue gains its first UserDirected interaction (depth 0 -> >0),
    ///     the gate engages once (GateAction.Disable). The engine then preempts the running
    ///     autonomous action by HigherPriorityNext priority; the gate never cancels interactions.
    ///   * While one or more UserDirected interactions remain queued (>0 -> >0), nothing happens —
    ///     a single engage covers the whole command window (idempotent: no double-increment).
    ///   * When the UserDirected queue drains to zero (>0 -> 0), the gate disengages once
    ///     (GateAction.Enable) and autonomy resumes.
    ///   * Release/leave/disconnect clears the gate (Clear): if still engaged, it emits one Enable
    ///     so no autonomy suppression is ever leaked past the claim.
    ///
    /// The decision is on a BOOLEAN "is any UserDirected interaction present", not an integer depth.
    /// The mod's running interaction is the head of InteractionList, so summing list + running would
    /// double-count and the depth would never read 0 — see CountUserDirectedInteractions in the mod.
    /// </summary>
    public sealed class AutonomyCommandGate
    {
        // Sims the gate is tracking (registered via claim, removed via release/clear). A Sim is a
        // key here iff its owning player has claimed it and the claim is still live.
        private readonly Dictionary<ulong, bool> sTracked = new Dictionary<ulong, bool>();

        // Sims whose autonomy the gate has CURRENTLY suppressed (engaged). Engaged ==> exactly one
        // outstanding IncrementAutonomyDisabled this gate is responsible for unwinding. This bit —
        // not the engine ref-count — is the gate's source of truth for counter balance, so a
        // foreign decrement ( reset, reactions) cannot trick the gate into a double-enable.
        private readonly Dictionary<ulong, bool> sEngaged = new Dictionary<ulong, bool>();

        /// <summary>True once the Sim is registered for command-lifecycle gating.</summary>
        public bool IsTracked(ulong simObjectId)
        {
            return sTracked.ContainsKey(simObjectId);
        }

        /// <summary>True while the gate is currently suppressing the Sim's autonomy (one outstanding increment).</summary>
        public bool IsGated(ulong simObjectId)
        {
            bool engaged;
            return sEngaged.TryGetValue(simObjectId, out engaged) && engaged;
        }

        /// <summary>Number of Sims currently tracked (for DIAG_STATE / introspection).</summary>
        public int TrackedCount
        {
            get { return sTracked.Count; }
        }

        /// <summary>
        /// Snapshot of the currently-tracked Sim ids — the set the mod pump iterates each tick to
        /// poll queue depth. Returned as a fresh list so the caller can mutate the gate (Clear)
        /// while iterating without invalidating an enumerator.
        /// </summary>
        public List<ulong> TrackedSims()
        {
            return new List<ulong>(sTracked.Keys);
        }

        /// <summary>
        /// Register a claimed Sim for command-lifecycle gating. Idempotent: a second Register for an
        /// already-tracked Sim is a no-op and returns None (does NOT re-engage). The
        /// <paramref name="userDirectedPresentAtRegister"/> flag handles the claim-mid-command case:
        /// if a player command is already queued when the claim lands, the gate engages immediately
        /// so there is no window where the command runs without autonomy suppressed.
        /// </summary>
        public GateAction Register(ulong simObjectId, bool userDirectedPresentAtRegister)
        {
            if (sTracked.ContainsKey(simObjectId))
            {
                // Already tracked — do not disturb the existing engage state.
                return GateAction.None;
            }

            sTracked[simObjectId] = true;

            if (userDirectedPresentAtRegister)
            {
                sEngaged[simObjectId] = true;
                return GateAction.Disable;
            }

            sEngaged[simObjectId] = false;
            return GateAction.None;
        }

        /// <summary>
        /// Observe the Sim's current UserDirected-queue presence and return the gate transition.
        /// Untracked Sims always return None (the gate only acts on claimed Sims). The transition
        /// rule is purely engaged-bit vs observed-presence:
        ///   not engaged + present  -> Disable (engage)
        ///   engaged     + absent   -> Enable  (disengage)
        ///   otherwise              -> None
        /// </summary>
        public GateAction Observe(ulong simObjectId, bool userDirectedPresent)
        {
            if (!sTracked.ContainsKey(simObjectId))
            {
                return GateAction.None;
            }

            bool engaged;
            if (!sEngaged.TryGetValue(simObjectId, out engaged))
            {
                engaged = false;
            }

            if (!engaged && userDirectedPresent)
            {
                sEngaged[simObjectId] = true;
                return GateAction.Disable;
            }

            if (engaged && !userDirectedPresent)
            {
                sEngaged[simObjectId] = false;
                return GateAction.Enable;
            }

            return GateAction.None;
        }

        /// <summary>
        /// Clear the gate for a Sim (release / leave / disconnect). If the gate was engaged, returns
        /// Enable so the caller unwinds the one outstanding autonomy suppression; otherwise None.
        /// Either way the Sim is fully untracked afterward — no gate state survives the claim.
        /// Idempotent: clearing an untracked Sim returns None.
        /// </summary>
        public GateAction Clear(ulong simObjectId)
        {
            bool wasEngaged;
            if (!sEngaged.TryGetValue(simObjectId, out wasEngaged))
            {
                wasEngaged = false;
            }

            sTracked.Remove(simObjectId);
            sEngaged.Remove(simObjectId);

            return wasEngaged ? GateAction.Enable : GateAction.None;
        }
    }
}
