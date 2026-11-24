// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System;
using System.Collections.Generic;

namespace SimsOnlineServerHost
{
    // Client side: the order and the due times of stream lines that wait a fixed delay before they are applied
    // (LineDelay.cs has the reason and the game side). No engine types here, so the order rules have unit tests.
    //
    // Rules:
    //   - A batch is due its delay after it arrived, and never before a batch that arrived earlier: when the delay is
    //     turned down while batches wait, the newer ones still follow the older ones.
    //   - A Sim whose walk line (Rs) waits in the queue counts as about to walk, so a transform of it that arrives
    //     meanwhile is not taken for a moving object.
    internal sealed class LineDelayQueue
    {
        internal sealed class Batch
        {
            public string[] Lines;
            public long ArrivedMs;
            public long DueMs;
            public string[] Walkers; // the Sims with a walk line in this batch
        }

        private readonly List<Batch> mQueue = new List<Batch>();
        private int mHead;
        private long mLastDueMs;
        private readonly Dictionary<string, int> mPendingWalks = new Dictionary<string, int>();

        internal int Waiting
        {
            get { return mQueue.Count - mHead; }
        }

        // Drops every waiting batch (e.g., on presentation reset). Returns the lines dropped.
        internal int Clear()
        {
            int lines = 0;
            for (int i = mHead; i < mQueue.Count; i++)
            {
                Batch batch = mQueue[i];
                if (batch != null && batch.Lines != null)
                {
                    lines += batch.Lines.Length;
                }
            }

            mQueue.Clear();
            mHead = 0;
            mLastDueMs = 0L;
            mPendingWalks.Clear();
            return lines;
        }

        // True while a walk line of this Sim waits here.
        internal bool WalkPending(string simId)
        {
            return simId != null && mPendingWalks.ContainsKey(simId);
        }

        internal void Add(string[] lines, long nowMs, double delayMs)
        {
            if (lines == null || lines.Length == 0)
            {
                return;
            }

            Batch batch = new Batch();
            batch.Lines = lines;
            batch.ArrivedMs = nowMs;
            long due = nowMs + (long)Math.Max(0.0, delayMs);
            batch.DueMs = due > mLastDueMs ? due : mLastDueMs;
            mLastDueMs = batch.DueMs;
            List<string> walkers = null;
            for (int i = 0; i < lines.Length; i++)
            {
                if (Field(lines[i], 1) != "Rs")
                {
                    continue;
                }

                string sim = Field(lines[i], 2);
                if (sim == null)
                {
                    continue;
                }

                if (walkers == null)
                {
                    walkers = new List<string>();
                }

                walkers.Add(sim);
                int count;
                mPendingWalks.TryGetValue(sim, out count);
                mPendingWalks[sim] = count + 1;
            }

            batch.Walkers = walkers == null ? null : walkers.ToArray();
            mQueue.Add(batch);
        }

        // The oldest batch when its time has come, otherwise null. Its walk lines no longer count as waiting.
        internal Batch Next(long nowMs)
        {
            if (mHead >= mQueue.Count)
            {
                return null;
            }

            Batch batch = mQueue[mHead];
            if (batch.DueMs > nowMs)
            {
                return null;
            }

            mQueue[mHead] = null;
            mHead++;
            if (mHead == mQueue.Count)
            {
                mQueue.Clear();
                mHead = 0;
            }
            else if (mHead >= 256)
            {
                mQueue.RemoveRange(0, mHead);
                mHead = 0;
            }

            for (int i = 0; batch.Walkers != null && i < batch.Walkers.Length; i++)
            {
                int count;
                if (mPendingWalks.TryGetValue(batch.Walkers[i], out count))
                {
                    if (count <= 1)
                    {
                        mPendingWalks.Remove(batch.Walkers[i]);
                    }
                    else
                    {
                        mPendingWalks[batch.Walkers[i]] = count - 1;
                    }
                }
            }

            return batch;
        }

        // Field number index (from 0) of a line whose fields are separated by '|', without splitting the line; null
        // when the line has fewer fields.
        internal static string Field(string line, int index)
        {
            if (line == null)
            {
                return null;
            }

            int start = 0;
            for (int i = 0; i < index; i++)
            {
                int bar = line.IndexOf('|', start);
                if (bar < 0)
                {
                    return null;
                }

                start = bar + 1;
            }

            int end = line.IndexOf('|', start);
            return end < 0 ? line.Substring(start) : line.Substring(start, end - start);
        }
    }
}
