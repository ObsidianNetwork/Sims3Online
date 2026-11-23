using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Skills;
using Sims3.SimIFace;

namespace SimsOnline.Handlers
{
    // T5b — the SWAPPABLE handler assembly entry point. Built engine-only by
    // make-handlers.ps1 (NO shell reference, NO contract assembly), embedded as hex into the shell,
    // loaded on the pump by the shell's lazy loader via Assembly.Load(byte[]). The reflection-only
    // topology (proven by the DIAG_HANDLERTEST spike + Phase B-1): the only types crossing the
    // Assembly.Load boundary are mscorlib (System.Converter<object[],object>) + engine-ref types —
    // both resolved from the SAME external reference DLLs, so identity is guaranteed.
    //
    // Shell entry points (found by reflection by NAME — no shared interface):
    //   string[] Names()                     -> the command names this set serves
    //   string   Dispatch(name, parts, ctx)  -> handle one command; ctx is HandlerBridge.Service as
    //                                           Converter<object[],object>
    // Shell services are reached ONLY through ctx: svc(new object[]{ "Verb", arg1, ... }) -> boxed
    // result. Engine work is done directly on the boxed engine objects the verbs return. No shell
    // type is ever named here. HAZARD: the verb table is stringly-typed — always pass (ulong)/(int)/
    // (float) with the exact type the shell switch casts to (a mismatch is a runtime InvalidCast).
    public class HandlerSet
    {
        // Phase B-2: the full APPLY_* + PROBE_* state family. (DIAG forensic tools + the engine-
        // callback-binding interaction handlers stay shell-permanent.)
        public static string[] Names()
        {
            return new string[]
            {
                "PROBE_SIM_STATUS",
                "APPLY_POSITION",
                "APPLY_POSITIONS",
                "APPLY_MOTIVE",
                "PROBE_SIM_MOTIVE",
                "APPLY_CASH",
                "PROBE_HOUSEHOLD_CASH",
                "APPLY_SKILL",
                "PROBE_SIM_SKILL",
                "APPLY_AUTONOMY",
                "PROBE_SIM_AUTONOMY",
                "PROBE_SIM_INTERACTION_QUEUE",
            };
        }

        // Returns "ok" / "err:<reason>" for the shell's dispatch breadcrumb; the actual wire reply is
        // sent via ctx verbs. parts = the decoded command split (parts[0]=name, parts[1]=corrId,
        // parts[2..]=args) — only mscorlib types cross.
        public static string Dispatch(string name, string[] parts, object ctx)
        {
            Converter<object[], object> svc = (Converter<object[], object>)ctx;
            switch (name)
            {
                case "PROBE_SIM_STATUS": return ProbeSimStatus(parts, svc);
                case "APPLY_POSITION": return ApplyPosition(parts, svc);
                case "APPLY_POSITIONS": return ApplyPositions(parts, svc);
                case "APPLY_MOTIVE": return ApplyMotive(parts, svc);
                case "PROBE_SIM_MOTIVE": return ProbeSimMotive(parts, svc);
                case "APPLY_CASH": return ApplyCash(parts, svc);
                case "PROBE_HOUSEHOLD_CASH": return ProbeHouseholdCash(parts, svc);
                case "APPLY_SKILL": return ApplySkill(parts, svc);
                case "PROBE_SIM_SKILL": return ProbeSimSkill(parts, svc);
                case "APPLY_AUTONOMY": return ApplyAutonomy(parts, svc);
                case "PROBE_SIM_AUTONOMY": return ProbeSimAutonomy(parts, svc);
                case "PROBE_SIM_INTERACTION_QUEUE": return ProbeSimInteractionQueue(parts, svc);
                default: return "err:unknown handler command " + name;
            }
        }

        // -------------------------------------------------------------------------------------
        // Handlers. Behavior is a faithful port of the shell's former Handle* bodies; the only
        // changes are: shell helpers/state -> svc verbs; SendLine(BridgeCodec.X) -> Reply/SendOk/
        // SendError verbs. Role gate uses GetRole. Engine reads stay direct on the boxed objects.
        // -------------------------------------------------------------------------------------

        private static string ProbeSimStatus(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 3)
            {
                Reply(svc, "SimStatusProbe", corrId, "err", "error=missing-sim-id");
                return "err:missing-sim-id";
            }
            ulong simObjectId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            object simObj = svc(new object[] { "FindSim", simObjectId });
            if (simObj == null)
            {
                Reply(svc, "SimStatusProbe", corrId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=not-found");
                return "err:sim-not-found";
            }
            Sim sim = (Sim)simObj;

            int queueCount = 0;
            string runningKey = string.Empty;
            if (sim.InteractionQueue != null)
            {
                List<InteractionInstance> queued = new List<InteractionInstance>(sim.InteractionQueue.InteractionList);
                queueCount = queued.Count;
                InteractionInstance running = sim.InteractionQueue.RunningInteraction;
                if (running != null && running.InteractionDefinition != null)
                {
                    runningKey = running.InteractionDefinition.GetType().FullName;
                }
            }
            bool autonomyEnabled = sim.Autonomy != null && AutonomyRestrictions.IsAnyAutonomyEnabled(sim);
            Vector3 position = sim.Position;
            string report = "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) +
                ";name=" + EncodeText(svc, sim.FullName) +
                ";x=" + Float(position.x) +
                ";y=" + Float(position.y) +
                ";z=" + Float(position.z) +
                ";queueCount=" + queueCount.ToString(CultureInfo.InvariantCulture) +
                ";running=" + EncodeText(svc, runningKey) +
                ";autonomyEnabled=" + (autonomyEnabled ? "true" : "false") +
                ";speed=" + (string)svc(new object[] { "GetSpeed" }) +
                ";role=" + (string)svc(new object[] { "GetRole" });
            Reply(svc, "SimStatusProbe", corrId, "ok", report);
            return "ok";
        }

        private static string ApplyPosition(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 6) { SendError(svc, corrId, "APPLY_POSITION requires sim object id and x/y/z."); return "err"; }
            if (!RequireClient(svc, corrId, "APPLY_POSITION")) { return "err:role"; }

            ulong simObjectId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            float x = float.Parse(parts[3], CultureInfo.InvariantCulture);
            float y = float.Parse(parts[4], CultureInfo.InvariantCulture);
            float z = float.Parse(parts[5], CultureInfo.InvariantCulture);
            Vector3 target = new Vector3(x, y, z);

            object simObj = svc(new object[] { "FindSim", simObjectId });
            if (simObj == null)
            {
                // Sim not on the lot yet — hand the deferred apply back to the shell's retry queue.
                svc(new object[] { "EnqueuePendingPositionApply", simObjectId, target });
                SendOk(svc, corrId);
                return "ok:deferred";
            }
            // a client route is walking this Sim to the server's goal; the shell keeps the target.
            if ((bool)svc(new object[] { "RouteHeld", ((Sim)simObj).ObjectId.Value, target }))
            {
                SendOk(svc, corrId);
                return "ok:route-held";
            }
            ((Sim)simObj).SetPosition(target);
            SendOk(svc, corrId);
            return "ok";
        }

        // batched position write. Wire: APPLY_POSITIONS|<corr>|<simId:x:y:z>;<simId:x:y:z>;...
        // One frame in -> every pair applied in THIS BridgeAlarmTick (single simulator thread) -> one
        // OK out. Per-Sim path mirrors ApplyPosition exactly: SetPosition when the Sim is on the lot,
        // else hand to the shell's deferred sPendingPositionApplies queue (Sim not yet in
        // LotManager.Actors). No CancelAllInteractions (would re-trigger orphaned posture).
        // A malformed individual pair is skipped (counted) rather than failing the whole batch.
        private static string ApplyPositions(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 3) { SendError(svc, corrId, "APPLY_POSITIONS requires a positions payload."); return "err"; }
            if (!RequireClient(svc, corrId, "APPLY_POSITIONS")) { return "err:role"; }

            string payload = parts[2];
            if (payload == null || payload.Length == 0) { SendError(svc, corrId, "APPLY_POSITIONS payload is empty."); return "err"; }

            string[] pairs = payload.Split(';');
            int applied = 0;
            int deferred = 0;
            int skipped = 0;
            int held = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                if (pair.Length == 0) { continue; }   // trailing/empty separator

                string[] f = pair.Split(':');
                if (f.Length < 4) { skipped++; continue; }

                ulong simObjectId;
                float x, y, z;
                try
                {
                    simObjectId = ulong.Parse(f[0], CultureInfo.InvariantCulture);
                    x = float.Parse(f[1], CultureInfo.InvariantCulture);
                    y = float.Parse(f[2], CultureInfo.InvariantCulture);
                    z = float.Parse(f[3], CultureInfo.InvariantCulture);
                }
                catch { skipped++; continue; }

                Vector3 target = new Vector3(x, y, z);
                object simObj = svc(new object[] { "FindSim", simObjectId });
                if (simObj == null)
                {
                    // Sim not on the lot yet — defer via the shell's retry queue (same as ApplyPosition).
                    svc(new object[] { "EnqueuePendingPositionApply", simObjectId, target });
                    deferred++;
                    continue;
                }

                Sim sim = (Sim)simObj;
                // a client route is walking this Sim to the server's goal; the shell keeps the target.
                if ((bool)svc(new object[] { "RouteHeld", sim.ObjectId.Value, target }))
                {
                    held++;
                    continue;
                }
                // repair orphaned SocializingPosture before teleporting — otherwise the
                // puppet stays frozen even while positions stream in from the host.
                svc(new object[] { "RepairOrphanedSocializingPosture", sim });
                sim.SetPosition(target);
                applied++;
            }

            // One OK for the whole batch (the corrId the controller is awaiting). Counts go in the
            // dispatch breadcrumb for the shell log; the wire reply stays the simple OK the
            // SendBridgeCommandAsync round-trip expects.
            SendOk(svc, corrId);
            return "ok:applied=" + applied.ToString(CultureInfo.InvariantCulture) +
                ";deferred=" + deferred.ToString(CultureInfo.InvariantCulture) +
                ";held=" + held.ToString(CultureInfo.InvariantCulture) +
                ";skipped=" + skipped.ToString(CultureInfo.InvariantCulture);
        }

        private static string ApplyMotive(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 5) { SendError(svc, corrId, "APPLY_MOTIVE requires sim object id, motive name, and value."); return "err"; }
            if (!RequireClient(svc, corrId, "APPLY_MOTIVE")) { return "err:role"; }

            ulong simObjectId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            string motiveName = DecodeText(svc, parts[3]);
            float value = float.Parse(parts[4], CultureInfo.InvariantCulture);

            object simObj = svc(new object[] { "FindSim", simObjectId });
            if (simObj == null) { SendError(svc, corrId, "Sim not found: " + simObjectId.ToString(CultureInfo.InvariantCulture)); return "err"; }
            Sim sim = (Sim)simObj;

            CommodityKind kind;
            try { kind = (CommodityKind)Enum.Parse(typeof(CommodityKind), motiveName, true); }
            catch { SendError(svc, corrId, "Unknown CommodityKind: " + motiveName); return "err"; }

            if (sim.Motives == null) { SendError(svc, corrId, "Sim has no Motives collection: " + simObjectId.ToString(CultureInfo.InvariantCulture)); return "err"; }
            sim.Motives.SetValue(kind, value);
            SendOk(svc, corrId);
            return "ok";
        }

        private static string ProbeSimMotive(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 4) { Reply(svc, "SimMotiveProbe", corrId, "err", "error=missing-args"); return "err"; }

            ulong simObjectId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            string motiveName = DecodeText(svc, parts[3]);
            object simObj = svc(new object[] { "FindSim", simObjectId });
            if (simObj == null) { Reply(svc, "SimMotiveProbe", corrId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=not-found"); return "err"; }
            Sim sim = (Sim)simObj;

            CommodityKind kind;
            try { kind = (CommodityKind)Enum.Parse(typeof(CommodityKind), motiveName, true); }
            catch { Reply(svc, "SimMotiveProbe", corrId, "err", "motive=" + motiveName + ";error=unknown-kind"); return "err"; }

            if (sim.Motives == null) { Reply(svc, "SimMotiveProbe", corrId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=no-motives"); return "err"; }
            float value = sim.Motives.GetValue(kind);
            Reply(svc, "SimMotiveProbe", corrId, "ok", "motive=" + motiveName + ";value=" + Float(value));
            return "ok";
        }

        private static string ApplyCash(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 4) { SendError(svc, corrId, "APPLY_CASH requires household id and funds amount."); return "err"; }
            if (!RequireClient(svc, corrId, "APPLY_CASH")) { return "err:role"; }

            ulong householdId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            int funds = int.Parse(parts[3], CultureInfo.InvariantCulture);
            object hhObj = svc(new object[] { "FindHousehold", householdId });
            if (hhObj == null) { SendError(svc, corrId, "Household not found: " + householdId.ToString(CultureInfo.InvariantCulture)); return "err"; }
            Household household = (Household)hhObj;

            int delta = funds - household.FamilyFunds;   // FamilyFunds is read-only; modify via delta
            if (delta != 0) { household.ModifyFamilyFunds(delta); }
            SendOk(svc, corrId);
            return "ok";
        }

        private static string ProbeHouseholdCash(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 3) { Reply(svc, "HouseholdCashProbe", corrId, "err", "error=missing-args"); return "err"; }

            ulong householdId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            object hhObj = svc(new object[] { "FindHousehold", householdId });
            if (hhObj == null) { Reply(svc, "HouseholdCashProbe", corrId, "err", "household=" + householdId.ToString(CultureInfo.InvariantCulture) + ";error=not-found"); return "err"; }
            Household household = (Household)hhObj;

            Reply(svc, "HouseholdCashProbe", corrId, "ok",
                "household=" + householdId.ToString(CultureInfo.InvariantCulture) + ";funds=" + household.FamilyFunds.ToString(CultureInfo.InvariantCulture));
            return "ok";
        }

        private static string ApplySkill(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 5) { SendError(svc, corrId, "APPLY_SKILL requires sim object id, skill name, and target level."); return "err"; }
            if (!RequireClient(svc, corrId, "APPLY_SKILL")) { return "err:role"; }

            ulong simObjectId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            string skillName = DecodeText(svc, parts[3]);
            int targetLevel = int.Parse(parts[4], CultureInfo.InvariantCulture);

            object simObj = svc(new object[] { "FindSim", simObjectId });
            if (simObj == null) { SendError(svc, corrId, "Sim not found: " + simObjectId.ToString(CultureInfo.InvariantCulture)); return "err"; }
            Sim sim = (Sim)simObj;
            if (sim.SkillManager == null) { SendError(svc, corrId, "Sim has no SkillManager: " + simObjectId.ToString(CultureInfo.InvariantCulture)); return "err"; }

            SkillNames skillKind;
            try { skillKind = (SkillNames)Enum.Parse(typeof(SkillNames), skillName, true); }
            catch { SendError(svc, corrId, "Unknown SkillNames: " + skillName); return "err"; }

            Skill skill = sim.SkillManager.GetElement(skillKind);
            if (skill == null) { skill = sim.SkillManager.AddElement(skillKind); }
            if (skill == null) { SendError(svc, corrId, "Could not add skill: " + skillName); return "err"; }

            int clamped = targetLevel;
            if (clamped > skill.MaxSkillLevel) { clamped = skill.MaxSkillLevel; }
            if (clamped < 0) { clamped = 0; }
            for (int i = skill.SkillLevel; i < clamped; i++) { skill.ForceSkillLevelUp(i + 1); }
            if (skill.SkillLevel > clamped) { skill.SkillLevel = clamped; }   // down: direct field write

            SendOk(svc, corrId);
            return "ok";
        }

        private static string ProbeSimSkill(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 4) { Reply(svc, "SimSkillProbe", corrId, "err", "error=missing-args"); return "err"; }

            ulong simObjectId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            string skillName = DecodeText(svc, parts[3]);
            object simObj = svc(new object[] { "FindSim", simObjectId });
            if (simObj == null) { Reply(svc, "SimSkillProbe", corrId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=not-found"); return "err"; }
            Sim sim = (Sim)simObj;
            if (sim.SkillManager == null) { Reply(svc, "SimSkillProbe", corrId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=no-skillmanager"); return "err"; }

            SkillNames skillKind;
            try { skillKind = (SkillNames)Enum.Parse(typeof(SkillNames), skillName, true); }
            catch { Reply(svc, "SimSkillProbe", corrId, "err", "skill=" + skillName + ";error=unknown-kind"); return "err"; }

            Skill skill = sim.SkillManager.GetElement(skillKind);
            if (skill == null)
            {
                Reply(svc, "SimSkillProbe", corrId, "ok",
                    "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";skill=" + skillName + ";level=0;points=0");
                return "ok";
            }
            Reply(svc, "SimSkillProbe", corrId, "ok",
                "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";skill=" + skillName +
                ";level=" + skill.SkillLevel.ToString(CultureInfo.InvariantCulture) + ";points=" + Float(skill.SkillPoints));
            return "ok";
        }

        private static string ApplyAutonomy(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 4) { SendError(svc, corrId, "APPLY_AUTONOMY requires sim object id and disabled flag."); return "err"; }
            if (!RequireClient(svc, corrId, "APPLY_AUTONOMY")) { return "err:role"; }

            ulong simObjectId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            bool targetDisabled = parts[3] == "1";
            object simObj = svc(new object[] { "FindSim", simObjectId });
            if (simObj == null) { SendError(svc, corrId, "Sim not found: " + simObjectId.ToString(CultureInfo.InvariantCulture)); return "err"; }
            Sim sim = (Sim)simObj;
            if (sim.Autonomy == null) { SendError(svc, corrId, "Sim has no Autonomy component: " + simObjectId.ToString(CultureInfo.InvariantCulture)); return "err"; }

            bool currentlyEnabled = AutonomyRestrictions.IsAnyAutonomyEnabled(sim);
            if (targetDisabled && currentlyEnabled) { sim.Autonomy.IncrementAutonomyDisabled(); }
            else if (!targetDisabled && !currentlyEnabled) { sim.Autonomy.DecrementAutonomyDisabled(); }
            SendOk(svc, corrId);
            return "ok";
        }

        private static string ProbeSimAutonomy(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 3) { Reply(svc, "SimAutonomyProbe", corrId, "err", "error=missing-sim-id"); return "err"; }

            ulong simObjectId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            object simObj = svc(new object[] { "FindSim", simObjectId });
            if (simObj == null) { Reply(svc, "SimAutonomyProbe", corrId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=not-found"); return "err"; }
            Sim sim = (Sim)simObj;
            if (sim.Autonomy == null) { Reply(svc, "SimAutonomyProbe", corrId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=no-autonomy"); return "err"; }

            bool enabled = AutonomyRestrictions.IsAnyAutonomyEnabled(sim);
            int disabledCount = enabled ? 0 : 1;
            Reply(svc, "SimAutonomyProbe", corrId, "ok",
                "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";enabled=" + (enabled ? "true" : "false") +
                ";disabledCount=" + disabledCount.ToString(CultureInfo.InvariantCulture));
            return "ok";
        }

        private static string ProbeSimInteractionQueue(string[] parts, Converter<object[], object> svc)
        {
            string corrId = Corr(parts);
            if (parts.Length < 3) { Reply(svc, "SimInteractionQueueProbe", corrId, "err", "error=missing-sim-id"); return "err"; }

            ulong simObjectId = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            object simObj = svc(new object[] { "FindSim", simObjectId });
            if (simObj == null) { Reply(svc, "SimInteractionQueueProbe", corrId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=not-found"); return "err"; }
            Sim sim = (Sim)simObj;
            if (sim.InteractionQueue == null) { Reply(svc, "SimInteractionQueueProbe", corrId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=no-queue"); return "err"; }

            InteractionInstance pick = sim.CurrentInteraction;
            if (pick == null) { pick = sim.InteractionQueue.GetHeadInteraction(); }
            if (pick == null) { pick = sim.InteractionQueue.RunningInteraction; }

            int queueCount = pick != null ? 1 : 0;
            string currentKey = "";
            if (pick != null)
            {
                try { currentKey = pick.GetType().Name; }
                catch { currentKey = "(error)"; }
            }
            Reply(svc, "SimInteractionQueueProbe", corrId, "ok",
                "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";queueCount=" + queueCount.ToString(CultureInfo.InvariantCulture) + ";current=" + currentKey);
            return "ok";
        }

        // -------------------------------------------------------------------------------------
        // Helpers — pure mscorlib + svc verbs.
        // -------------------------------------------------------------------------------------

        private static string Corr(string[] parts)
        {
            return parts.Length > 1 ? parts[1] : "0";
        }

        // Role gate (replaces the shell's `if (sCurrentRole != "client")`). Sends the err reply and
        // returns false when the role is wrong.
        private static bool RequireClient(Converter<object[], object> svc, string corrId, string cmd)
        {
            string role = (string)svc(new object[] { "GetRole" });
            if (role != "client")
            {
                SendError(svc, corrId, cmd + " rejected: role is '" + role + "' (must be 'client'). Send SET_ROLE first.");
                return false;
            }
            return true;
        }

        // Float matches the shell's Float helper (pure mscorlib — replicated, no verb needed).
        private static string Float(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string EncodeText(Converter<object[], object> svc, string value)
        {
            return (string)svc(new object[] { "EncodeText", value });
        }

        private static string DecodeText(Converter<object[], object> svc, string value)
        {
            return (string)svc(new object[] { "DecodeText", value });
        }

        private static void SendOk(Converter<object[], object> svc, string corrId)
        {
            svc(new object[] { "SendOk", corrId });
        }

        private static void SendError(Converter<object[], object> svc, string corrId, string reason)
        {
            svc(new object[] { "SendError", corrId, reason });
        }

        private static void Reply(Converter<object[], object> svc, string probeKind, string corrId, string kind, string report)
        {
            svc(new object[] { "SendReply", probeKind, corrId, kind, report });
        }
    }
}
