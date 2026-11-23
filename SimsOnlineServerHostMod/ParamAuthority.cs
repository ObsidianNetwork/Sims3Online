// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // item 4 (the user, 2026-10-18): on a client, while the server's lines drive a driver (the replay acquired
    // it, or borrowed the client's own), the server's parameter values win. The copy's own game code can still set
    // parameters on a driver the replay borrowed (boot 5: Idle's highLOD, which each game sets from its own on-screen
    // test), and the last write wins. HoldOwn drops those calls, so the value the replay applied stays.
    // ServerHostMain follows a client driver when it maps a server driver onto it and unfollows it at release or
    // re-resolve; SacsRecorder asks HoldOwn on every SetParam. Replayed calls (SacsRecorder.Replaying) and drivers the
    // replay does not drive pass straight through. On only while the copy is on (sacs.seed-copy), so copy-off runs are
    // unchanged.
    internal sealed class ParamAuthority
    {
        // client driver -> how many server drivers are mapped onto it (a borrowed driver can serve more than one)
        private readonly Dictionary<uint, int> mFollowed = new Dictionary<uint, int>();

        internal long Held;
        internal string LastHeld; // the last held parameter: its hash (x8), or prefix:name

        internal void Follow(uint clientDriver)
        {
            int mapped;
            mFollowed.TryGetValue(clientDriver, out mapped);
            mFollowed[clientDriver] = mapped + 1;
        }

        internal void Unfollow(uint clientDriver)
        {
            int mapped;
            if (!mFollowed.TryGetValue(clientDriver, out mapped))
            {
                return;
            }

            if (mapped <= 1)
            {
                mFollowed.Remove(clientDriver);
            }
            else
            {
                mFollowed[clientDriver] = mapped - 1;
            }
        }

        internal void Clear()
        {
            mFollowed.Clear();
        }

        internal bool Follows(uint clientDriver)
        {
            return mFollowed.ContainsKey(clientDriver);
        }

        internal int Count
        {
            get { return mFollowed.Count; }
        }

        // True when this SetParam is the copy's own call on a driver the replay drives, with the copy on: drop it.
        internal bool HoldOwn(uint clientDriver, string param, bool on, bool replaying)
        {
            if (!on || replaying || !mFollowed.ContainsKey(clientDriver))
            {
                return false;
            }

            Held++;
            LastHeld = param;
            return true;
        }

        internal static string HashName(uint paramHash)
        {
            return paramHash.ToString("x8", CultureInfo.InvariantCulture);
        }

        internal string Describe()
        {
            return "paramHeld=" + Held.ToString(CultureInfo.InvariantCulture) + "/last:" + (LastHeld ?? "-")
                + "/followed:" + mFollowed.Count.ToString(CultureInfo.InvariantCulture);
        }
    }
}
