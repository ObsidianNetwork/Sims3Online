// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 via build-server-host-mod.ps1 (non-recursive
//      *.cs glob over this directory — keep this file in the mod ROOT, never a subfolder).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">
//      so the lane-ordering tests exercise this exact dequeue policy.
// Therefore: NO var, NO LINQ, NO lambdas, NO async, NO C#3+ syntax, NO engine types — only
// System.Collections.Generic + plain string types. A C#3+ construct accidentally added here
// compiles green in the test project but silently breaks the csc-2.0 mod build.
//
// NOTE: deliberately NO `#nullable` directive — that pragma is C# 8 and csc 2.0 rejects it
// (CS1024). The file uses only reference-type (string) values; the net8.0 test build sees no
// nullable-reference warnings worth suppressing here.
using System.Collections.Generic;

namespace SimsOnlineServerHost
{
    /// <summary>
    /// the PURE outbound-lane policy core, free of engine types and locks so it is
    /// unit-testable in DedicatedServer.Tests. The mod shell (ServerHostMain) owns the side
    /// effects — the <c>sSendLock</c>, the <c>BridgeMemoryBuffer.CanWriteHostLine</c>/<c>WriteHostLine</c>
    /// frame writes, and the 500ms consumed-stamp fallback — while this class owns the WHEN of
    /// which line leaves next.
    ///
    /// Two FIFO lanes share one logical outbound queue:
    ///   * PRIORITY — small latency-sensitive control lines (OK/ERROR/HELLO/EVENT/*_PROBE),
    ///     enqueued via the shell's SendLine. One admin/command reply must not wait behind a
    ///     500+ chunk snapshot burst (the set_game_speed-timeout root cause).
    ///   * BULK — snapshot chunks, enqueued via the shell's SendLines / SendChunkedSnapshot.
    ///
    /// Policy: strict priority-first dequeue. A priority line jumps ahead of the bulk
    /// backlog every drain step. Priority lines are small and infrequent, so strict priority-first
    /// cannot meaningfully starve bulk (no fairness counter — see the handoff). Chunks of a single
    /// snapshot stay in order RELATIVE TO EACH OTHER because they are all bulk and FIFO within the
    /// lane; cross-lane reordering (priority ahead of bulk) is exactly the goal. This is purely
    /// internal queue ordering: no wire/protocol change, no ProtocolVersion bump.
    /// </summary>
    public sealed class OutboundLaneQueue
    {
        private readonly Queue<string> sPriority = new Queue<string>();
        private readonly Queue<string> sBulk = new Queue<string>();

        /// <summary>Enqueue a small latency-sensitive control/command/event line (priority lane).</summary>
        public void EnqueuePriority(string line)
        {
            sPriority.Enqueue(line);
        }

        /// <summary>Enqueue a bulk snapshot-chunk line (bulk lane, FIFO within the lane).</summary>
        public void EnqueueBulk(string line)
        {
            sBulk.Enqueue(line);
        }

        /// <summary>True when BOTH lanes are empty — the SendLine fast-path "queue empty" check.</summary>
        public bool IsEmpty
        {
            get { return sPriority.Count == 0 && sBulk.Count == 0; }
        }

        /// <summary>True when EITHER lane has a pending line (HasPendingOutbound).</summary>
        public bool HasPending
        {
            get { return sPriority.Count > 0 || sBulk.Count > 0; }
        }

        /// <summary>Total pending lines across both lanes (DIAG pendingOutbound + alarm cadence).</summary>
        public int Count
        {
            get { return sPriority.Count + sBulk.Count; }
        }

        /// <summary>
        /// Dequeue the next line to send: PRIORITY first, BULK only when priority is empty.
        /// Returns null when both lanes are empty. This is the whole fix — one admin reply now
        /// jumps ahead of the chunk backlog every drain step.
        /// </summary>
        public string Dequeue()
        {
            if (sPriority.Count > 0)
            {
                return sPriority.Dequeue();
            }

            if (sBulk.Count > 0)
            {
                return sBulk.Dequeue();
            }

            return null;
        }

        /// <summary>drop queued outbound from a prior controller attachment on CONNECT.</summary>
        public void Clear()
        {
            sPriority.Clear();
            sBulk.Clear();
        }
    }
}
