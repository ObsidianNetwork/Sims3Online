// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System.Collections.Generic;

namespace SimsOnlineServerHost
{
    // the order of the lines that introduce a server driver to a client, apart from SacsRecorder so the tests can
    // check it. Both introductions put the acquire first, then the seeds (before anything draws), the actors, and then the
    // driver's parameter values (item 4: a client that meets the driver late, or a late joiner, would otherwise keep the
    // values its own driver had).
    internal static class DriverIntroduction
    {
        // DriverHead (a driver met in a targeted dump after its acquire went by). acquire, seedLine and actors are ring
        // lines without seq; seedLine may be null (none, or the copy off). parameters are the server's cached lines,
        // complete with their seq 0 ("0|P|..."), or null.
        internal static List<string> Head(string acquire, string seedLine, IList<string> actors, IList<string> parameters)
        {
            List<string> lines = new List<string>();
            lines.Add("0|" + acquire);
            if (seedLine != null)
            {
                lines.Add("0|" + seedLine);
            }

            for (int i = 0; i < actors.Count; i++)
            {
                lines.Add("0|" + actors[i]);
            }

            if (parameters != null)
            {
                for (int i = 0; i < parameters.Count; i++)
                {
                    lines.Add(parameters[i]);
                }
            }

            for (int i = lines.Count - 1; i >= 0; i--)
                if (ClientBoundaryRoute.IsPlanRecord(lines[i], true)) lines.RemoveAt(i);
            return lines;
        }

        // SnapshotDriver (late join), lines without seq; a null is an empty slot the pager skips. The driver's history:
        // Head (its acquire first), then what the calls lost between Head and Tail left behind, then Tail. Two kinds of
        // slot are always there, so a page boundary never moves when the copy switches or a seed goes out between pages:
        // the seeds right after the acquire, and, for a driver adopted from the save, the parameter values read at
        // adoption right after the acquire and actor lines Adopt wrote (adoptHead lines). Those were set before the
        // recorder saw the driver, so its history has nothing else to restore them from; a recorded driver's own
        // parameter calls are in its history already.
        internal static List<string> Snapshot(IList<string> head, string seedSlot, int adoptHead, IList<string> adoptParams,
            bool carry, IList<string> gapSettings, IList<string> gapStates, IEnumerable<string> tail)
        {
            List<string> all = new List<string>();
            for (int i = 0; i < head.Count; i++)
            {
                all.Add(head[i]);
                if (i == 0)
                {
                    all.Add(seedSlot);
                }

                if (i + 1 == adoptHead && adoptParams != null)
                {
                    for (int p = 0; p < adoptParams.Count; p++)
                    {
                        all.Add(carry ? adoptParams[p] : null);
                    }
                }
            }

            all.AddRange(gapSettings);
            all.AddRange(gapStates);
            all.AddRange(tail);
            ClientBoundaryRoute.DiscardPlanRecords(all, false);
            return all;
        }

        // The copy-on resend (SacsRecorder.CopyOnTick, item 4): a driver's cached parameter lines ("0|P|..."), as ring
        // lines without seq. A client that met the driver while the copy was off got no parameter values in its
        // introduction (Head), and a value set before then is not set again; these carry the server's to it. Anything
        // without the seq-0 prefix is skipped.
        internal static List<string> ParameterResend(IList<string> parameters)
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < parameters.Count; i++)
            {
                string line = parameters[i];
                if (line != null && line.Length > 2 && line[0] == '0' && line[1] == '|')
                {
                    lines.Add(line.Substring(2));
                }
            }

            return lines;
        }

        // The copy-on resend's seeds for one driver (SacsRecorder.CopyOnTick, tier 1): none, a fresh spare sent alone (a
        // driver acquired or adopted while the copy was off, as before), or a fresh spare and then the server's seeds as
        // they are now (CopyOnSeeds, sacs.seed-copy=2). The last is for a driver whose seeds went out at acquire but that
        // a client met while the copy was off (metWithoutSeeds): an introduction carries seeds only while the copy is on,
        // and seedsSent is per driver, not per client, so that client never got them (the pet driver of the 2026-10-21
        // arms, "sent, never applied"). With sacs.seed-copy=1 it gets the spare alone, like a driver acquired while off.
        internal const int CopyOnNone = 0;
        internal const int CopyOnSpare = 1;
        internal const int CopyOnSeeds = 2;

        internal static int CopyOnResend(bool seedsSent, bool metWithoutSeeds, bool late)
        {
            if (!seedsSent)
            {
                return CopyOnSpare; // with sacs.seed-copy=2 its next state call sends its seeds (SacsRecorder.SendSeeds)
            }

            if (!metWithoutSeeds)
            {
                return CopyOnNone;
            }

            return late ? CopyOnSeeds : CopyOnSpare;
        }
    }
}
