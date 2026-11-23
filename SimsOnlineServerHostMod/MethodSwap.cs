using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Vehicles;

namespace SimsOnlineServerHost
{
    // Startup method swap (Session L spike, 2026-10-22; the sims3-modernization mods/ts3fixes mechanism, see
    // docs/engine/sims3-modernization-crossref.md "Method replacement without a fork build"). At startup, before the
    // world loads, a game method's MonoMethod header (40 bytes at MethodHandle.Value; the name word at +0x18 stays) is
    // replaced by a mod method's, and a stub method of the mod receives the game method's header, so the original stays
    // callable through the stub. The interpreter caches a method's transformed code by its MonoMethod, and resolves a
    // callee's RuntimeMethod when the caller is transformed (+0x14 of the MonoMethod), so the swap takes only on a
    // method that no transformed caller has resolved yet: a loaded world is too late, OnStartupApp is early enough.
    // The stub form must never run on a method whose +0x14 is nonzero: the stub would inherit that RuntimeMethod,
    // whose method is the target, whose header is then the replacement's, and the replacement would call itself.
    //
    // The spike's target is the fork's PresenterHook.StreamOwnsIdle(Sim): the server's game calls it for every Sim's
    // standing posture (SkipIdleCallbacks, SkipIdleRequest) and, with presenter mode off there, it answers false. The
    // replacement counts the calls and answers through the stub, so the server's behaviour is unchanged and the count
    // shows the swap took. The switch is the package tunable kMethodSwap (Initializer.cs): make-mod writes it True into
    // the package's Current_Tuning when SIMSONLINE_METHOD_SWAP=1 is set on the build (`SIMSONLINE_METHOD_SWAP=1 python
    // scripts/quick_loop.py run --mod --boot`), and the game's tuning loader sets the field before any mod code runs.
    // A boot sends the mod no bridge command before LOAD_SAVE and TS3's runtime has no environment reads, so a runtime
    // tune cannot reach it in time; the tunable swap.stream-owns-idle is read too, for a boot path that one day relays a
    // tune first. Status in SACS_RECORDER status: methodSwap=off|on|skipped:<why>|error:<type>,methodSwapHits=N.
    //
    // The second swap is the player-scope gate on the game's send-home: Sim.MakeSimGoHomeInCarOrBoat's full
    // overload, where every MakeSimGoHome of a Sim with a home lot ends (141 call sites: situations, lot rules, socials,
    // careers). On the server a claimed Sim is not the selected one, so the game treats it as an NPC: placed on a
    // community lot it got GoHome at High priority (ForcePushPriority, non-selectable) within a second, over and over
    // (scratch/GoHomeWhyProbe.cs, run 20261022-031048). The replacement refuses, for a Sim whose household holds a Sim in
    // SET_SIM_SCOPE's claimed set (ServerHostMain.IsScopeHousehold; the household, as the fork's H1 makes the
    // whole household selectable, so a housemate such as Pablo is held like Jennifer), a push below UserDirected (ForcePushPriority's NonCriticalNPCBehavior for a
    // selectable Sim) or at High (its default for a non-selectable one: High ranks above UserDirected, so the first gate,
    // below UserDirected only, let the unselected player's GoHome through, GoHomeGateProbe 2026-10-22). Privacy and the
    // critical levels above it still pass; every other Sim, the server's own NPCs and townies included, goes through the
    // original. On a client the rule is the presenter's instead (Main, 2026-10-22): a puppet's interactions come from the
    // server, so the client's own game never sends one home, whoever it is (the fork's PresenterHook.IsPuppet: presenter
    // on and not the player's own Sim; it answers false on the server, where presenter mode is off).
    // Status: goHomeSwap=off|on|skipped:<why>,goHomeCalls=N,goHomeRefused=N,goHomePuppets=N,goHomeLast=<FirstName>/<id>/<level>.
    internal static class MethodSwap
    {
        internal const string TunableName = "swap.stream-owns-idle";
        private const int kHeader = 40;
        private const int kName = 0x18;
        private const int kCompiled = 0x14;

        public static long Hits;
        private static string sStatus = "off";

        public static long GoHomeCalls;
        public static long GoHomeRefused;
        public static long GoHomePuppets;
        private delegate bool IdTest(ulong id);
        private static IdTest sIsPuppet;
        private static bool sPuppetResolved;
        public static string GoHomeLast = "-"; // the last call's Sim and level (which Sims a client's own game sends home)
        private static string sGoHomeStatus = "off";

        // Receives StreamOwnsIdle's header; its own body never runs after Install.
        public static bool StreamOwnsIdleStub(Sim sim)
        {
            return sim == null && sim != null;
        }

        // Runs under StreamOwnsIdle's MonoMethod: the count, then the original's answer.
        public static bool StreamOwnsIdleHook(Sim sim)
        {
            Hits++;
            return StreamOwnsIdleStub(sim);
        }

        // Receives MakeSimGoHomeInCarOrBoat's header; its own body never runs after Install.
        public static bool GoHomeStub(Sim sim, IOwnableVehicle ownableVehicle, bool pushAsContinuation, InteractionPriority priority,
            Sim.WalkStyle walkStyle, Callback startedCallback, Callback completedCallback, Callback failedCallback)
        {
            return sim == null && sim != null;
        }

        // Runs under MakeSimGoHomeInCarOrBoat's MonoMethod: a claimed Sim goes home only when its player says so.
        public static bool GoHomeHook(Sim sim, IOwnableVehicle ownableVehicle, bool pushAsContinuation, InteractionPriority priority,
            Sim.WalkStyle walkStyle, Callback startedCallback, Callback completedCallback, Callback failedCallback)
        {
            GoHomeCalls++;
            GoHomeLast = (sim != null ? sim.FirstName + "/" + sim.ObjectId.Value.ToString(CultureInfo.InvariantCulture) : "null") + "/" + priority.Level;
            if (sim != null && IsPuppet(sim.ObjectId.Value))
            {
                GoHomePuppets++;
                return false;
            }

            InteractionPriorityLevel level = priority.Level;
            bool engineSendHome = level < InteractionPriorityLevel.UserDirected || level == InteractionPriorityLevel.High;
            if (sim != null && engineSendHome && ServerHostMain.IsScopeHousehold(sim))
            {
                GoHomeRefused++;
                return false;
            }

            return GoHomeStub(sim, ownableVehicle, pushAsContinuation, priority, walkStyle, startedCallback, completedCallback, failedCallback);
        }

        // PresenterHook.IsPuppet, resolved on the first call (the fork's type; no fork: never a puppet).
        internal static bool IsPuppet(ulong simId)
        {
            if (!sPuppetResolved)
            {
                sPuppetResolved = true;
                try
                {
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name != "Sims3GameplaySystems")
                        {
                            continue;
                        }

                        Type hook = asm.GetType("SimsOnline.Hooks.PresenterHook", false);
                        MethodInfo m = hook == null ? null : hook.GetMethod("IsPuppet", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(ulong) }, null);
                        if (m != null)
                        {
                            sIsPuppet = (IdTest)Delegate.CreateDelegate(typeof(IdTest), m);
                        }
                    }
                }
                catch (Exception)
                {
                    sIsPuppet = null;
                }
            }

            try
            {
                return sIsPuppet != null && sIsPuppet(simId);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static string Describe()
        {
            return "methodSwap=" + sStatus + ",methodSwapHits=" + Hits.ToString(CultureInfo.InvariantCulture)
                + ",goHomeSwap=" + sGoHomeStatus + ",goHomeCalls=" + GoHomeCalls.ToString(CultureInfo.InvariantCulture)
                + ",goHomeRefused=" + GoHomeRefused.ToString(CultureInfo.InvariantCulture) + ",goHomePuppets=" + GoHomePuppets.ToString(CultureInfo.InvariantCulture) + ",goHomeLast=" + GoHomeLast;
        }

        private static bool Wanted()
        {
            return Initializer.MethodSwapWanted || Tunables.Get(TunableName, 0.0) >= 1.0;
        }

        private static int RuntimeMethodWord(MethodBase m)
        {
            return Marshal.ReadInt32(m.MethodHandle.Value, kCompiled);
        }

        // Their SwapMethod: the replacement's header over the original's, bytes 0..0x17 and 0x1c..0x27.
        private static void Swap(MethodBase original, MethodBase replacement)
        {
            IntPtr o = original.MethodHandle.Value;
            byte[] repl = new byte[kHeader];
            Marshal.Copy(replacement.MethodHandle.Value, repl, 0, kHeader);
            Marshal.Copy(repl, 0, o, kName);
            Marshal.Copy(repl, kName + 4, new IntPtr(o.ToInt32() + kName + 4), kHeader - kName - 4);
        }

        // OnStartupApp, after the startup commands and before the bridge pump: the swap, when wanted and safe.
        internal static void Install()
        {
            try
            {
                if (!Wanted())
                {
                    sStatus = "off";
                    return;
                }

                sGoHomeStatus = InstallGoHome();
                Type hook = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Sims3GameplaySystems")
                    {
                        hook = asm.GetType("SimsOnline.Hooks.PresenterHook", false);
                    }
                }

                if (hook == null)
                {
                    sStatus = "skipped:no-fork";
                    return;
                }

                MethodInfo target = hook.GetMethod("StreamOwnsIdle", BindingFlags.Public | BindingFlags.Static);
                MethodInfo stub = typeof(MethodSwap).GetMethod("StreamOwnsIdleStub", BindingFlags.Public | BindingFlags.Static);
                MethodInfo repl = typeof(MethodSwap).GetMethod("StreamOwnsIdleHook", BindingFlags.Public | BindingFlags.Static);
                if (target == null || stub == null || repl == null)
                {
                    sStatus = "skipped:no-method";
                    return;
                }

                if (RuntimeMethodWord(target) != 0 || RuntimeMethodWord(stub) != 0)
                {
                    sStatus = "skipped:resolved-already";
                    return;
                }

                Swap(stub, target);
                Swap(target, repl);
                sStatus = "on";
            }
            catch (Exception ex)
            {
                sStatus = "error:" + ex.GetType().Name;
            }
        }

        // The stock game's own method, so no fork is needed; the status for Describe.
        private static string InstallGoHome()
        {
            try
            {
                Type[] args = new Type[]
                {
                    typeof(Sim), typeof(IOwnableVehicle), typeof(bool), typeof(InteractionPriority), typeof(Sim.WalkStyle),
                    typeof(Callback), typeof(Callback), typeof(Callback),
                };
                BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                MethodInfo target = typeof(Sim).GetMethod("MakeSimGoHomeInCarOrBoat", flags, null, args, null);
                MethodInfo stub = typeof(MethodSwap).GetMethod("GoHomeStub", flags, null, args, null);
                MethodInfo repl = typeof(MethodSwap).GetMethod("GoHomeHook", flags, null, args, null);
                if (target == null || stub == null || repl == null)
                {
                    return "skipped:no-method";
                }

                if (RuntimeMethodWord(target) != 0 || RuntimeMethodWord(stub) != 0)
                {
                    return "skipped:resolved-already";
                }

                Swap(stub, target);
                Swap(target, repl);
                return "on";
            }
            catch (Exception ex)
            {
                return "error:" + ex.GetType().Name;
            }
        }
    }
}
