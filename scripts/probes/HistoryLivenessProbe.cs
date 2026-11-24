using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace SimsOnline.Probes
{
    // late_join.py fills this template under .omo/run/act; it never edits this source.
    public class HistoryLivenessProbe
    {
        private const string TargetIds = "";

        private static Dictionary<uint, bool> Owners(Sim sim, MethodInfo driverOf)
        {
            Dictionary<uint, bool> owners = new Dictionary<uint, bool>();
            Posture posture = sim.Posture;
            for (int depth = 0; posture != null && depth < 6; depth++)
            {
                if (posture.CurrentStateMachine != null && posture.CurrentStateMachine.IsValid)
                    owners[(uint)driverOf.Invoke(null, new object[] { posture.CurrentStateMachine })] = true;
                posture = posture.PreviousPosture;
            }
            if (sim.Standing != null && sim.Standing.CurrentStateMachine != null && sim.Standing.CurrentStateMachine.IsValid)
                owners[(uint)driverOf.Invoke(null, new object[] { sim.Standing.CurrentStateMachine })] = true;
            if (sim.InteractionQueue != null)
                foreach (InteractionInstance interaction in sim.InteractionQueue.InteractionList)
                    if (interaction != null && interaction.mCurrentStateMachine != null && interaction.mCurrentStateMachine.IsValid)
                        owners[(uint)driverOf.Invoke(null, new object[] { interaction.mCurrentStateMachine })] = true;
            return owners;
        }

        public static string Run()
        {
            Type recorder = null;
            Type shadow = null;
            Type host = null;
            Type boundary = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (recorder == null) recorder = assembly.GetType("SimsOnlineServerHost.SacsRecorder");
                if (shadow == null) shadow = assembly.GetType("SimsOnlineServerHost.SimLinkShadow");
                if (host == null) host = assembly.GetType("SimsOnlineServerHost.ServerHostMain");
                if (boundary == null) boundary = assembly.GetType("SimsOnlineServerHost.BoundaryState");
            }
            if (recorder == null || shadow == null || host == null) return "ok=false;error=no recorder";
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            int version = (int)shadow.GetProperty("NativeVersion", flags).GetValue(null, null);
            if (version < 16) return "ok=false;native=" + version;
            MethodInfo finished = shadow.GetMethod("HasNoPendingWork", flags);
            MethodInfo machines = recorder.GetMethod("ActorMachines", flags);
            MethodInfo snapshot = recorder.GetMethod("SnapshotDrivers", flags);
            MethodInfo adopt = host.GetMethod("AdoptTargetDrivers", flags);
            MethodInfo driverOf = host.GetMethod("DriverHandleOf", flags);
            int active = 0, owned = 0, excluded = 0, unknown = 0, violations = 0;
            int missingOwned = 0, missingActive = 0, retainedFinished = 0;
            string examples = "";
            Dictionary<uint, bool> allOwners = new Dictionary<uint, bool>();
            foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
                foreach (uint owner in Owners(sim, driverOf).Keys)
                    allOwners[owner] = true;
            FieldInfo lastTargets = boundary.GetField("LastTargets", flags);
            object previousTargets = lastTargets.GetValue(null);
            try
            {
            foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                string id = sim.ObjectId.Value.ToString(CultureInfo.InvariantCulture);
                if (TargetIds.Length > 0 && !TargetIds.Contains("," + id + ",")) continue;
                Dictionary<ulong, bool> targets = new Dictionary<ulong, bool>();
                targets[sim.ObjectId.Value] = true;
                adopt.Invoke(null, new object[] { targets, true });
                List<uint> selected = (List<uint>)snapshot.Invoke(null, new object[] { targets });
                List<string> rows = (List<string>)machines.Invoke(null, new object[] { sim.ObjectId.Value });
                Dictionary<uint, bool> owners = Owners(sim, driverOf);
                foreach (uint owner in owners.Keys)
                    if (owner != 0u && !selected.Contains(owner))
                    {
                        violations++;
                        missingOwned++;
                        if (missingOwned <= 6) examples += sim.FullName + ":" + owner + ",";
                    }
                foreach (string row in rows)
                {
                    string[] f = row.Split('|');
                    if (f[4] != "0" || f[5] != "1") continue;
                    uint handle = uint.Parse(f[0], CultureInfo.InvariantCulture);
                    int state = (int)finished.Invoke(null, new object[] { handle });
                    bool kept = selected.Contains(handle);
                    if (allOwners.ContainsKey(handle))
                    {
                        owned++;
                        if (!kept) { violations++; missingOwned++; }
                    }
                    else if (state == 1)
                    {
                        excluded++;
                        if (kept) { violations++; retainedFinished++; }
                    }
                    else if (state == 0)
                    {
                        active++;
                        if (!kept) { violations++; missingActive++; }
                    }
                    else unknown++;
                }
            }
            }
            finally
            {
                lastTargets.SetValue(null, previousTargets);
            }
            return "ok=" + (active + owned > 0 && violations == 0 ? "true" : "false")
                + ";native=" + version + ";activeKept=" + active + ";ownedKept=" + owned
                + ";finishedExcluded=" + excluded + ";unknown=" + unknown + ";violations=" + violations
                + ";missingOwned=" + missingOwned + ";missingActive=" + missingActive
                + ";retainedFinished=" + retainedFinished + ";examples=" + examples;
        }
    }
}
