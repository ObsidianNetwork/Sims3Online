// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System.Collections.Generic;

namespace SimsOnlineServerHost
{
    // B2: the latest state the stream gave each server driver on this client: an E line's state, or a request
    // the replay applied. A failed request changes nothing; a release or a re-acquire of the driver forgets it.
    // StateCatchUp cannot serve: it records only while sacs.catchup is on (off by default).
    internal sealed class ReplayStateCache
    {
        internal const int Capacity = 1024; // server drivers; the least recently set goes first

        private struct Entry
        {
            internal uint Actor;
            internal uint State;
            internal long Order;
        }

        private readonly Dictionary<uint, Entry> mStates = new Dictionary<uint, Entry>();
        private long mOrder;

        internal int Count
        {
            get { return mStates.Count; }
        }

        internal void Set(uint serverDriver, uint actor, uint state)
        {
            if (!mStates.ContainsKey(serverDriver) && mStates.Count >= Capacity)
            {
                uint oldest = 0u;
                long oldestOrder = long.MaxValue;
                foreach (KeyValuePair<uint, Entry> kept in mStates)
                {
                    if (kept.Value.Order < oldestOrder)
                    {
                        oldest = kept.Key;
                        oldestOrder = kept.Value.Order;
                    }
                }

                mStates.Remove(oldest);
            }

            Entry entry;
            entry.Actor = actor;
            entry.State = state;
            entry.Order = ++mOrder;
            mStates[serverDriver] = entry;
        }

        internal void Forget(uint serverDriver)
        {
            mStates.Remove(serverDriver);
        }

        internal void Clear()
        {
            mStates.Clear();
        }

        internal bool TryGet(uint serverDriver, out uint actor, out uint state)
        {
            Entry entry;
            if (mStates.TryGetValue(serverDriver, out entry))
            {
                actor = entry.Actor;
                state = entry.State;
                return true;
            }

            actor = 0u;
            state = 0u;
            return false;
        }

        // Of these server drivers, the one the stream set last, and its state.
        internal bool Latest(List<uint> serverDrivers, out uint serverDriver, out uint actor, out uint state)
        {
            serverDriver = 0u;
            actor = 0u;
            state = 0u;
            long best = 0;
            for (int i = 0; i < serverDrivers.Count; i++)
            {
                Entry entry;
                if (mStates.TryGetValue(serverDrivers[i], out entry) && entry.Order > best)
                {
                    best = entry.Order;
                    serverDriver = serverDrivers[i];
                    actor = entry.Actor;
                    state = entry.State;
                }
            }

            return best > 0;
        }
    }
}
