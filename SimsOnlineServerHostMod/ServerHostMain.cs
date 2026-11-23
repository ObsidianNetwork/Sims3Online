using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.InteractionsShared;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.UI;
using Sims3.Gameplay.Objects.Seating;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;
using Sims3.Gameplay.Socializing;
using Sims3.UI.GameEntry;

namespace SimsOnlineServerHost
{
    public static class ServerHostMain
    {
        private static readonly object sSendLock = new object();
        // two-lane outbound. SendLine -> priority (small OK/ERROR/HELLO/EVENT/PROBE);
        // SendLines/SendChunkedSnapshot -> bulk (snapshot chunks). Priority-first dequeue keeps a
        // small admin/command reply from waiting behind a 500+ chunk snapshot burst (the
        // set_game_speed-timeout root cause). Policy lives in the pure OutboundLaneQueue (source-
        // linked into the controller tests); this shell owns sSendLock + the frame writes.
        private static readonly OutboundLaneQueue sPendingOutboundLines = new OutboundLaneQueue();
        private static readonly Dictionary<ulong, ulong> sTrackedInteractionIds = new Dictionary<ulong, ulong>();
        private static readonly Dictionary<ulong, ulong> sAutonomyWatchLastInteractionIds = new Dictionary<ulong, ulong>();
        // (pure-puppet Phase 1): last-seen interaction id per CLAIMED (command-gate-tracked)
        // Sim, so the host emits that Sim's AUTONOMOUS interaction changes to the mirror client. This is
        // SEPARATE from sAutonomyWatchLastInteractionIds (the legacy DISABLE_AUTONOMY watch set): under
        // a claimed Sim keeps free will and is tracked in sCommandGate, NOT the autonomy-watch
        // set, so its host-run autonomous interactions were never emitted -- the client puppet converged
        // by position but ran a divergent interaction (the ReadBook-vs-RouteAway split).
        private static readonly Dictionary<ulong, ulong> sClaimedInteractionLastIds = new Dictionary<ulong, ulong>();
        // seat binding: Sit runs as TransitionInteraction (not the queue head), so track the
        // last emitted transition-Sit id separately from sClaimedInteractionLastIds.
        private static readonly Dictionary<ulong, ulong> sClaimedTransitionSitLastEmittedIds = new Dictionary<ulong, ulong>();
        // (pure-puppet Phase 2): last-EMITTED routing interaction id per UNCLAIMED Sim on a
        // tier-1 (claimed) lot, so the host emits an unclaimed townie's AUTONOMOUS routing (GoHome /
        // VisitCommunityLot / intra-lot Terrain.GoHere) to the mirror and the puppet WALKS instead of
        // teleport-sliding on the position stream. SEPARATE from the claimed set
        // (sClaimedInteractionLastIds) and the legacy DISABLE_AUTONOMY set (sAutonomyWatchLastInteractionIds):
        // neither emit covers an unclaimed townie, so its host-run routes were never mirrored. Keyed on
        // last-EMITTED id (pure dedupe-by-id) rather than seed-then-change, because eligibility itself is
        // gated on "is currently routing" -- the FIRST sight of a new routing id IS the event to emit.
        private static readonly Dictionary<ulong, ulong> sUnclaimedRoutingLastEmittedIds = new Dictionary<ulong, ulong>();

        // command-lifecycle autonomy gate. The controller REGISTERS a claimed Sim
        // (GATE_AUTONOMY) without disabling its autonomy; PollCommandLifecycleGates then watches
        // each registered Sim's queue and engages the gate (IncrementAutonomyDisabled) when a
        // UserDirected interaction appears, disengaging it (DecrementAutonomyDisabled) when the
        // directed queue drains. The gate object owns the WHEN (pure decision core, source-linked
        // into the controller tests) and the registry (which Sims are tracked + their engage bit);
        // this shell owns the side effects. The gate lives in the SHELL so its registry survives
        // RELOAD_HANDLERS. Server-authority only in practice — the controller never
        // registers gates on a client-role mod, so the poll is a no-op there.
        private static readonly AutonomyCommandGate sCommandGate = new AutonomyCommandGate();

        // P3.3 snapshot delta: per-entity change trackers compared on each SendSnapshot.
        // Updated on every snapshot call (so deltas always see the current entity state).
        // Cleared on SESSION_READY so prior-save entity state never contaminates a new session.
        private static readonly Dictionary<ulong, SimChangeTracker> sSimTrackers = new Dictionary<ulong, SimChangeTracker>();
        private static readonly Dictionary<ulong, ObjectChangeTracker> sObjectTrackers = new Dictionary<ulong, ObjectChangeTracker>();

        private sealed class SimChangeTracker
        {
            public Vector3 LastPosition;
            public ulong LastInteractionId;
            public long LastChangedTick;
        }

        private sealed class ObjectChangeTracker
        {
            public Vector3 LastPosition;
            public ulong LastLotId;
            public long LastChangedTick;
        }
        private static readonly SimLifecycleSample sWorldLoadSample = new SimLifecycleSample("worldLoad");
        private static readonly SimLifecycleSample sLoadingScreenDisposedSample = new SimLifecycleSample("loadingScreenDisposed");
        private static readonly SimLifecycleSample sFirstSnapshotRequestSample = new SimLifecycleSample("firstSnapshotRequest");

        private const int MaxChunkPayloadChars = 3600;

        private static bool sInitialized;
        private static bool sBridgePumpStarted;
        private static bool sBridgeTaskScheduled;
        // the idle pump cadence (SACS_RECORDER pump-ms 25..250 changes it at run time); 10ms
        // outbound-backlog scheduling is unchanged. 50 since 2026-10-21: every controller request
        // waits for this tick, so a round trip cost about 600 ms at 250 and about 100 ms at 50, and a late
        // join's history gather stopped the other players' stream for 7 s against 2.5 s. The suite A/B on the
        // server and both clients (arms 20261021-074634) failed alike at both values, and the tick rate held.
        private static int sBridgeIdlePollMs = 50;
        private static bool sStartupProbeSeen;
        // M4 P4.1.0.3: role switching. Default "server" preserves all existing behavior.
        // "client" makes APPLY_POSITION accepted; in server role APPLY_POSITION is rejected.
        private static string sCurrentRole = "server";

        // the GC rate gate (SacsGc) switches on in the server game only.
        internal static bool IsServerRole
        {
            get { return sCurrentRole == "server"; }
        }

        private static bool sWorldLoadProbeSeen;
        private static bool sConnected;
        private static long sControllerSessions; // CONNECTs since the mod loaded (SACS_RECORDER status: sessions)
        private static long sWarmResets; // of them, re-attaches that dropped a presented session (warmResets)
        private static bool sStartupLoadRequested;
        private static bool sLoadRequestPending;
        private static bool sLoadTransitionStarted;
        private static bool sLoadViaMenu;
        private static int sBridgeProbeTicks;
        private static int sDeferredLoadTicks;
        private static Sim sTrackedActiveActor;
        private static List<InteractionInstance> sClientQueueKnownInteractions = new List<InteractionInstance>();
        private static bool sIsProcessingQueueChange;
        private static string sPendingLoadCorrelationId;
        private static string sCurrentSaveName = string.Empty;

        internal static bool IsBridgeLoadActive()
        {
            return sPendingLoadCorrelationId != null || sLoadRequestPending || sLoadTransitionStarted;
        }
        private static string sCurrentSpeed = "Pause";
        // P5 speed-input capture: suppress Gameflow speed polling while applying authoritative
        // SET_GAME_SPEED (mirror echo) or reverting a local UI change.
        private static bool sSuppressSpeedCapture;
        private static string sSpeedCaptureCandidateWire = string.Empty;
        private static int sSpeedCaptureStablePolls;
        // P5.2: ignore UI speed glitches for a few ticks after authoritative SET_GAME_SPEED
        // (join baseline mirror can briefly flash Pause while positions are applying).
        private static int sSpeedCaptureCooldownUntilTick;
        private const int SpeedCaptureCooldownTicks = 8;
        // P5.2: client-role join mirror may arrive before every Sim is in LotManager.Actors.
        private static Dictionary<ulong, PendingPositionApply> sPendingPositionApplies;
        private const int PendingPositionApplyMaxTicks = 240;
        private const int PendingPositionApplyMaxEntries = 128;

        private sealed class PendingPositionApply
        {
            public Vector3 Target;
            public int EnqueuedAtBridgeTick;
        }

        private sealed class SimLifecycleSample
        {
            public string Stage;
            public bool Seen;
            public long Tick;
            public int Actors;
            public int VisibleActors;
            public int SnapshotSims;

            public SimLifecycleSample(string stage)
            {
                Stage = stage;
            }

            public string Encode()
            {
                return (Seen ? "1" : "0") + "," +
                    Tick.ToString(CultureInfo.InvariantCulture) + "," +
                    Actors.ToString(CultureInfo.InvariantCulture) + "," +
                    VisibleActors.ToString(CultureInfo.InvariantCulture) + "," +
                    SnapshotSims.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static void Initialise()
        {
            if (sInitialized)
            {
                return;
            }

            BridgeMemoryBuffer.Initialize();
            World.OnStartupAppEventHandler += new EventHandler(OnStartupApp);
            World.OnWorldLoadFinishedEventHandler += new EventHandler(OnWorldLoadFinished);
            sInitialized = true;
        }

        private static void OnStartupApp(object sender, EventArgs e)
        {
            sStartupProbeSeen = true;
            // record from the first world load on. SacsProxy's instance is set once per app
            // domain, so a recorder installed here sees every driver, including the posture idles Sims
            // acquire while the world loads (installed later, their transitions never reached clients).
            try
            {
                SacsRecorder.Install();
                SlotsRecorder.Install();
                SacsRecorder.DescribeObject = new SacsRecorder.ObjectDescriber(DescribeStreamObject);
                sPresentationModuleStart = PresentationModules.StartPackaged();
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            ProcessStartupCommands();
            MethodSwap.Install(); // before the world loads: a swap takes only on a method no transformed caller resolved yet
            StartBridgePump();
        }

        private static void OnWorldLoadFinished(object sender, EventArgs e)
        {
            sWorldLoadProbeSeen = true;
            RecordSimLifecycleSample(sWorldLoadSample);
            StartBridgePump();
            EventTracker.AddListener(EventTypeId.kLoadingScreenDisposed, new ProcessEventDelegate(LoadingScreenDisposed));
            SendSessionReadyIfPending();
        }

        private static ListenerAction LoadingScreenDisposed(Event e)
        {
            RecordSimLifecycleSample(sLoadingScreenDisposedSample);
            StartBridgePump();
            SendSessionReadyIfPending();

            return ListenerAction.Remove;
        }

        private static void StartBridgePump()
        {
            if (sBridgePumpStarted)
            {
                return;
            }

            sBridgePumpStarted = true;
            ScheduleBridgeTask(0.25f);
        }

        private static void ScheduleBridgeTask(float delaySeconds)
        {
            if (sBridgeTaskScheduled)
            {
                return;
            }

            sBridgeTaskScheduled = true;
            Simulator.AddObject((object)new OneShotFunctionTask(BridgeTaskTick, StopWatch.TickStyles.Seconds, delaySeconds));
        }

        private static void BridgeTaskTick()
        {
            sBridgeTaskScheduled = false;
            RustRoutePlan.InBridgePump = true; // a Plan* reached from the pump must not wait (RustRoutePlan.RequestAndWait)

            try
            {
                // Experiment C, client ring path: lines already in the ring go before any bridge command this tick.
                DrainClientRing();

                // Experiment C, client side: when the ring's lines became visible to the game thread (a pump tick).
                if (SimLinkShadow.ClientEnabled)
                {
                    try
                    {
                        SimLinkShadow.PollInbound();
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }
                }

                BridgeAlarmTick();
            }
            finally
            {
                RustRoutePlan.InBridgePump = false;
                if (sBridgePumpStarted)
                {
                    float delay = sPendingOutboundLines.HasPending ? 0.01f : (sBridgeIdlePollMs / 1000f);
                    ScheduleBridgeTask(delay);
                }
            }
        }

        private static void BridgeAlarmTick()
        {
            sBridgeProbeTicks++;
            ApplyClientStoryProgressionPolicy();
            ApplyServerDialogPolicy();
            PumpStings();
            PumpDialogHook();
            EnsureServerSacsRecorder();
            SacsRecorder.TrackLocal = sCurrentRole == "client";
            if (sCurrentRole == "server")
            {
                try
                {
                    SacsRecorder.ReadLoopTick();
                    RustLiveSimNavBuilder.Tick();
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }

                try
                {
                    SacsRecorder.CopyOnTick();
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }

                try
                {
                    SacsRecorder.WalkerTick(); // option C: walker counters of the drivers introduced since
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }

                try
                {
                    SacsSeedCopy.GcTick();
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
            }

            DrainUserIntents();
            UpdateQueueMirror();

            try
            {
                // refresh control header before inbound reads — compat payloads can stomp
                // bytes 32-63 between pump ticks.
                BridgeMemoryBuffer.EnsureControlHeader();

                // inbound first. The previous shape skipped command reads entirely while
                // ANY outbound backlog existed, so a long chunk stream starved every queued
                // command. Reading first also consumes an unread inbound frame before an
                // outbound write could clobber it.
                int inboundProcessed = 0;
                while (inboundProcessed < 4 && ProcessMemoryCommandOnce())
                {
                    inboundProcessed++;
                }

                BridgeMemoryBuffer.TryCompleteMultiSlotUpgrade();
                DrainOutboundBudgeted();
                ProcessDeferredLoadRequest();
                PollAutonomyWatchQueues();
                PollCommandLifecycleGates();   // command-lifecycle autonomy gate
                PollClaimedSimInteractionEmit();  // emit claimed-Sim AUTONOMOUS interactions to the mirror
                PollUnclaimedRoutingEmit();       // emit UNCLAIMED tier-1 townie AUTONOMOUS routing to the mirror
                PollClientInputInterception();
                PollSpeedWatchdog();
                PollClientPendingPositionApplies();
                // restore the discovery beacon when the controller has gone away so a
                // restarted controller can re-find the buffer (self-guarded staleness checks).
                BridgeMemoryBuffer.TryRearmFindMarker();
            }
            catch (Exception ex)
            {
                SendLine(BridgeCodec.Error("00000000000000000000000000000000", ex.GetType().Name + " " + ex.Message));
            }
        }

        private static DateTime sOutboundBlockedSinceUtc = DateTime.MinValue;

        // consumed-marker discipline. Write the next outbound line only when the shared
        // frame is consumable; busy-drain a bounded budget per tick so a 500-chunk snapshot takes
        // seconds instead of one line per 250ms tick. Legacy fallback: if the controller has
        // NEVER stamped a frame consumed (pre- build), degrade to the lossy write after
        // 500ms instead of stalling the bridge forever. once a consumed stamp has been
        // observed this session, the fallback is disabled permanently — overwriting an unread
        // frame is how command responses vanished, and a stamping controller that stalls will
        // resume (or its short command timeout fails the command cleanly).
        private static void DrainOutboundBudgeted()
        {
            if (!HasPendingOutbound())
            {
                return;
            }

            StopWatch budget = StopWatch.Create(StopWatch.TickStyles.Milliseconds);
            budget.Start();
            int written = 0;
            while (written < 64 && budget.GetElapsedTime() < 25)
            {
                if (!HasPendingOutbound())
                {
                    return;
                }

                if (BridgeMemoryBuffer.CanWriteHostLine())
                {
                    // A server frame may be an unread command; read it before overwriting.
                    ProcessMemoryCommandOnce();
                    if (FlushQueuedOutboundLine())
                    {
                        written++;
                    }
                    sOutboundBlockedSinceUtc = DateTime.MinValue;
                }
                else if (sOutboundBlockedSinceUtc == DateTime.MinValue)
                {
                    sOutboundBlockedSinceUtc = DateTime.UtcNow;
                    BusyWaitMilliseconds(2);
                }
                else if (!BridgeMemoryBuffer.ConsumedStampSeen
                    && (DateTime.UtcNow - sOutboundBlockedSinceUtc).TotalMilliseconds > 500)
                {
                    FlushQueuedOutboundLineUnchecked();
                    written++;
                    sOutboundBlockedSinceUtc = DateTime.MinValue;
                }
                else
                {
                    BusyWaitMilliseconds(2);
                }
            }
        }

        private static bool HasPendingOutbound()
        {
            lock (sSendLock)
            {
                // pending if EITHER lane (priority or bulk) is non-empty.
                return sPendingOutboundLines.HasPending;
            }
        }

        private static void ProcessStartupCommands()
        {
            for (int attempt = 0; attempt < 3000 && !sStartupLoadRequested; attempt++)
            {
                ProcessMemoryCommandOnce();
                // the alarm pump is not running yet, so drain any response a handler
                // had to queue because the slot was not consumable at SendLine time.
                FlushQueuedOutboundLine();
                BridgeMemoryBuffer.TryCompleteMultiSlotUpgrade();
                BusyWaitMilliseconds(10);
            }
        }

        private static bool ProcessMemoryCommandOnce()
        {
            string line = BridgeMemoryBuffer.ReadServerLine();
            if (line == null)
            {
                return false;
            }

            ProcessCommandLine(line);
            return true;
        }

        private static void BusyWaitMilliseconds(uint milliseconds)
        {
            StopWatch stopWatch = StopWatch.Create(StopWatch.TickStyles.Milliseconds);
            stopWatch.Start();
            while (stopWatch.GetElapsedTime() < milliseconds)
            {
            }
        }

        private static void ProcessCommandLine(string line)
        {
            BridgeCommand command = null;

            try
            {
                command = BridgeCodec.DecodeCommand(line);

                // T5a: command dispatch is a table, not an if/else chain. The lookup is
                // the seam T5b/T5d plug into (swap the table at a tick boundary to hot-reload the
                // hot-swappable handlers). Lazy + retryable build (not readonly/Lazy) so a future
                // handler swap that left it null can rebuild — the s&box hotload fossilized-cache
                // lesson ( part 2).
                if (sDispatch == null)
                {
                    sDispatch = BuildDispatch();
                }

                BridgeHandler handler;
                if (sDispatch.TryGetValue(command.Name, out handler))
                {
                    // W2: whatever a server command inserts passes the forked client authority gate
                    // (AuthorityHook); no-op on stock gameplay.
                    BeginServerApply();
                    try
                    {
                        handler(command);
                    }
                    finally
                    {
                        EndServerApply();
                    }
                }
                else
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "Unknown bridge command: " + command.Name));
                }
            }
            catch (Exception ex)
            {
                string correlationId = command == null ? "00000000000000000000000000000000" : command.CorrelationId;
                SendLine(BridgeCodec.Error(correlationId, ex.Message));
            }
        }

        // T5a: the command dispatch table. One entry per wire command; the value is the
        // static handler. Build order groups the categories the hot-reload design ( part 2)
        // splits on: SHELL-PERMANENT (session/load/save/speed/role/snapshot/autonomy/queue/ping) vs
        // HOT-SWAPPABLE (DIAG_* + APPLY_* + their PROBE_SIM_* reads). T5b moves the swappable group
        // into a separate assembly registered through this same table.
        private static Dictionary<string, BridgeHandler> BuildDispatch()
        {
            Dictionary<string, BridgeHandler> d = new Dictionary<string, BridgeHandler>();

            // --- Shell-permanent: session / lifecycle / authority (never hot-swapped) ---
            d["CONNECT"] = HandleConnect;
            d["DISCONNECT"] = HandleDisconnect;
            d["LOAD_SAVE"] = HandleLoadSave;
            d["LOAD_SAVE_MENU"] = HandleLoadSaveViaMenu;
            d["SAVE_GAME"] = HandleSaveGame;
            d["MERGE_LOT_STATE"] = HandleMergeLotState;   // slice 6: lot-boundary reconcile write
            d["IMPORT_HOUSEHOLD_CONTENT"] = HandleImportHouseholdContent;  // live deep-state import
            d["MERGE_TRAVELLING_HOUSEHOLD"] = HandleMergeTravellingHousehold;  // reload-safe travel merge
            d["EXPORT_HOUSEHOLD_CONTENT"] = HandleExportHouseholdContent;
            d["SET_GAME_SPEED"] = HandleSetGameSpeed;
            d["SET_WORLD_TIME"] = HandleSetWorldTime;
            d["REQUEST_SNAPSHOT"] = HandleRequestSnapshot;
            d["BULK_REF"] = HandleBulkRef;
            d["SET_ROLE"] = HandleSetRole;
            d["QUEUE_INTERACTION"] = HandleQueueInteraction;
            d["SET_ACTIVE_SIM"] = HandleSetActiveSim;
            d["CANCEL_INTERACTION"] = HandleCancelInteraction;
            d["LEAVE_CONVERSATION"] = HandleLeaveConversation; // n14, server role
            d["DIALOG_ANSWER"] = HandleDialogAnswer;       //, server role
            d["SHOW_DIALOG"] = HandleShowDialog;           //, client role
            d["DIALOG_AUTOANSWER"] = HandleDialogAutoAnswer; //, tests
            d["DISABLE_AUTONOMY"] = HandleDisableAutonomy;
            d["ENABLE_AUTONOMY"] = HandleEnableAutonomy;
            d["GATE_AUTONOMY"] = HandleGateAutonomy;       // command-lifecycle gate register
            d["UNGATE_AUTONOMY"] = HandleUngateAutonomy;   // command-lifecycle gate clear
            d["SET_SIM_SCOPE"] = HandleSetSimScope;         // W1: player lots/Sims -> forked SimScopeHook
            d["SET_AUTHORITY"] = HandleSetAuthority;        // W2: client authority gate mode + owned Sims
            d["PROBE_SIM_STATE"] = HandleProbeSimState;     // W2 part B: motives + buffs of up to 8 Sims
            d["APPLY_SIM_STATE"] = HandleApplySimState;     // W2 part B: set a mirrored Sim state (client)
            d["PROBE_SIM_DETAILS"] = HandleProbeSimDetails; // job, funds, skills of up to 8 Sims (server)
            d["APPLY_SIM_DETAILS"] = HandleApplySimDetails; // bring a Sim's job, funds, skills to the server's (client)
            d["SET_SIM_ALIASES"] = HandleSetSimAliases;     // W2 identity: server ObjectId -> SimDescriptionId (client)
            d["PROBE_SIM_PRESENCE"] = HandleProbeSimPresence; // W2 identity: journal of Sims entering/leaving the world
            d["SPAWN_SIM"] = HandleSpawnSim;                 // W2 identity: instantiate the server's Sim (client)
            d["DESPAWN_SIM"] = HandleDespawnSim;             // W2 identity: remove a Sim the server removed (client)
            d["EXPORT_SIM_DESCRIPTION"] = HandleExportSimDescription; // one description to a BULK slot (server)
            d["IMPORT_SIM_DESCRIPTION"] = HandleImportSimDescription; // the server's description, same id (client)
            d["PROBE_PRESENTATION"] = HandleProbePresentation; // what each Sim looks like (comparison harness)
            d["SACS_RECORDER"] = HandleSacsRecorder;         // record every animation call (ISacsProxy decorator)
            d["SACS_REPLAY"] = HandleSacsReplay;             // play the server's recorded animation calls (client)
            d["PING"] = HandlePing;
            d["NOTIFY"] = HandleNotify;
            d["NOTICES"] = HandleNotices;                    // the notices this game showed (scenario asserts)

            // --- Shell-permanent: engine-callback binders (CreateInstanceWithCallbacks → shell-
            // private OnInteraction* delegates) + shell-coupled diagnostics. NOT hot-swappable. ---
            d["APPLY_INTERACTION_QUEUE"] = HandleApplyInteractionQueue;
            d["APPLY_INTERACTIONS"] = HandleApplyInteractions;
            d["APPLY_FORCE_ALIGN"] = HandleApplyForceAlign;
            d["INJECT_CLICK"] = HandleInjectClick;
            d["INJECT_READ_INTENT"] = HandleInjectReadIntent;
            d["PROBE_SIM_LIFECYCLE"] = HandleProbeSimLifecycle;
            d["PROBE_STATE_INJECTION"] = HandleProbeStateInjection;
            d["PROBE_ACTOR_LOOKUP"] = HandleProbeActorLookup;

            // NOTE: APPLY_POSITION/MOTIVE/CASH/SKILL/AUTONOMY + PROBE_SIM_MOTIVE/SKILL/AUTONOMY/
            // INTERACTION_QUEUE + PROBE_HOUSEHOLD_CASH + PROBE_SIM_STATUS moved to the swappable
            // handler assembly (T5b Phase B-2) — registered by name from sSwappableCommandNames
            // below, routed to DispatchToHandler.

            // --- Hot-swappable candidates: live-state inspectors + hot-injection (DIAG_*) ---
            d["DIAG_STATE"] = HandleDiagState;
            d["DIAG_CAPTURE_INTERACTION"] = HandleDiagCaptureInteraction;
            d["DIAG_INTERACTION_STATE"] = HandleDiagInteractionState;
            d["DIAG_QUEUE"] = HandleDiagQueue;
            d["DIAG_RESOLVE"] = HandleDiagResolve;
            d["DIAG_WALK"] = HandleDiagWalk;
            d["DIAG_BLOB"] = HandleDiagBlob;
            d["DIAG_LOADTEST"] = HandleDiagLoadTest;
            d["DIAG_RUN"] = HandleDiagRun;
            d["DIAG_RERUN"] = HandleDiagRerun;
            d["DIAG_HANDLERTEST"] = HandleDiagHandlerTest;   // T5b feasibility spike
            d["RELOAD_HANDLERS"] = HandleReloadHandlers;     // T5d live hot-swap

            // --- T5b Phase B-1: commands served by the SWAPPABLE handler assembly. Registered by
            // NAME here (cheap, no Assembly.Load) and routed to DispatchToHandler, which lazy-loads
            // the embedded handler on FIRST USE (post-handshake, on the pump). Loading at boot would
            // run Assembly.Load + emit frames mid-CONNECT and corrupt the handshake. ---
            for (int i = 0; i < sSwappableCommandNames.Length; i++)
            {
                d[sSwappableCommandNames[i]] = DispatchToHandler;
            }

            return d;
        }

        // T5b Phase B-1: the shell-side source of truth for which commands the swappable handler
        // assembly serves. Registered to DispatchToHandler at boot; the handler's own Names() is a
        // cross-check at load time (logged on mismatch), not the registration source. Phase B-2 adds
        // the remaining APPLY_*/PROBE_*/DIAG_* names here as their bodies move to the handler asm.
        private static readonly string[] sSwappableCommandNames = new string[]
        {
            "PROBE_SIM_STATUS",
            "APPLY_POSITION",
            "APPLY_POSITIONS",      // batched position write (N Sims, one frame, one tick)
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

        // T5a: the three formerly-inline dispatch arms, wrapped so every command is a uniform
        // BridgeHandler. Behavior is byte-for-byte what the if/else chain did.
        private static void HandleConnect(BridgeCommand command)
        {
            sConnected = true;

            // a warm re-attach is a new controller session — drop queued outbound from the
            // prior attachment and consume orphan host-ring frames before answering CONNECT.
            lock (sSendLock)
            {
                sPendingOutboundLines.Clear();
            }

            sPendingLoadCorrelationId = null;
            sLoadRequestPending = false;
            sLoadTransitionStarted = false;
            sStartupLoadRequested = false;

            BridgeMemoryBuffer.ClearHostOutboundSlotsForReconnect();

            // A bridge reconnect can resume the same public stream. Only a fresh JoinSession resets the client.
            sControllerSessions++;
            sNavBlobs.Clear();
            RustLiveSimNavBuilder.Reset();
            if (sCurrentRole != "client" && (sObjectAliases.Count > 0 || sReplayDrivers.Count > 0 || sClientEffects.Count > 0))
            {
                sWarmResets++;
                sLastPresentationReset = ResetPresentation();
            }

            // on a warm v11 session (multi-slot already enabled), always answer CONNECT
            // with HELLO|6 on compat0 — do not rely on parts[2] alone (re-attach reads can vary).
            bool requestV11 = BridgeMemoryBuffer.MultiSlotEnabled
                || (command.Parts.Length >= 3
                    && command.Parts[2] == BridgeSharedRegionLayout.ProtocolVersion.ToString(CultureInfo.InvariantCulture));

            if (requestV11)
            {
                string hello = BridgeCodec.HelloV6(SafeWorldFileName(), SafeTick(), BridgeSharedRegionLayout.DefaultHelloCaps);
                if (BridgeMemoryBuffer.MultiSlotEnabled)
                {
                    // Re-attach on a live v11 session: HELLO must land on compat0 (ConnectAsync reads
                    // it before layout refresh), not the host ring.
                    BridgeMemoryBuffer.WriteHostLineCompat(hello);
                }
                else
                {
                    BridgeMemoryBuffer.WriteHostLineCompat(hello);
                    BridgeMemoryBuffer.RequestMultiSlotEnable();
                }

                BridgeMemoryBuffer.EnsureControlHeader();
                return;
            }

            SendLine(BridgeCodec.Hello(SafeWorldFileName(), SafeTick()));
        }

        internal static void DiscardNavigationBlobs()
        {
            sNavBlobs.Clear();
        }

        /// <summary>
        /// follow-up (2026-07-03): graceful controller shutdown. The controller sends
        /// DISCONNECT fire-and-forget just before exiting; it will not consume a response, so
        /// none is written. Controller death becomes an explicit signal instead of being
        /// inferred from 10s of evidence silence — the next attach lands in milliseconds and
        /// never trips the cross-session sequence-dedupe gate.
        /// </summary>
        private static void HandleDisconnect(BridgeCommand command)
        {
            sConnected = false;
            lock (sSendLock)
            {
                sPendingOutboundLines.Clear();
            }

            BridgeMemoryBuffer.NoteControllerDisconnected();
        }

        private static void HandleRequestSnapshot(BridgeCommand command)
        {
            // P3.3: optional SinceTick in parts[2]. Sentinel -1 means full snapshot.
            long sinceTick = -1L;
            if (command.Parts.Length >= 3 && command.Parts[2].Length > 0)
            {
                sinceTick = long.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            }
            SendSnapshot(command.CorrelationId, sinceTick);
        }

        private static void HandlePing(BridgeCommand command)
        {
            // liveness refutation — a live controller answers the re-armed find-marker
            // beacon with PING so queued outbound lines resume immediately. Reading the command
            // already recorded controller evidence.
            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        private static void HandleLoadSave(BridgeCommand command)
        {
            BridgeMemoryBuffer.CompleteMultiSlotUpgradeAfterCommand();
            BridgeMemoryBuffer.NoteControllerActivity();

            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("LOAD_SAVE requires a save name.");
            }

            if (sPendingLoadCorrelationId != null)
            {
                if (sLoadTransitionStarted)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "A save is already loading."));
                    return;
                }

                // duplicate LOAD_SAVE during the deferred-load window (client boot can
                // deliver the same command twice before transition starts) — adopt the new corr id.
                sPendingLoadCorrelationId = command.CorrelationId;
                return;
            }

            string saveName = BridgeCodec.DecodeText(command.Parts[2]);
            string refusal = LoadPreconditionFailure(saveName);
            if (refusal != null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, refusal));
                return;
            }

            sCurrentSaveName = saveName;
            sPendingLoadCorrelationId = command.CorrelationId;
            sStartupLoadRequested = true;
            sLoadRequestPending = true;
            sLoadTransitionStarted = false;
            sLoadViaMenu = false;
            sDeferredLoadTicks = 0;
        }

        // P2.5 load-path comparison probe: mirrors HandleLoadSave but flags the load to go through
        // the menu API (Responder.Instance.MainMenuModel.LoadAndTransitionToSaveGame) rather than
        // the direct GameStates.TransitionToGameStateToInWorld(..., force: true) path.
        // Both paths are required to reach SESSION_READY for the probe to PASS.
        private static void HandleLoadSaveViaMenu(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("LOAD_SAVE_MENU requires a save name.");
            }

            if (sPendingLoadCorrelationId != null)
            {
                if (sLoadTransitionStarted)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "A save is already loading."));
                    return;
                }

                // duplicate LOAD_SAVE during the deferred-load window (client boot can
                // deliver the same command twice before transition starts) — adopt the new corr id.
                sPendingLoadCorrelationId = command.CorrelationId;
                return;
            }

            string saveName = BridgeCodec.DecodeText(command.Parts[2]);
            string refusal = LoadPreconditionFailure(saveName);
            if (refusal != null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, refusal));
                return;
            }

            sCurrentSaveName = saveName;
            sPendingLoadCorrelationId = command.CorrelationId;
            sStartupLoadRequested = true;
            sLoadRequestPending = true;
            sLoadTransitionStarted = false;
            sLoadViaMenu = true;
            sDeferredLoadTicks = 0;
        }

        private static void ProcessDeferredLoadRequest()
        {
            if (!sLoadRequestPending || sLoadTransitionStarted)
            {
                return;
            }

            sDeferredLoadTicks++;
            if (sDeferredLoadTicks < 8)
            {
                return;
            }

            if (Responder.Instance == null)
            {
                return;
            }

            // The game may have moved on during the wait.
            string refusal = LoadPreconditionFailure(sCurrentSaveName);
            if (refusal != null)
            {
                RejectPendingLoad(refusal);
                return;
            }

            if (SafeIsInWorld())
            {
                SendSessionReadyIfPending();
                return;
            }

            BeginSaveLoad();
        }

        private static void BeginSaveLoad()
        {
            SaveGameMetadata metadata = new SaveGameMetadata();
            metadata.mSaveFile = sCurrentSaveName;

            if (!Responder.Instance.MainMenuModel.GetSaveFileDetails(ref metadata))
            {
                RejectPendingLoad("Save file metadata could not be read: " + sCurrentSaveName);
                return;
            }

            sLoadRequestPending = false;
            sLoadTransitionStarted = true;

            if (sLoadViaMenu)
            {
                // P2.5 menu path: Responder.Instance.MainMenuModel.LoadAndTransitionToSaveGame
                // re-checks UIManager.GetSaveGameMetadata internally, then calls
                // GameStates.TransitionToGameStateToInWorld(saveFile, metadata, LiveMode, force: false).
                // It returns false if the save is unreadable or the saveFile string is empty.
                bool menuLoadStarted = Responder.Instance.MainMenuModel.LoadAndTransitionToSaveGame(sCurrentSaveName, metadata);
                if (!menuLoadStarted)
                {
                    RejectPendingLoad("LoadAndTransitionToSaveGame returned false for: " + sCurrentSaveName);
                    return;
                }
            }
            else
            {
                GameStates.TransitionToGameStateToInWorld(sCurrentSaveName, metadata, InWorldState.SubState.LiveMode, true);
            }
        }

        // the claimed Sims whose households every save's join fingerprint covers (SACS_RECORDER fingerprint-scope;
        // null until the controller sends it), and the fingerprint the last save that answered OK took
        // (fingerprint-saved; null after a save that failed or with no scope).
        private static ulong[] sJoinFingerprintScope;
        private static string sSavedJoinFingerprint;

        private static string TakeJoinFingerprint(ulong[] simIds)
        {
            try
            {
                List<Sim> sims = new List<Sim>();
                for (int i = 0; simIds != null && i < simIds.Length; i++)
                {
                    Sim sim = FindSim(simIds[i]);
                    if (sim != null)
                    {
                        sims.Add(sim);
                    }
                }

                return JoinFingerprint.Describe(sims);
            }
            catch (Exception ex)
            {
                return "error=" + ex.GetType().Name + ":" + (ex.Message ?? string.Empty).Replace(';', ',');
            }
        }

        private static void HandleSaveGame(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("SAVE_GAME requires a save name.");
            }

            sSavedJoinFingerprint = null;

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SAVE_GAME requires an in-world session."));
                return;
            }

            if (Responder.Instance == null || Responder.Instance.OptionsModel == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Options model is not available."));
                return;
            }

            string saveName = BridgeCodec.DecodeText(command.Parts[2]);
            OptionsModel optionsModel = Responder.Instance.OptionsModel as OptionsModel;
            if (optionsModel == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Options model does not support synchronous save."));
                return;
            }

            string preconditionFailure = SavePreconditionFailure(optionsModel);
            if (preconditionFailure != null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, preconditionFailure));
                return;
            }

            // Saving the loaded save: use its own id ("Name.sims3"), as the game's Save does (OptionsModel.SaveGame).
            // CreateLoadSaveFileInfo("Name") writes the folder "Name" instead, which the next load of "Name.sims3"
            // never sees (cold-test-075).
            saveName = CurrentSaveIdFor(saveName);
            LoadSaveFileInfo saveInfo = LoadSaveManager.CreateLoadSaveFileInfo(saveName);
            if (saveInfo == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "LoadSaveManager.CreateLoadSaveFileInfo returned null: " + saveName));
                return;
            }

            try
            {
                uint result = LoadSaveManager.SaveGame(saveInfo, GameStates.HasTravelData || GameStates.MovingWorldName != null);
                if (result != 0u)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "LoadSaveManager.SaveGame error code " + result.ToString(CultureInfo.InvariantCulture) + ": " + saveName));
                    return;
                }

                // the join fingerprint of exactly what was just saved (same handler: no simulation step between).
                // It never fails the save: an error is the fingerprint's own text.
                if (sJoinFingerprintScope != null)
                {
                    sSavedJoinFingerprint = TakeJoinFingerprint(sJoinFingerprintScope);
                }

                optionsModel.SaveName = saveName;
                GameStates.LoadFileName = EnsureSims3Extension(saveInfo.ID);
                sCurrentSaveName = saveName;
                SendLine(BridgeCodec.Ok(command.CorrelationId));
            }
            finally
            {
                saveInfo.Dispose();
            }
        }

        /// <summary>
        /// slice 6: MERGE_LOT_STATE|corr|lotId|simId:x:y:z;... — server-role lot-boundary
        /// reconcile write. The controller has already validated ownership and lot scoping
        /// (LotExportValidator), so this handler is a dumb positional merge: SetPosition for every
        /// Sim found on this instance, skip (and count) the rest. Runs before SAVE_GAME so the
        /// canonical server file persists the client-native lot state. Shell-permanent because the
        /// checkpoint gate must not depend on the swappable handler assembly being loaded.
        /// </summary>
        private static void HandleMergeLotState(BridgeCommand command)
        {
            if (command.Parts.Length < 4)
            {
                throw new InvalidOperationException("MERGE_LOT_STATE requires a lot id and a positions payload.");
            }

            if (sCurrentRole != "server")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "MERGE_LOT_STATE rejected: role is '" + sCurrentRole + "' (must be 'server'). The reconcile merge only targets the authoritative instance."));
                return;
            }

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "MERGE_LOT_STATE requires an in-world session."));
                return;
            }

            string payload = command.Parts[3];
            if (payload == null || payload.Length == 0)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "MERGE_LOT_STATE payload is empty."));
                return;
            }

            string[] pairs = payload.Split(';');
            int applied = 0;
            int skipped = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                if (pair.Length == 0)
                {
                    continue;
                }

                string[] fields = pair.Split(':');
                if (fields.Length < 4)
                {
                    skipped++;
                    continue;
                }

                ulong simObjectId;
                float x;
                float y;
                float z;
                try
                {
                    simObjectId = ulong.Parse(fields[0], CultureInfo.InvariantCulture);
                    x = float.Parse(fields[1], CultureInfo.InvariantCulture);
                    y = float.Parse(fields[2], CultureInfo.InvariantCulture);
                    z = float.Parse(fields[3], CultureInfo.InvariantCulture);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                Sim sim = FindSim(simObjectId);
                if (sim == null)
                {
                    // Unlike the client APPLY_POSITIONS path there is no deferred retry queue on
                    // the server role: a Sim missing from the authoritative instance means the
                    // export is stale (Sim left the lot / despawned) and the pair is dropped.
                    skipped++;
                    continue;
                }

                sim.SetPosition(new Vector3(x, y, z));
                applied++;
            }

            // Wire reply is the plain OK the controller round-trip awaits; applied/skipped counts
            // are intentionally not encoded (matches the APPLY_POSITIONS batch contract).
            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        /// <summary>
        /// IMPORT_HOUSEHOLD_CONTENT|corr|householdId|totalBytes|propertyStreamBytes|
        /// resKeyTableBytes|objectIdTableBytes|crc32Hex|entryCount|resKeyCount|objectIdCount
        ///
        /// Live deep-state import. Fetches the uploaded BULK blob (sLastBlobBytes), splits it into
        /// property stream + ResKeyTable + ObjectIdTable, reconstructs the engine objects, finds the
        /// target household, and runs ImportContent + PostImport + PostSimDescriptionLoadFixup.
        /// Uses the same primitives proven by (same-household) and (cross-household).
        /// Shell-permanent: the checkpoint gate must not depend on the swappable handler assembly.
        /// </summary>
        private static void HandleImportHouseholdContent(BridgeCommand command)
        {
            // Wire: IMPORT_HOUSEHOLD_CONTENT|corr|householdId|totalBytes|propertyStreamBytes|
            //       resKeyTableBytes|objectIdTableBytes|crc32Hex|entryCount|resKeyCount|objectIdCount
            if (command.Parts.Length < 11)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "IMPORT_HOUSEHOLD_CONTENT requires householdId|totalBytes|propertyStreamBytes|resKeyTableBytes|objectIdTableBytes|crc32Hex|entryCount|resKeyCount|objectIdCount"));
                return;
            }

            if (sCurrentRole != "server")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "IMPORT_HOUSEHOLD_CONTENT rejected: role is '" + sCurrentRole + "' (must be 'server')."));
                return;
            }

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "IMPORT_HOUSEHOLD_CONTENT requires an in-world session."));
                return;
            }

            ulong householdId;
            int totalBytes;
            int propertyStreamBytes;
            int resKeyTableBytes;
            int objectIdTableBytes;
            uint crc32;
            int entryCount;
            int resKeyCount;
            int objectIdCount;

            try
            {
                householdId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
                totalBytes = int.Parse(command.Parts[3], CultureInfo.InvariantCulture);
                propertyStreamBytes = int.Parse(command.Parts[4], CultureInfo.InvariantCulture);
                resKeyTableBytes = int.Parse(command.Parts[5], CultureInfo.InvariantCulture);
                objectIdTableBytes = int.Parse(command.Parts[6], CultureInfo.InvariantCulture);
                crc32 = uint.Parse(command.Parts[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                entryCount = int.Parse(command.Parts[8], CultureInfo.InvariantCulture);
                resKeyCount = int.Parse(command.Parts[9], CultureInfo.InvariantCulture);
                objectIdCount = int.Parse(command.Parts[10], CultureInfo.InvariantCulture);
            }
            catch
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "IMPORT_HOUSEHOLD_CONTENT parameter parse failed"));
                return;
            }

            // Fetch the uploaded BULK blob.
            if (sLastBlobBytes == null || sLastBlobBytes.Length == 0)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "IMPORT_HOUSEHOLD_CONTENT requires an uploaded BULK blob (send BULK_REF first)."));
                return;
            }

            if (sLastBlobBytes.Length != totalBytes)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "IMPORT_HOUSEHOLD_CONTENT blob size mismatch: expected " + totalBytes.ToString(CultureInfo.InvariantCulture)
                    + " got " + sLastBlobBytes.Length.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            // Verify CRC32.
            uint computedCrc = ComputeCrc32(sLastBlobBytes);
            if (computedCrc != crc32)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "IMPORT_HOUSEHOLD_CONTENT CRC32 mismatch: expected " + crc32.ToString("X8", CultureInfo.InvariantCulture)
                    + " got " + computedCrc.ToString("X8", CultureInfo.InvariantCulture)));
                return;
            }

            // Split the blob into the three segments.
            if (propertyStreamBytes + resKeyTableBytes + objectIdTableBytes != totalBytes)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "IMPORT_HOUSEHOLD_CONTENT segment sizes don't sum to totalBytes"));
                return;
            }

            byte[] propertyStream = new byte[propertyStreamBytes];
            byte[] resKeyTableData = new byte[resKeyTableBytes];
            byte[] objectIdTableData = new byte[objectIdTableBytes];
            Buffer.BlockCopy(sLastBlobBytes, 0, propertyStream, 0, propertyStreamBytes);
            if (resKeyTableBytes > 0)
            {
                Buffer.BlockCopy(sLastBlobBytes, propertyStreamBytes, resKeyTableData, 0, resKeyTableBytes);
            }
            if (objectIdTableBytes > 0)
            {
                Buffer.BlockCopy(sLastBlobBytes, propertyStreamBytes + resKeyTableBytes, objectIdTableData, 0, objectIdTableBytes);
            }

            // Find the target household by ID.
            Household targetHousehold = null;
            foreach (Household h in Household.sHouseholdList)
            {
                if (h != null && h.HouseholdId == householdId)
                {
                    targetHousehold = h;
                    break;
                }
            }

            if (targetHousehold == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "IMPORT_HOUSEHOLD_CONTENT target household " + householdId.ToString(CultureInfo.InvariantCulture) + " not found."));
                return;
            }

            // Capture pre-import SimDescriptionIds (to detect remap).
            System.Collections.Generic.List<ulong> preImportSimDescIds = new System.Collections.Generic.List<ulong>();
            foreach (SimDescription sd in targetHousehold.AllSimDescriptions)
            {
                if (sd != null)
                {
                    preImportSimDescIds.Add(sd.SimDescriptionId);
                }
            }

            // Reconstruct the engine objects from the serialized bytes.
            ResKeyTable resKeys = new ResKeyTable(resKeyTableData);
            ObjectIdTable objIds = new ObjectIdTable(objectIdTableData);

            // Reconstruct the PropertyStreamReader from the property stream.
            MemoryStream readMs = new MemoryStream(propertyStream);
            BinaryReader br = new BinaryReader(readMs);
            PropertyStreamReader reader = new PropertyStreamReader();
            reader.Open(br);

            bool imported = false;
            string importErr = "";
            try
            {
                imported = targetHousehold.ImportContent(resKeys, objIds, reader);
                reader.Close();
            }
            catch (Exception ix)
            {
                importErr = ix.GetType().Name + ":" + ix.Message;
                imported = false;
            }

            // Run the explicit fixup chain (mirrors the real save/load pipeline + probe).
            string fixupErr = "";
            try { targetHousehold.PostImport(); }
            catch (Exception fx) { fixupErr = fixupErr + "postImport:" + fx.GetType().Name + ":" + fx.Message + ";"; }
            try { targetHousehold.PostSimDescriptionLoadFixup(); }
            catch (Exception fx) { fixupErr = fixupErr + "postSimDescFixup:" + fx.GetType().Name + ":" + fx.Message + ";"; }

            // Capture post-import SimDescriptionIds (to report remap).
            System.Collections.Generic.List<ulong> postImportSimDescIds = new System.Collections.Generic.List<ulong>();
            foreach (SimDescription sd in targetHousehold.AllSimDescriptions)
            {
                if (sd != null)
                {
                    postImportSimDescIds.Add(sd.SimDescriptionId);
                }
            }

            // Build the remap report. The controller parses this to verify the import.
            StringBuilder remapReport = new StringBuilder();
            remapReport.Append("{\"imported\":");
            remapReport.Append(imported ? "true" : "false");
            remapReport.Append(",\"householdId\":");
            remapReport.Append(householdId.ToString(CultureInfo.InvariantCulture));
            remapReport.Append(",\"entryCount\":");
            remapReport.Append(entryCount.ToString(CultureInfo.InvariantCulture));
            remapReport.Append(",\"preImportIds\":[");
            for (int i = 0; i < preImportSimDescIds.Count; i++)
            {
                if (i > 0) remapReport.Append(",");
                remapReport.Append(preImportSimDescIds[i].ToString(CultureInfo.InvariantCulture));
            }
            remapReport.Append("],\"postImportIds\":[");
            for (int i = 0; i < postImportSimDescIds.Count; i++)
            {
                if (i > 0) remapReport.Append(",");
                remapReport.Append(postImportSimDescIds[i].ToString(CultureInfo.InvariantCulture));
            }
            remapReport.Append("],\"remappedIds\":[");
            bool first = true;
            for (int i = 0; i < preImportSimDescIds.Count && i < postImportSimDescIds.Count; i++)
            {
                if (preImportSimDescIds[i] != postImportSimDescIds[i])
                {
                    if (!first) remapReport.Append(",");
                    first = false;
                    remapReport.Append("[");
                    remapReport.Append(preImportSimDescIds[i].ToString(CultureInfo.InvariantCulture));
                    remapReport.Append(",");
                    remapReport.Append(postImportSimDescIds[i].ToString(CultureInfo.InvariantCulture));
                    remapReport.Append("]");
                }
            }
            remapReport.Append("]");
            if (importErr.Length > 0)
            {
                remapReport.Append(",\"importErr\":\"");
                remapReport.Append(EscapeJson(importErr));
                remapReport.Append("\"");
            }
            if (fixupErr.Length > 0)
            {
                remapReport.Append(",\"fixupErr\":\"");
                remapReport.Append(EscapeJson(fixupErr));
                remapReport.Append("\"");
            }
            remapReport.Append("}");

            if (imported && fixupErr.Length == 0)
            {
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "ok", remapReport.ToString()));
            }
            else
            {
                string combined = importErr;
                if (fixupErr.Length > 0)
                {
                    combined = combined + ";" + fixupErr;
                }
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "err",
                    "import failed: " + combined + ";report=" + remapReport.ToString()));
            }
        }

        /// <summary>
        /// reload-safe travel-merge handler.
        /// Imports into a FRESH temp household, then merges into existing identities.
        /// NEVER calls ImportContent on the existing live household.
        /// Wire: MERGE_TRAVELLING_HOUSEHOLD|corr|householdId|totalBytes|crc32Hex
        /// </summary>
        private static void HandleMergeTravellingHousehold(BridgeCommand command)
        {
            // Wire: MERGE_TRAVELLING_HOUSEHOLD|corr|householdId|totalBytes|crc32Hex|householdPropBytes|householdResKeyBytes|householdObjIdBytes|inventoryCount
            if (command.Parts.Length < 9)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "MERGE_TRAVELLING_HOUSEHOLD requires householdId|totalBytes|crc32Hex|householdPropBytes|householdResKeyBytes|householdObjIdBytes|inventoryCount"));
                return;
            }

            if (sCurrentRole != "server")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "MERGE_TRAVELLING_HOUSEHOLD rejected: role is '" + sCurrentRole + "' (must be 'server')."));
                return;
            }

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "MERGE_TRAVELLING_HOUSEHOLD requires an in-world session."));
                return;
            }

            ulong householdId;
            int totalBytes;
            uint crc32;
            int householdPropBytes;
            int householdResKeyBytes;
            int householdObjIdBytes;
            int inventoryCount;
            try
            {
                householdId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
                totalBytes = int.Parse(command.Parts[3], CultureInfo.InvariantCulture);
                crc32 = uint.Parse(command.Parts[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                householdPropBytes = int.Parse(command.Parts[5], CultureInfo.InvariantCulture);
                householdResKeyBytes = int.Parse(command.Parts[6], CultureInfo.InvariantCulture);
                householdObjIdBytes = int.Parse(command.Parts[7], CultureInfo.InvariantCulture);
                inventoryCount = int.Parse(command.Parts[8], CultureInfo.InvariantCulture);
            }
            catch
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "MERGE_TRAVELLING_HOUSEHOLD parameter parse failed"));
                return;
            }

            // Fetch the uploaded BULK blob.
            if (sLastBlobBytes == null || sLastBlobBytes.Length == 0)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "MERGE_TRAVELLING_HOUSEHOLD requires an uploaded BULK blob (send BULK_REF first)."));
                return;
            }

            if (sLastBlobBytes.Length != totalBytes)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "MERGE_TRAVELLING_HOUSEHOLD blob size mismatch: expected " + totalBytes.ToString(CultureInfo.InvariantCulture)
                    + " got " + sLastBlobBytes.Length.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            // Verify CRC32.
            uint computedCrc = ComputeCrc32(sLastBlobBytes);
            if (computedCrc != crc32)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "MERGE_TRAVELLING_HOUSEHOLD CRC32 mismatch: expected " + crc32.ToString("X8", CultureInfo.InvariantCulture)
                    + " got " + computedCrc.ToString("X8", CultureInfo.InvariantCulture)));
                return;
            }

            // Find the TARGET household.
            Household targetHousehold = null;
            foreach (Household h in Household.sHouseholdList)
            {
                if (h != null && h.HouseholdId == householdId)
                {
                    targetHousehold = h;
                    break;
                }
            }
            if (targetHousehold == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "MERGE_TRAVELLING_HOUSEHOLD target household " + householdId.ToString(CultureInfo.InvariantCulture) + " not found."));
                return;
            }

            // Slice the blob into household triplet + inventory segments.
            // Layout: [householdProp][householdResKey][householdObjId][inv0Prop][inv0ResKey][inv0ObjId]...
            // Inventory segments follow sequentially after the household triplet.
            // The wire carries householdPropBytes, householdResKeyBytes, householdObjIdBytes, and inventoryCount.
            // For inventoryCount > 0, the remaining bytes after the household triplet are inventory triplets.
            // Each inventory triplet size is determined by reading 3 int32 values prefixing each triplet:
            // [propLen:int32][resKeyLen:int32][objIdLen:int32][propBytes][resKeyBytes][objIdBytes]
            int blobOffset = 0;

            byte[] propertyStream = new byte[householdPropBytes];
            Buffer.BlockCopy(sLastBlobBytes, blobOffset, propertyStream, 0, householdPropBytes);
            blobOffset += householdPropBytes;

            byte[] resKeyTableData = new byte[householdResKeyBytes];
            if (householdResKeyBytes > 0)
            {
                Buffer.BlockCopy(sLastBlobBytes, blobOffset, resKeyTableData, 0, householdResKeyBytes);
                blobOffset += householdResKeyBytes;
            }

            byte[] objectIdTableData = new byte[householdObjIdBytes];
            if (householdObjIdBytes > 0)
            {
                Buffer.BlockCopy(sLastBlobBytes, blobOffset, objectIdTableData, 0, householdObjIdBytes);
                blobOffset += householdObjIdBytes;
            }

            // Step 1-2: Create a FRESH temp household and import the household triplet into it.
            Household tempHousehold = Household.Create();
            ResKeyTable resKeys = new ResKeyTable(resKeyTableData);
            ObjectIdTable objIds = new ObjectIdTable(objectIdTableData);
            MemoryStream readMs = new MemoryStream(propertyStream);
            BinaryReader br = new BinaryReader(readMs);
            PropertyStreamReader reader = new PropertyStreamReader();
            reader.Open(br);

            bool imported = false;
            string importErr = "";
            // set IsTravelImport flag around ImportContent so the engine
            // does not remap SimDescriptionIds (mirrors GameStates.cs:2191 semantics).
            bool prevIsTravelImport = Household.IsTravelImport;
            try
            {
                Household.IsTravelImport = true;
                try
                {
                    imported = tempHousehold.ImportContent(resKeys, objIds, reader);
                    reader.Close();
                }
                catch (Exception ix)
                {
                    importErr = ix.GetType().Name + ":" + ix.Message;
                    imported = false;
                }
            }
            finally
            {
                Household.IsTravelImport = prevIsTravelImport;
            }

            if (!imported)
            {
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "err",
                    "temp household import failed: " + importErr));
                try { tempHousehold.Destroy(); }
                catch
                {
                    // Cleanup only: the import failure was reported just above.
                }
                return;
            }

            // Snapshot both collections before merge operations mutate household membership.
            // MergeTravelInformation and Bin.ImportSim can alter AllSimDescriptions while running.
            // Use ICollection.CopyTo instead of foreach to avoid the version-checking enumerator.
            System.Collections.ICollection importedColl = (System.Collections.ICollection)tempHousehold.AllSimDescriptions;
            SimDescription[] importedArray = new SimDescription[importedColl.Count];
            importedColl.CopyTo(importedArray, 0);
            System.Collections.Generic.List<SimDescription> importedDescriptors =
                new System.Collections.Generic.List<SimDescription>(importedArray.Length);
            for (int i = 0; i < importedArray.Length; i++)
            {
                if (importedArray[i] != null) importedDescriptors.Add(importedArray[i]);
            }
            System.Collections.ICollection targetColl = (System.Collections.ICollection)targetHousehold.AllSimDescriptions;
            SimDescription[] targetArray = new SimDescription[targetColl.Count];
            targetColl.CopyTo(targetArray, 0);
            System.Collections.Generic.List<SimDescription> targetDescriptors =
                new System.Collections.Generic.List<SimDescription>(targetArray.Length);
            for (int i = 0; i < targetArray.Length; i++)
            {
                if (targetArray[i] != null) targetDescriptors.Add(targetArray[i]);
            }

            // Steps 3-5: Match imported descriptors to existing by SimDescriptionId, then merge.
            int matchedCount = 0;
            int mergeErrors = 0;
            string mergeErr = "";
            foreach (SimDescription importedDesc in importedDescriptors)
            {
                if (importedDesc == null) continue;
                SimDescription existingDesc = null;
                foreach (SimDescription ed in targetDescriptors)
                {
                    if (ed != null && ed.SimDescriptionId == importedDesc.SimDescriptionId)
                    {
                        existingDesc = ed;
                        break;
                    }
                }
                if (existingDesc == null) continue;

                matchedCount++;
                try
                {
                    existingDesc.MergeTravelInformation(importedDesc);
                }
                catch (Exception mx)
                {
                    mergeErrors++;
                    mergeErr = mergeErr + mx.GetType().Name + ":" + mx.Message + ";";
                }
                try
                {
                    existingDesc.MergePregnancyInformation(importedDesc);
                }
                catch
                {
                    // Pregnancy merge is best-effort.
                }
                if (existingDesc.CreatedSim != null)
                {
                    try
                    {
                        existingDesc.CreatedSim.BuffManager.LoadBuffsFromTravel(importedDesc);
                    }
                    catch
                    {
                        // Buff merge is best-effort.
                    }
                }
            }

            // Step 6: Call Bin.ImportSim via reflection (internal static) for each matched actor.
            // Bin.ImportSim(SimDescription, Vector3, ulong) restores inventory onto the existing actor.
            // For stage 1, we pass 0 as the inventory content index (no DownloadContent inventory).
            // The MergeTravelInformation call above already copies deep state into existing descriptors.
            string importSimErr = "";
            if (sBinImportSimMethod == null)
            {
                try
                {
                    System.Type binType = typeof(Sims3.Gameplay.Core.Bin);
                    sBinImportSimMethod = binType.GetMethod("ImportSim",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                }
                catch (Exception bs)
                {
                    importSimErr = "Bin.ImportSim lookup failed: " + bs.GetType().Name + ":" + bs.Message;
                }
            }
            if (sBinImportSimMethod != null)
            {
                foreach (SimDescription importedDesc in importedDescriptors)
                {
                    if (importedDesc == null) continue;
                    SimDescription existingDesc = null;
                    foreach (SimDescription ed in targetDescriptors)
                    {
                        if (ed != null && ed.SimDescriptionId == importedDesc.SimDescriptionId)
                        {
                            existingDesc = ed;
                            break;
                        }
                    }
                    if (existingDesc == null) continue;
                    try
                    {
                        sBinImportSimMethod.Invoke(null,
                            new object[] { existingDesc, Sims3.SimIFace.Vector3.OutOfWorld, 0UL });
                    }
                    catch (Exception isx)
                    {
                        importSimErr = importSimErr + isx.GetType().Name + ":" + isx.Message + ";";
                    }
                }
            }
            // Step 7: Call Household.MergeTravelData via reflection (internal instance).
            string travelDataErr = "";
            try
            {
                if (sHouseholdMergeTravelDataMethod == null)
                {
                    sHouseholdMergeTravelDataMethod = typeof(Household).GetMethod(
                        "MergeTravelData",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                }
                if (sHouseholdMergeTravelDataMethod != null)
                {
                    sHouseholdMergeTravelDataMethod.Invoke(targetHousehold, new object[] { tempHousehold });
                }
                else
                {
                    travelDataErr = "MergeTravelData method not found via reflection";
                }
            }
            catch (Exception td)
            {
                travelDataErr = td.GetType().Name + ":" + td.Message;
            }

            // Step 9: Run PostImport fixups on the TARGET household.
            string fixupErr = "";
            try { targetHousehold.PostImport(); }
            catch (Exception fx) { fixupErr = fixupErr + "postImport:" + fx.GetType().Name + ":" + fx.Message + ";"; }
            try { targetHousehold.PostSimDescriptionLoadFixup(); }
            catch (Exception fx) { fixupErr = fixupErr + "postSimDescFixup:" + fx.GetType().Name + ":" + fx.Message + ";"; }

            // Step 10: Dispose imported temp descriptors and destroy temp household.
            // Use the snapshotted list, not AllSimDescriptions (which may have been modified by merge ops).
            for (int di = 0; di < importedDescriptors.Count; di++)
            {
                SimDescription importedDesc = importedDescriptors[di];
                if (importedDesc != null)
                {
                    try { importedDesc.DisposeTravelImportedSimDesc(); }
                    catch { SwallowedErrors.Note(); }
                }
            }
            try { tempHousehold.Destroy(); }
            catch { SwallowedErrors.Note(); }

            // Build the report.
            StringBuilder report = new StringBuilder();
            report.Append("{\"travelMerge\":true");
            report.Append(",\"householdId\":");
            report.Append(householdId.ToString(CultureInfo.InvariantCulture));
            report.Append(",\"matched\":");
            report.Append(matchedCount.ToString(CultureInfo.InvariantCulture));
            report.Append(",\"mergeErrors\":");
            report.Append(mergeErrors.ToString(CultureInfo.InvariantCulture));
            report.Append(",\"funds\":");
            report.Append(targetHousehold.FamilyFunds.ToString(CultureInfo.InvariantCulture));
            if (mergeErr.Length > 0)
            {
                report.Append(",\"mergeErr\":\"");
                report.Append(EscapeJson(mergeErr));
                report.Append("\"");
            }
            if (travelDataErr.Length > 0)
            {
                report.Append(",\"travelDataErr\":\"");
                report.Append(EscapeJson(travelDataErr));
                report.Append("\"");
            }
            if (fixupErr.Length > 0)
            {
                report.Append(",\"fixupErr\":\"");
                report.Append(EscapeJson(fixupErr));
                report.Append("\"");
            }
            report.Append("}");

            bool ok = matchedCount > 0 && mergeErrors == 0;
            if (ok)
            {
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "ok", report.ToString()));
            }
            else
            {
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "err",
                    "travel-merge failed: matched=" + matchedCount.ToString(CultureInfo.InvariantCulture)
                    + ";errors=" + mergeErrors.ToString(CultureInfo.InvariantCulture)
                    + ";report=" + report.ToString()));
            }
        }

        private static void HandleExportHouseholdContent(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "EXPORT_HOUSEHOLD_CONTENT requires a householdId."));
                return;
            }

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "EXPORT_HOUSEHOLD_CONTENT requires an in-world session."));
                return;
            }

            ulong householdId;
            try
            {
                householdId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            }
            catch
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "EXPORT_HOUSEHOLD_CONTENT householdId parse failed."));
                return;
            }

            Household household = FindHousehold(householdId);
            if (household == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "EXPORT_HOUSEHOLD_CONTENT household " + householdId.ToString(CultureInfo.InvariantCulture) + " not found."));
                return;
            }

            if (!BridgeMemoryBuffer.MultiSlotEnabled)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "EXPORT_HOUSEHOLD_CONTENT requires the bridge v11 BULK region."));
                return;
            }

            PropertyStreamWriter writer = null;
            try
            {
                MemoryStream stream = new MemoryStream();
                BinaryWriter binaryWriter = new BinaryWriter(stream);
                writer = new PropertyStreamWriter();
                writer.Open(binaryWriter);

                // Patch Genealogy mSim back-references before export.
                // Genealogy.ExportContent NREs when mSpouse is non-null but mSpouse.IMiniSimDescription
                // returns null (both mSim and mMiniSim null on spouse's Genealogy).
                PatchGenealogyBackReferences(household);
                ResKeyTable resKeys = new ResKeyTable();
                ObjectIdTable objectIds = new ObjectIdTable();
                bool exported = household.ExportContent(resKeys, objectIds, writer);
                int entryCount = writer.Count;
                writer.Close();
                writer = null;
                binaryWriter.Flush();

                if (!exported)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId,
                        "EXPORT_HOUSEHOLD_CONTENT Household.ExportContent returned false."));
                    return;
                }

                byte[] propertyStream = stream.ToArray();
                byte[] resKeyTable = resKeys.Serialize();
                byte[] objectIdTable = objectIds.Serialize();
                int totalBytes = propertyStream.Length + resKeyTable.Length + objectIdTable.Length;

                if (totalBytes <= 0)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId,
                        "EXPORT_HOUSEHOLD_CONTENT produced an empty payload."));
                    return;
                }

                if (totalBytes > BridgeSharedRegionLayout.BulkSlotPayloadMax)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId,
                        "EXPORT_HOUSEHOLD_CONTENT payload exceeds BULK slot capacity: "
                        + totalBytes.ToString(CultureInfo.InvariantCulture) + " > "
                        + BridgeSharedRegionLayout.BulkSlotPayloadMax.ToString(CultureInfo.InvariantCulture)));
                    return;
                }

                // assemble full payload (household + inventories) BEFORE publishing.
                // The original code published the household-only payload first, then re-published
                // after appending inventories — leaking one HostReady slot per export call.
                byte[] payload = new byte[totalBytes];
                Buffer.BlockCopy(propertyStream, 0, payload, 0, propertyStream.Length);
                Buffer.BlockCopy(resKeyTable, 0, payload, propertyStream.Length, resKeyTable.Length);
                Buffer.BlockCopy(objectIdTable, 0, payload,
                    propertyStream.Length + resKeyTable.Length, objectIdTable.Length);


                // export per-Sim inventories + shared inventories.
                System.Collections.Generic.List<string> invSegments = new System.Collections.Generic.List<string>();
                int inventoryCount = 0;

                // Per-Sim inventories.
                int simSlotIndex = 0;
                foreach (SimDescription sd in household.AllSimDescriptions)
                {
                    if (sd == null) continue;
                    Sim sim = sd.CreatedSim;
                    if (sim == null) { simSlotIndex++; continue; }
                    Inventory inv = sim.Inventory;
                    if (inv == null || inv.IsEmpty) { simSlotIndex++; continue; }

                    ResKeyTable invResKeys = new ResKeyTable();
                    ObjectIdTable invObjIds = new ObjectIdTable();
                    MemoryStream invMs = new MemoryStream();
                    BinaryWriter invBw = new BinaryWriter(invMs);
                    PropertyStreamWriter invWriter = new PropertyStreamWriter();
                    invWriter.Open(invBw);
                    bool invOk = inv.ExportContent(invResKeys, invObjIds, invWriter);
                    invWriter.Close();
                    invBw.Flush();
                    byte[] invProp = invMs.ToArray();
                    byte[] invResKeysBytes = invResKeys.Serialize();
                    byte[] invObjIdsBytes = invObjIds.Serialize();

                    if (invOk && invProp.Length > 0)
                    {
                        int invTotal = invProp.Length + invResKeysBytes.Length + invObjIdsBytes.Length;
                        invSegments.Add(simSlotIndex.ToString(CultureInfo.InvariantCulture)
                            + "," + invProp.Length.ToString(CultureInfo.InvariantCulture)
                            + "," + invResKeysBytes.Length.ToString(CultureInfo.InvariantCulture)
                            + "," + invObjIdsBytes.Length.ToString(CultureInfo.InvariantCulture));
                        inventoryCount++;

                        // Append to payload.
                        int oldLen = payload.Length;
                        byte[] newPayload = new byte[oldLen + invTotal];
                        Buffer.BlockCopy(payload, 0, newPayload, 0, oldLen);
                        Buffer.BlockCopy(invProp, 0, newPayload, oldLen, invProp.Length);
                        Buffer.BlockCopy(invResKeysBytes, 0, newPayload, oldLen + invProp.Length, invResKeysBytes.Length);
                        Buffer.BlockCopy(invObjIdsBytes, 0, newPayload, oldLen + invProp.Length + invResKeysBytes.Length, invObjIdsBytes.Length);
                        payload = newPayload;
                        totalBytes = payload.Length;
                    }
                    simSlotIndex++;
                }

                // Re-check total size after inventories.
                if (totalBytes > BridgeSharedRegionLayout.BulkSlotPayloadMax)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId,
                        "EXPORT_HOUSEHOLD_CONTENT payload with inventories exceeds BULK slot capacity: "
                        + totalBytes.ToString(CultureInfo.InvariantCulture) + " > "
                        + BridgeSharedRegionLayout.BulkSlotPayloadMax.ToString(CultureInfo.InvariantCulture)));
                    return;
                }

                // single publish after full assembly (household + inventories).
                // No re-publish needed — the first and only publish carries the final payload.
                if (!BridgeBulkBuffer.HasWritableHostSlots(1))
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId,
                        "EXPORT_HOUSEHOLD_CONTENT BULK slot unavailable; unread host payloads were preserved."));
                    return;
                }

                int slotIndex;
                uint crc32;

                if (!BridgeBulkBuffer.TryPublishHostBulk(command.CorrelationId, payload, out slotIndex, out crc32))
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId,
                        "EXPORT_HOUSEHOLD_CONTENT BULK publication failed."));
                    return;
                }

                string invSegmentStr = string.Join("|", invSegments.ToArray());
                if (inventoryCount > 0)
                {
                    SendLine(BridgeCodec.ExportHouseholdContentReportV2(
                        command.CorrelationId,
                        householdId,
                        slotIndex,
                        totalBytes,
                        propertyStream.Length,
                        resKeyTable.Length,
                        objectIdTable.Length,
                        crc32,
                        entryCount,
                        resKeys.Count,
                        objectIds.Count,
                        inventoryCount,
                        invSegmentStr));
                }
                else
                {
                    SendLine(BridgeCodec.ExportHouseholdContentReport(
                        command.CorrelationId,
                        householdId,
                        slotIndex,
                        totalBytes,
                        propertyStream.Length,
                        resKeyTable.Length,
                        objectIdTable.Length,
                        crc32,
                        entryCount,
                        resKeys.Count,
                        objectIds.Count));
                }
            }
            catch (Exception ex)
            {
                if (writer != null)
                {
                    try { writer.Close(); }
                    catch
                    {
                        // Cleanup only: the export error is reported just below.
                    }
                }

                SendLine(BridgeCodec.Error(command.CorrelationId,
                    "EXPORT_HOUSEHOLD_CONTENT failed: " + ex.GetType().Name + ": " + ex.Message));
            }
        }

        /// <summary>
        /// Before calling Household.ExportContent, ensure every Genealogy object
        /// in the household has its mSim back-reference set. The engine's Genealogy.ExportContent
        /// dereferences mSpouse.IMiniSimDescription.SimDescriptionId without null-checking
        /// IMiniSimDescription, which NREs when the spouse's Genealogy has both mSim and mMiniSim
        /// null (a detached stub created during marriage without a back-reference).
        /// </summary>
        private static void PatchGenealogyBackReferences(Household household)
        {
            try
            {
                BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo mSimField = typeof(Genealogy).GetField("mSim", bf);
                FieldInfo mGenealogyField = typeof(SimDescription).GetField("mGenealogy", bf);
                if (mSimField == null || mGenealogyField == null) return;

                foreach (SimDescription sd in household.AllSimDescriptions)
                {
                    if (sd == null) continue;
                    Genealogy gen = (Genealogy)mGenealogyField.GetValue(sd);
                    if (gen == null) continue;

                    // Set mSim back-reference on this SimDescription's Genealogy
                    if (mSimField.GetValue(gen) == null)
                    {
                        mSimField.SetValue(gen, sd);
                    }

                    // Also patch the spouse's Genealogy if it exists
                    FieldInfo mSpouseField = typeof(Genealogy).GetField("mSpouse", bf);
                    if (mSpouseField != null)
                    {
                        Genealogy spouse = (Genealogy)mSpouseField.GetValue(gen);
                        if (spouse != null && mSimField.GetValue(spouse) == null)
                        {
                            // Find the spouse's SimDescription in the household
                            SimDescription spouseSd = FindSpouseSimDescription(household, gen, mSpouseField, mGenealogyField, bf);
                            if (spouseSd != null)
                            {
                                mSimField.SetValue(spouse, spouseSd);
                            }
                        }
                    }
                }
            }
            catch { SwallowedErrors.Note(); }
        }

        private static SimDescription FindSpouseSimDescription(Household household, Genealogy gen,
            FieldInfo mSpouseField, FieldInfo mGenealogyField, BindingFlags bf)
        {
            // Walk the household's SimDescriptions to find one whose Genealogy's mSpouse points back to gen
            foreach (SimDescription sd in household.AllSimDescriptions)
            {
                if (sd == null) continue;
                Genealogy sdGen = (Genealogy)mGenealogyField.GetValue(sd);
                if (sdGen == null) continue;
                Genealogy sdSpouse = (Genealogy)mSpouseField.GetValue(sdGen);
                if (sdSpouse == gen) return sd;
            }
            return null;
        }

        private static uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1u) == 1u)
                    {
                        crc = (crc >> 1) ^ 0xEDB88320u;
                    }
                    else
                    {
                        crc = crc >> 1;
                    }
                }
            }
            return ~crc;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string EnsureSims3Extension(string saveName)
        {
            if (saveName == null)
            {
                return string.Empty;
            }

            if (saveName.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase))
            {
                return saveName;
            }

            return saveName + ".sims3";
        }

        // The loaded save's id when saveName names it (with or without ".sims3"); otherwise saveName (a save-as).
        private static string CurrentSaveIdFor(string saveName)
        {
            string id = CurrentSaveId();
            if (id != null && string.Equals(StripSims3(id), StripSims3(saveName), StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }

            return saveName;
        }

        // The loaded save's id ("Name.sims3"), or null when the game has none or cannot say.
        private static string CurrentSaveId()
        {
            LoadSaveFileInfo current = null;
            try
            {
                current = LoadSaveManager.GetCurrentSaveGameInfo();
                return current == null ? null : current.ID;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (current != null)
                {
                    current.Dispose();
                }
            }
        }

        private static string StripSims3(string name)
        {
            return name.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 6) : name;
        }

        private static string SavePreconditionFailure(OptionsModel optionsModel)
        {
            if (!SafeIsInWorld())
            {
                return "SAVE_GAME requires an in-world session.";
            }

            string disabledReason;
            if (((Sims3.Gameplay.Gameflow)Sims3.Gameplay.Gameflow.Singleton).IsSaveDisabled(out disabledReason))
            {
                return "Gameflow reports save disabled: " + SafeString(disabledReason);
            }

            if (!optionsModel.CanSaveGame())
            {
                return "OptionsModel.CanSaveGame=false; IsInSaveGameState=" + optionsModel.IsInSaveGameState().ToString() + "; InMainMenuState=" + optionsModel.InMainMenuState().ToString() + "; SaveGameInProgress=" + optionsModel.SaveGameInProgress.ToString();
            }

            if (optionsModel.SaveGameInProgress)
            {
                return "OptionsModel.SaveGameInProgress=true before SAVE_GAME.";
            }

            // CanSaveGame lets edit town, play flow and shopping through; NRaas Saver saves only in live mode
            // (IsValidState). No modal-window check (the game's own OptionsModel.SaveGameSynchronous has one): the
            // server's world keeps a modal window open, so every late join's checkpoint waited 20 s and failed (Session H,
            // 21 October), and LoadSaveManager.SaveGame does not need one.
            InWorldSubState subState = GameStates.GetInWorldSubState();
            if (!(subState is LiveModeState))
            {
                return "SAVE_GAME requires live mode; the sub-state is " + (subState == null ? "none" : subState.GetType().Name) + ".";
            }

            return null;
        }

        // what LOAD_SAVE / LOAD_SAVE_MENU cannot do. In-world, a load of the save already loaded answers
        // SESSION_READY (a re-attach), but a load of another save used to answer it too, for the world the game was still
        // in. A game leaving its world, quitting or travelling cannot start a load. null: the load can go ahead (also when
        // the game cannot say, early in its startup).
        private static string LoadPreconditionFailure(string saveName)
        {
            if (SafeIsInWorld())
            {
                string loaded = CurrentSaveId();
                if (loaded != null && !string.Equals(StripSims3(loaded), StripSims3(saveName), StringComparison.OrdinalIgnoreCase))
                {
                    return "LOAD_SAVE " + saveName + " refused: the game is in the world of " + loaded + "; another save needs a restart.";
                }

                return null;
            }

            try
            {
                if (GameStates.IsGameShuttingDown)
                {
                    return "LOAD_SAVE refused: the game is leaving its world or quitting.";
                }

                if (GameStates.IsTravelling)
                {
                    return "LOAD_SAVE refused: the game is travelling.";
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return null;
        }

        private static void HandleSetGameSpeed(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("SET_GAME_SPEED requires a speed.");
            }

            Sims3.SimIFace.Gameflow.GameSpeed speed = ParseSpeed(command.Parts[2]);
            sSuppressSpeedCapture = true;
            try
            {
                Sims3.Gameplay.Gameflow.SetGameSpeed(speed, Sims3.Gameplay.Gameflow.SetGameSpeedContext.GameStates);
                sCurrentSpeed = command.Parts[2];
            }
            finally
            {
                sSuppressSpeedCapture = false;
            }
            sSpeedCaptureCandidateWire = string.Empty;
            sSpeedCaptureStablePolls = 0;
            sSpeedCaptureCooldownUntilTick = sBridgeProbeTicks + SpeedCaptureCooldownTicks;
            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        private static void HandleSetWorldTime(BridgeCommand command)
        {
            if (command.Parts.Length < 5)
            {
                throw new InvalidOperationException("SET_WORLD_TIME requires day, hours, and minutes.");
            }

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SET_WORLD_TIME requires an in-world session."));
                return;
            }

            int day = int.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            int hours = int.Parse(command.Parts[3], CultureInfo.InvariantCulture);
            int minutes = int.Parse(command.Parts[4], CultureInfo.InvariantCulture);
            if (day < 0 || day > 6)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SET_WORLD_TIME day must be 0..6 (Sunday..Saturday)."));
                return;
            }

            if (hours < 0 || hours > 23)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SET_WORLD_TIME hours must be 0..23."));
                return;
            }

            if (minutes < 0 || minutes > 59)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SET_WORLD_TIME minutes must be 0..59."));
                return;
            }

            // by the shortest way within the week, as a move of StartTime so the week stays (WorldClockSet);
            // SimClockUtils.SetStartTime always landed in week 0. A game that already reads that minute is left alone.
            long delta = WorldClockSet.DeltaTicks(SimClock.CurrentTicks, day, hours, minutes);
            if (delta == 0)
            {
                sWorldClockSetsSkipped++;
                SendLine(BridgeCodec.Ok(command.CorrelationId));
                return;
            }

            SimClock.StartTime = new DateAndTime(SimClock.StartTime.Ticks + delta);
            sWorldClockSets++;
            sWorldClockLastDeltaTicks = delta;
            RefreshUiClocks();
            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // SET_WORLD_TIME counts for DIAG: sets that moved the clock, sets the game already matched, and the
        // last move in ticks (negative: back).
        private static int sWorldClockSets;
        private static int sWorldClockSetsSkipped;
        private static long sWorldClockLastDeltaTicks;

        // the HUD clock's caption changes only when HudModel's repeating minute alarm fires (HudModel.cs:3161,
        // TimeControl.OnMinuteChanged), and Responder's minute tick drives the Sim panel and the moodlet tooltips
        // (Responder.cs:413). Alarms keep absolute due times (AlarmManager.Simulate), so a clock moved back holds both
        // for as long as it moved. After a set, both fire just after the next minute starts and the caption repaints
        // now. Private engine members, so a name the game lacks leaves the set as it is.
        private static void RefreshUiClocks()
        {
            try
            {
                long now = SimClock.CurrentTicks;
                long nextMinute = WorldClockSet.MinuteStartTicks(((now * 2) / WorldClockSet.TicksPerTwoMinutes) + 1);
                float wait = (nextMinute - now + 1) / 37.5f;
                object hud = Sims3.UI.Responder.Instance == null ? null : Sims3.UI.Responder.Instance.HudModel;
                FieldInfo clockAlarm = hud == null ? null : hud.GetType().GetField("mUpdateClockAlarm", BindingFlags.Instance | BindingFlags.NonPublic);
                if (clockAlarm != null)
                {
                    AlarmManager.Global.SetNewAlarmTime((AlarmHandle)clockAlarm.GetValue(hud), wait, TimeUnit.Minutes);
                }

                object responder = Sims3.UI.Responder.Instance;
                FieldInfo minuteTick = responder == null ? null : responder.GetType().GetField("sOnceAMinuteAlarmHandle", BindingFlags.Static | BindingFlags.NonPublic);
                if (minuteTick != null)
                {
                    AlarmManager.Global.SetNewAlarmTime((AlarmHandle)minuteTick.GetValue(null), wait, TimeUnit.Minutes);
                }

                MethodInfo repaint = hud == null ? null : hud.GetType().GetMethod("OnMinuteChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                if (repaint != null)
                {
                    repaint.Invoke(hud, null);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // game minutes until the HUD clock's caption next changes (its minute alarm); 0 to 1 when it ticks,
        // more when the clock was moved back past it; -1 when the HUD or the member is missing.
        private static float HudClockDueMinutes()
        {
            try
            {
                object hud = Sims3.UI.Responder.Instance == null ? null : Sims3.UI.Responder.Instance.HudModel;
                FieldInfo clockAlarm = hud == null ? null : hud.GetType().GetField("mUpdateClockAlarm", BindingFlags.Instance | BindingFlags.NonPublic);
                if (clockAlarm == null)
                {
                    return -1f;
                }

                return AlarmManager.Global.GetTimeLeft((AlarmHandle)clockAlarm.GetValue(hud), TimeUnit.Minutes);
            }
            catch (Exception)
            {
                return -1f;
            }
        }

        // a SocializingPosture with no running (or transitioning) interaction is an
        // orphan — the social died without popping posture (live-proven cause:'s
        // claim-time CancelAllInteractions killing a boot-time chat) — and every later
        // interaction then fails its posture transition at start in ~15ms with the empty-key
        // "Interaction callback failed" signature (the claimed-Sim freeze). The Posture setter
        // unwinds stale postures via OnExitPosture (null selects Standing). A transient
        // social-unwind window is indistinguishable from an orphan here, but the reset is
        // exactly what the pending player command's posture transition would do anyway.
        // Live-proven 2026-06-12: frozen Jennifer Martinez walked immediately after reset.
        internal static bool RepairOrphanedSocializingPosture(Sim actor)
        {
            if (actor == null || actor.Posture == null || actor.InteractionQueue == null)
            {
                return false;
            }
            if (actor.InteractionQueue.RunningInteraction != null)
            {
                return false;
            }
            if (actor.InteractionQueue.TransitionInteraction != null)
            {
                return false;
            }
            if (actor.Posture.GetType().Name != "SocializingPosture")
            {
                return false;
            }
            actor.Posture = null;
            return true;
        }

        // client puppet may still be SittingPosture when the host starts SocializeWith.
        // Queue Stand first so the terminal social approach can run.
        private static void TryQueueStandBeforeSocialApproach(Sim actor)
        {
            if (actor == null || actor.Posture == null || actor.InteractionQueue == null)
            {
                return;
            }

            if (actor.Posture.GetType().Name != "SittingPosture")
            {
                return;
            }

            try
            {
                InteractionInstance stand = Stand.Singleton.CreateInstanceWithCallbacks(
                    actor,
                    actor,
                    new InteractionPriority(InteractionPriorityLevel.UserDirected),
                    false,
                    true,
                    new Callback(OnInteractionStarted),
                    new Callback(OnInteractionCompleted),
                    new Callback(OnInteractionFailed)) as InteractionInstance;

                if (stand == null)
                {
                    return;
                }

                sIsProcessingQueueChange = true;
                try
                {
                    actor.InteractionQueue.AddNext(stand);
                }
                finally
                {
                    sIsProcessingQueueChange = false;
                }
            }
            catch
            {
                SwallowedErrors.Note();
            }
        }

        // host moved to social while the client puppet still has a stale book/sit chain.
        private static void TryClearBlockingPuppetActivity(Sim actor)
        {
            if (actor == null || actor.InteractionQueue == null)
            {
                return;
            }

            try
            {
                InteractionInstance running = actor.InteractionQueue.RunningInteraction;
                if (running == null || running.InteractionDefinition == null)
                {
                    return;
                }

                string defName = running.InteractionDefinition.GetType().FullName;
                if (defName == null)
                {
                    return;
                }

                if (defName.IndexOf("ReadBook") >= 0
                    || defName.IndexOf("GetBook") >= 0
                    || defName.IndexOf("InteractionsShared.Sit") >= 0
                    || defName.IndexOf("InteractionsShared.Stand") >= 0
                    || defName.IndexOf("GoHere") >= 0)
                {
                    actor.InteractionQueue.CancelAllInteractions();
                    if (actor.Posture != null && actor.Posture.GetType().Name == "SittingPosture")
                    {
                        actor.Posture = null;
                    }
                }
            }
            catch
            {
                SwallowedErrors.Note();
            }
        }

        // follow-up (off-lot y=0 GoHere): this queue path bypasses the engine's
        // definition-level routability test, so a wrong destination height dies at
        // mRoute.Plan() ~250ms after start with the generic empty-key failure. The engine
        // snaps positions to terrain height before routing (Sim.cs SetPosition paths,
        // GameObject ctor) — mirror that here. Live-proven: y=0 to a 60.5-height lot failed;
        // terrain-height y to the same x/z completed. Tolerance 0.5 keeps clicked
        // destinations (already engine-resolved) byte-identical.
        private static Vector3 SnapDestinationToTerrain(Vector3 destination)
        {
            float terrainY = World.GetTerrainHeight(destination.x, destination.z);
            if (Math.Abs(destination.y - terrainY) > 0.5f)
            {
                destination.y = terrainY;
            }
            return destination;
        }

        private static void HandleQueueInteraction(BridgeCommand command)
        {
            if (command.Parts.Length < 9)
            {
                throw new InvalidOperationException("QUEUE_INTERACTION requires actor, key, target, position flag, and coordinates.");
            }

            if (command.Parts.Length > 12)
            {
                HandleQueueUserInteraction(command);
                return;
            }

            ulong actorObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            string interactionKey = BridgeCodec.DecodeText(command.Parts[3]);
            if (interactionKey != "Terrain.GoHere" && !interactionKey.Contains("Terrain+GoHere"))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Unsupported interaction key: " + interactionKey));
                return;
            }

            if (command.Parts[5] != "1")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Terrain.GoHere requires a target position."));
                return;
            }

            Sim actor = FindSim(actorObjectId);
            if (actor == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + actorObjectId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            RepairOrphanedSocializingPosture(actor);

            float x = float.Parse(command.Parts[6], CultureInfo.InvariantCulture);
            float y = float.Parse(command.Parts[7], CultureInfo.InvariantCulture);
            float z = float.Parse(command.Parts[8], CultureInfo.InvariantCulture);
            Vector3 destination = SnapDestinationToTerrain(new Vector3(x, y, z));
            InteractionPriority priority = new InteractionPriority(InteractionPriorityLevel.UserDirected);
            InteractionDefinition definition = Terrain.GoHere.GetSingleton(actor, destination);
            Terrain.GoHere goHere = definition.CreateInstanceWithCallbacks(
                Terrain.Singleton,
                actor,
                priority,
                false,
                true,
                new Callback(OnInteractionStarted),
                new Callback(OnInteractionCompleted),
                new Callback(OnInteractionFailed)) as Terrain.GoHere;

            if (goHere == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Terrain.GoHere did not create an interaction instance."));
                return;
            }

            goHere.SetDestination(destination, false);
            if (!actor.InteractionQueue.AddNext(goHere))
            {
                SendLines(new string[] {
                    BridgeCodec.EventInteractionFailed(actorObjectId, goHere.Id, "InteractionQueue.AddNext returned false."),
                    BridgeCodec.Error(command.CorrelationId, "Terrain.GoHere could not be queued.")
                });
                return;
            }

            sTrackedInteractionIds[actorObjectId] = goHere.Id;
            SendLines(new string[] {
                BridgeCodec.EventInteractionQueued(actorObjectId, goHere.Id, interactionKey, command.Parts[4], command.Parts[5], command.Parts[6], command.Parts[7], command.Parts[8]),
                BridgeCodec.Ok(command.CorrelationId)
            });
        }

        // input forwarding (docs/engine/input-forwarding.md), server role. A player's pick, sent by
        // the client whose authority gate blocked it: QUEUE_INTERACTION|corr|actor|hexDefinitionType|target|
        // hasPos|x|y|z|hexName|hexPath|hit|picked. Rebuilds the pick the way the pie menu does: the
        // target's interaction list for the actor, the entry with the same definition type, name and
        // path (or the only entry of that type), then CreateInstanceFromParameters with UserDirected
        // priority and flags, the click's hit and the picker's selections.
        private static void HandleQueueUserInteraction(BridgeCommand command)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            ulong actorObjectId = ulong.Parse(command.Parts[2], c);
            string definitionType = BridgeCodec.DecodeText(command.Parts[3]);
            ulong targetObjectId = command.Parts[4].Length == 0 ? 0uL : ulong.Parse(command.Parts[4], c);
            string name = BridgeCodec.DecodeText(command.Parts[9]);
            string path = BridgeCodec.DecodeText(command.Parts[10]);

            Sim actor = FindSim(actorObjectId);
            if (actor == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + actorObjectId.ToString(c)));
                return;
            }

            GameObject target = targetObjectId == 0uL ? Terrain.Singleton : FindTargetObject(targetObjectId);
            if (target == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Target not found: " + targetObjectId.ToString(c)));
                return;
            }

            InteractionObjectPair match = null;
            InteractionObjectPair onlyOfType = null;
            int ofType = 0;
            List<InteractionObjectPair> candidates = PieMenuInteractions(target, actor);
            for (int i = 0; candidates != null && i < candidates.Count; i++)
            {
                InteractionObjectPair iop = candidates[i];
                if (iop == null || iop.InteractionDefinition == null || iop.InteractionDefinition.GetType().FullName != definitionType)
                {
                    continue;
                }

                ofType++;
                onlyOfType = iop;
                if (UserPickName(iop.InteractionDefinition, actor, iop.Target, iop) == name
                    && UserPickPath(iop.InteractionDefinition, actor) == path)
                {
                    match = iop;
                    break;
                }
            }

            if (match == null && ofType == 1)
            {
                match = onlyOfType;
            }

            if (match == null)
            {
                NoteRefusal(actor, target, null, definitionType, name);
                SendLine(BridgeCodec.Error(command.CorrelationId, "No matching interaction: " + definitionType + " '" + name + "' (" + ofType.ToString(c) + " of that type on the target)."));
                return;
            }

            GameObjectHit hit = GameObjectHit.NoHit;
            int hitType;
            ulong hitId;
            uint hitLayer;
            if (command.Parts[5] == "1" && SimsOnlineServerHost.UserIntentWire.TryParseHit(command.Parts[11], out hitType, out hitId, out hitLayer))
            {
                hit = new GameObjectHit((GameObjectHitType)hitType);
                hit.mId = hitId;
                hit.mLayerMask = hitLayer;
                hit.mPoint = new Vector3(float.Parse(command.Parts[6], c), float.Parse(command.Parts[7], c), float.Parse(command.Parts[8], c));
            }

            List<object> picked = null;
            char[] kinds;
            ulong[] ids;
            if (SimsOnlineServerHost.UserIntentWire.TryParsePicked(command.Parts[12], out kinds, out ids) && kinds.Length > 0)
            {
                picked = new List<object>();
                for (int i = 0; i < kinds.Length; i++)
                {
                    object item = kinds[i] == 'd' ? (object)SimDescription.Find(ids[i]) : (object)FindTargetObject(ids[i]);
                    if (item == null)
                    {
                        SendLine(BridgeCodec.Error(command.CorrelationId, "Picked object not found: " + kinds[i] + ids[i].ToString(c)));
                        return;
                    }

                    picked.Add(item);
                }
            }

            // the server's queue can still drop a pick it accepted (the chair another player's Sim
            // took first, a route that fails). The pick's pair carries the engine's own start and failure
            // callbacks (IopWithCallbacks, as autonomy's callers use), so that failure reaches the player
            // who made it: EVENT|PickFailed, which the controller sends to that player as a CommandRejected.
            // The custom-tuning form keeps the menu entry's tuning (a social's, for one).
            PickWatch watch = new PickWatch(command.CorrelationId, actorObjectId);
            Sims3.Gameplay.Autonomy.Callback onStart = new Sims3.Gameplay.Autonomy.Callback(watch.OnStart);
            Sims3.Gameplay.Autonomy.Callback onFail = new Sims3.Gameplay.Autonomy.Callback(watch.OnFail);
            InteractionObjectPair watched = match.Tuning != null
                ? (InteractionObjectPair)new IopWithCallbacksAndCustomTuning(match.InteractionDefinition, match.Target, match.Tuning,
                    match.Target == null ? null : match.Target.GetType(), onStart, null, onFail)
                : (InteractionObjectPair)new IopWithCallbacks(match.InteractionDefinition, match.Target, onStart, null, onFail);
            InteractionInstanceParameters parameters = new InteractionInstanceParameters(
                watched, actor, new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                InteractionFlags.UserDirected, hit, picked, false);
            InteractionInstance instance = match.InteractionDefinition.CreateInstanceFromParameters(ref parameters);
            if (instance == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "CreateInstanceFromParameters returned null for " + definitionType + "."));
                return;
            }

            if (instance is IImmediateInteraction)
            {
                Simulator.AddObject(new ImmediateInteractionTask(actor, instance));
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=immediate;name=" + PresentationField(name)));
                return;
            }

            if (!actor.InteractionQueue.Add(instance))
            {
                NoteRefusal(actor, target, match, definitionType, name);
                SendLine(BridgeCodec.Error(command.CorrelationId, SimsOnlineServerHost.UserIntentWire.WithTooltip(
                    "InteractionQueue.Add refused " + definitionType + " '" + name + "'.", PickRefusalTooltip(instance))));
                return;
            }

            // Armed only now: Add's own test calls the failure callback before it refuses, and that
            // refusal is the Error reply above.
            watch.Instance = instance;
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                "result=queued;id=" + instance.Id.ToString(c) + ";name=" + PresentationField(name)));
        }

        // part A step 3, menu refusals (MenuRefusal): a player's pick that the server refused on the definition's
        // own test, or for which it lists no such entry, goes to the clients as an Rf line, and the client's menu shows
        // that entry greyed. The server tests the pick again at each prop poll (an entry it did not list, at each resend)
        // and sends the clear once it would pass, so the entry is greyed for as long as the server would refuse it. A
        // refusal for another reason (the queue's head locked, a must-run group ahead: InteractionQueue.AddInteraction) is
        // not sent: the definition's test passes then. A refusal also holds the definition's other entries on that target
        // whose test fails on the server (up to 12). At most 32 are held, and one older than four sim hours is cleared.
        // Each refusal line carries in its field 7 the greyed-out tooltip the server game's own test gave for that entry,
        // empty when it gave none (card 57 row 57-a); showing that text on the greyed entry is the fork's. A client that
        // does not read field 7 greys the entry as before. stream.refusals 0 turns it off.
        private sealed class RefusalWatch
        {
            internal Sim Actor;
            internal GameObject Target;
            internal ulong ActorId;
            internal ulong TargetId;
            // The server's own entry; null when it listed none.
            internal InteractionObjectPair Pair;
            internal string DefinitionType;
            internal string Name;
            internal long SinceTicks;
        }

        private const int RefusalWatchCapacity = 32;
        private const int RefusalSiblingLimit = 12;
        private const float RefusalMaxMinutes = 240f;
        private static readonly List<RefusalWatch> sRefusals = new List<RefusalWatch>();
        private static long sRefusalsSent;
        private static long sRefusalsCleared;
        private static long sRefusalsSkipped;
        private static long sRefusalRetests;
        private static long sRefusalErrors;

        // The definition's own test for a user-directed pick, as a player's pie menu runs it.
        private static bool PickTestPasses(InteractionObjectPair pair, Sim actor)
        {
            string unused;
            return PickTestPasses(pair, actor, out unused);
        }

        // As above, and the greyed-out tooltip its test gave for the refusal (null when it passed, or gave none), so the
        // refusal can carry the reason the server's own menu would show (card 57 row 57-a).
        private static bool PickTestPasses(InteractionObjectPair pair, Sim actor, out string tooltip)
        {
            InteractionInstanceParameters parameters = new InteractionInstanceParameters(pair, actor,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                InteractionFlags.UserDirected, GameObjectHit.NoHit, null, false);
            GreyedOutTooltipCallback why = null;
            bool passes = IUtil.IsPass(pair.InteractionDefinition.Test(ref parameters, ref why));
            tooltip = passes ? null : TooltipText(why);
            return passes;
        }

        // What a greyed-out tooltip callback answers, or null when there is none or it throws: a refusal never fails over
        // its reason.
        private static string TooltipText(GreyedOutTooltipCallback why)
        {
            if (why == null)
            {
                return null;
            }

            try
            {
                return why();
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
                return null;
            }
        }

        // The server game's own greyed-out tooltip for a pick its queue refused, so the player reads why
        // (docs/refusal-reason-analysis.md). The queue's insert test (InteractionInstance.Test) runs the definition's test
        // with the instance's parameters and drops the tooltip; this runs it again the same way and keeps it. Null when
        // the test passes (the queue refused for another reason) or gives no tooltip (the menu would hide the entry).
        private static string PickRefusalTooltip(InteractionInstance instance)
        {
            try
            {
                InteractionInstanceParameters parameters = instance.GetInteractionParameters();
                GreyedOutTooltipCallback why = null;
                if (IUtil.IsPass(instance.InteractionDefinition.Test(ref parameters, ref why)) || why == null)
                {
                    return null;
                }

                return why();
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
                return null;
            }
        }

        private static void NoteRefusal(Sim actor, GameObject target, InteractionObjectPair pair, string definitionType, string name)
        {
            if (actor == null || target == null || target is Terrain || Tunables.Get("stream.refusals", 1f) <= 0f)
            {
                return;
            }

            try
            {
                string tooltip = null;
                if (pair != null && PickTestPasses(pair, actor, out tooltip))
                {
                    sRefusalsSkipped++;
                    return;
                }

                HoldRefusal(actor, target, pair, definitionType, name ?? string.Empty, tooltip);
                if (pair == null)
                {
                    return;
                }

                // The definition's other entries on that target that the server refuses too (a newspaper's chooser lists
                // Read, Find a Job, Check Weather and five more, and an old paper fails them all): one refusal greys them
                // all, rather than one per pick.
                List<InteractionObjectPair> candidates = PieMenuInteractions(target, actor);
                int siblings = 0;
                for (int i = 0; candidates != null && i < candidates.Count && siblings < RefusalSiblingLimit; i++)
                {
                    InteractionObjectPair iop = candidates[i];
                    if (iop == null || iop.InteractionDefinition == null || iop.InteractionDefinition.GetType().FullName != definitionType)
                    {
                        continue;
                    }

                    string other = UserPickName(iop.InteractionDefinition, actor, iop.Target, iop);
                    if (other == name)
                    {
                        continue;
                    }

                    string siblingTooltip;
                    if (PickTestPasses(iop, actor, out siblingTooltip))
                    {
                        continue;
                    }

                    HoldRefusal(actor, target, iop, definitionType, other, siblingTooltip);
                    siblings++;
                }
            }
            catch (Exception)
            {
                sRefusalErrors++;
            }
        }

        // One held refusal, replacing the same entry's, the oldest cleared when full; sent to the clients with the
        // tooltip its test gave (null when it gave none).
        private static void HoldRefusal(Sim actor, GameObject target, InteractionObjectPair pair, string definitionType,
            string name, string tooltip)
        {
            for (int i = sRefusals.Count - 1; i >= 0; i--)
            {
                RefusalWatch held = sRefusals[i];
                if (held.Actor == actor && held.Target == target && held.DefinitionType == definitionType && held.Name == name)
                {
                    sRefusals.RemoveAt(i);
                }
            }

            if (sRefusals.Count >= RefusalWatchCapacity)
            {
                ClearRefusal(sRefusals[0]);
                sRefusals.RemoveAt(0);
            }

            RefusalWatch watch = new RefusalWatch();
            watch.Actor = actor;
            watch.Target = target;
            watch.ActorId = actor.ObjectId.Value;
            watch.TargetId = target.ObjectId.Value;
            watch.Pair = pair;
            watch.DefinitionType = definitionType;
            watch.Name = name;
            watch.SinceTicks = SimClock.CurrentTicks;
            sRefusals.Add(watch);
            SacsRecorder.RingLine(MenuRefusal.Line(watch.ActorId, watch.TargetId, definitionType, watch.Name, true, tooltip));
            sRefusalsSent++;
        }

        // Each prop poll: a held refusal whose test now passes (or whose actor or target is gone) is cleared.
        private static void RetestRefusals(bool resend)
        {
            if (sRefusals.Count == 0)
            {
                return;
            }

            float now = SimClock.ConvertFromTicks(SimClock.CurrentTicks, TimeUnit.Minutes);
            for (int i = sRefusals.Count - 1; i >= 0; i--)
            {
                RefusalWatch watch = sRefusals[i];
                bool clear;
                try
                {
                    if (watch.Actor.HasBeenDestroyed || watch.Target.HasBeenDestroyed
                        || now - SimClock.ConvertFromTicks(watch.SinceTicks, TimeUnit.Minutes) > RefusalMaxMinutes)
                    {
                        clear = true;
                    }
                    else if (watch.Pair != null)
                    {
                        sRefusalRetests++;
                        clear = PickTestPasses(watch.Pair, watch.Actor);
                    }
                    else if (resend)
                    {
                        sRefusalRetests++;
                        clear = ListsPick(watch);
                    }
                    else
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    sRefusalErrors++;
                    clear = true;
                }

                if (clear)
                {
                    ClearRefusal(watch);
                    sRefusals.RemoveAt(i);
                }
            }
        }

        // An entry the server did not list: does it list it now, with its test passing?
        private static bool ListsPick(RefusalWatch watch)
        {
            List<InteractionObjectPair> candidates = PieMenuInteractions(watch.Target, watch.Actor);
            for (int i = 0; candidates != null && i < candidates.Count; i++)
            {
                InteractionObjectPair iop = candidates[i];
                if (iop != null && iop.InteractionDefinition != null && iop.InteractionDefinition.GetType().FullName == watch.DefinitionType
                    && UserPickName(iop.InteractionDefinition, watch.Actor, iop.Target, iop) == watch.Name)
                {
                    return PickTestPasses(iop, watch.Actor);
                }
            }

            return false;
        }

        private static void ClearRefusal(RefusalWatch watch)
        {
            // Nothing is refused, so field 7 is empty: the clear carries no tooltip.
            SacsRecorder.RingLine(MenuRefusal.Line(watch.ActorId, watch.TargetId, watch.DefinitionType, watch.Name, false, null));
            sRefusalsCleared++;
        }

        private static string RefusalReport()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return sRefusals.Count.ToString(c) + "/sent:" + sRefusalsSent.ToString(c) + "/cleared:" + sRefusalsCleared.ToString(c)
                + "/skipped:" + sRefusalsSkipped.ToString(c) + "/retests:" + sRefusalRetests.ToString(c) + "/errors:" + sRefusalErrors.ToString(c);
        }

        // one player pick on the server's queue (HandleQueueUserInteraction), so a failure after
        // the queue accepted it reaches the player who made it.
        private sealed class PickWatch
        {
            private readonly string mCorrelationId;
            private readonly ulong mActorObjectId;
            private bool mStarted;

            // Set once the queue accepted the pick.
            public InteractionInstance Instance;

            public PickWatch(string correlationId, ulong actorObjectId)
            {
                mCorrelationId = correlationId;
                mActorObjectId = actorObjectId;
            }

            public void OnStart(Sim actor, float unused)
            {
                mStarted = true;
            }

            public void OnFail(Sim actor, float unused)
            {
                try
                {
                    InteractionInstance instance = Instance;
                    // A cancel is the player's (their HUD, a new order) or the server's own; neither is news.
                    if (instance == null || instance.Cancelled)
                    {
                        return;
                    }

                    string reason = PickFailureReason(actor, instance, mStarted);
                    if (reason != null)
                    {
                        // Card 57 row 57-b: a code that tells the player nothing (NotRun, FailedToStart) also carries the
                        // server game's greyed-out tooltip when its test now fails with one, in the suffix the Add
                        // refusal uses. The other two already name the cause, so they run the test no further.
                        if (SimsOnlineServerHost.UserIntentWire.PickCodeTakesTooltip(reason))
                        {
                            reason = SimsOnlineServerHost.UserIntentWire.WithTooltip(reason, PickRefusalTooltip(instance));
                        }

                        SendLine(BridgeCodec.EventPickFailed(mCorrelationId, mActorObjectId, instance.Id, reason));
                    }
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
            }
        }

        // Why a pick the queue accepted failed: the object was in use, the route failed, it failed to
        // start, or it never ran (a posture transition or a run-time test refused it). Null when it ran
        // and ended without one of those: that is the interaction's own ending, not a refusal.
        private static string PickFailureReason(Sim actor, InteractionInstance instance, bool started)
        {
            if (instance.WasInUse || (actor != null && actor.HasExitReason(ExitReason.ObjectInUse)))
            {
                return "ObjectInUse";
            }

            if (actor != null && actor.HasExitReason(ExitReason.RouteFailed))
            {
                return "RouteFailed";
            }

            if (actor != null && actor.HasExitReason(ExitReason.FailedToStart))
            {
                return "FailedToStart";
            }

            return started ? null : "NotRun";
        }

        // The list a player's menu on this target is built from, as the game's own pick handlers choose it
        // (GameObject.OnPick, OnPickFromInventory): a charred object's charred list, an object in an inventory its
        // inventory menu's list, else the world pie menu's. A pick from a Sim's inventory (Browse the Web on the phone)
        // is in the inventory list only, so the server found "0 of that type on the target" for it.
        private static List<InteractionObjectPair> PieMenuInteractions(GameObject target, Sim actor)
        {
            if (target.Charred)
            {
                return target.GetCharredInteractions(actor);
            }

            return target.InInventory ? target.GetAllInventoryInteractionsForActor(actor) : target.GetAllInteractionsForActor(actor);
        }

        private static string UserPickName(InteractionDefinition definition, Sim actor, IGameObject target, InteractionObjectPair iop)
        {
            try
            {
                return definition.GetInteractionName(actor, target, iop) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string UserPickPath(InteractionDefinition definition, Sim actor)
        {
            try
            {
                string[] path = definition.GetPath(actor.IsFemale);
                return path == null ? string.Empty : string.Join("/", path);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // Client role: the authority gate parks the player's picks it blocked (AuthorityHook.UserIntents,
        // fork); send each one to the server once.
        private static int sUserIntentSeen;
        private static FieldInfo sUserIntentSequenceField;
        private static FieldInfo sUserIntentsField;

        private static void DrainUserIntents()
        {
            if (sCurrentRole != "client")
            {
                return;
            }

            try
            {
                Type hook = AuthorityHook();
                if (hook == null)
                {
                    return;
                }

                if (sUserIntentSequenceField == null)
                {
                    sUserIntentSequenceField = hook.GetField("UserIntentSequence");
                    sUserIntentsField = hook.GetField("UserIntents");
                    if (sUserIntentSequenceField == null || sUserIntentsField == null)
                    {
                        return; // a fork without input forwarding
                    }
                }

                int sequence = (int)sUserIntentSequenceField.GetValue(null);
                if (sequence == sUserIntentSeen)
                {
                    return;
                }

                object[] ring = (object[])sUserIntentsField.GetValue(null);
                int first = Math.Max(sUserIntentSeen + 1, sequence - ring.Length + 1);
                sUserIntentSeen = sequence;
                for (int i = first; i <= sequence; i++)
                {
                    InteractionInstance instance = ring[i % ring.Length] as InteractionInstance;
                    if (instance != null)
                    {
                        SendUserIntent(instance);
                    }
                }
            }
            catch (Exception ex)
            {
                SendLine(BridgeCodec.Error("00000000000000000000000000000000", "User intent drain error: " + ex.Message));
            }
        }

        // Object ids on the wire are the server's: a Sim is named by its alias (WireSimObjectId).
        private static ulong WireObjectId(IGameObject obj)
        {
            if (obj == null)
            {
                return 0uL;
            }

            Sim sim = obj as Sim;
            if (sim != null)
            {
                return WireSimObjectId(sim);
            }

            // A copy this client made of a server object (ResolveStreamObject: a runtime object such as a delivered
            // newspaper) has an id of its own; the server knows it by its own id only, and a pick on the copy went out
            // under the copy's id and came back "Target not found" ( part A step 3, 2026-10-21). One pick per
            // click, so a scan of the aliases is enough.
            ObjectGuid local = obj.ObjectId;
            foreach (KeyValuePair<ulong, ObjectGuid> alias in sObjectAliases)
            {
                if (alias.Value == local)
                {
                    return alias.Key;
                }
            }

            return local.Value;
        }

        private static void SendUserIntent(InteractionInstance instance)
        {
            Sim actor = instance.InstanceActor as Sim;
            if (actor == null || instance.InteractionDefinition == null)
            {
                return;
            }

            IGameObject target = instance.Target;
            ulong targetId = target is Terrain ? 0uL : WireObjectId(target);
            InteractionDefinition definition = instance.InteractionDefinition;
            InteractionObjectPair pair = instance.InteractionObjectPair;
            string name = UserPickName(definition, actor, pair != null ? pair.Target : target, pair);
            string path = UserPickPath(definition, actor);

            string hitText = SimsOnlineServerHost.UserIntentWire.FormatHit(0, 0uL, 0u);
            Vector3 point = Vector3.Zero;
            bool hasHit = false;
            FieldInfo hitField = instance.GetType().GetField("Hit");
            if (hitField != null && hitField.GetValue(instance) is GameObjectHit)
            {
                GameObjectHit hit = (GameObjectHit)hitField.GetValue(instance);
                if (hit.mType != GameObjectHitType.None)
                {
                    ulong hitId = hit.mId;
                    GameObject hitObject = new ObjectGuid(hit.mId).ObjectFromId<GameObject>();
                    if (hitObject != null)
                    {
                        hitId = WireObjectId(hitObject);
                    }

                    hitText = SimsOnlineServerHost.UserIntentWire.FormatHit((int)hit.mType, hitId, hit.mLayerMask);
                    point = hit.mPoint;
                    hasHit = true;
                }
            }

            List<char> kinds = new List<char>();
            List<ulong> ids = new List<ulong>();
            List<object> selected = instance.SelectedObjects;
            for (int i = 0; selected != null && i < selected.Count; i++)
            {
                object item = selected[i];
                if (item is Sim)
                {
                    kinds.Add('s');
                    ids.Add(WireSimObjectId((Sim)item));
                }
                else if (item is GameObject)
                {
                    kinds.Add('o');
                    ids.Add(WireObjectId((GameObject)item));
                }
                else if (item is SimDescription)
                {
                    kinds.Add('d');
                    ids.Add(((SimDescription)item).SimDescriptionId);
                }
            }

            SendLine(BridgeCodec.EventUserIntent(
                WireSimObjectId(actor), targetId, definition.GetType().FullName, name, path, hitText,
                hasHit ? Float(point.x) : "-", hasHit ? Float(point.y) : "-", hasHit ? Float(point.z) : "-",
                SimsOnlineServerHost.UserIntentWire.FormatPicked(kinds, ids)));
            ShowToast("Sent to server: " + (name.Length > 0 ? name : definition.GetType().Name));
        }

        private static void HandleSetActiveSim(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("SET_ACTIVE_SIM requires a Sim object id.");
            }

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SET_ACTIVE_SIM requires an in-world session."));
                return;
            }

            ulong simObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            Sim sim = FindSim(simObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + simObjectId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            // the client controller re-asserts its claimed Sim; selecting it again would move the camera.
            if (Sim.ActiveActor == sim)
            {
                SendLine(BridgeCodec.Ok(command.CorrelationId));
                return;
            }

            if (!SelectSim(sim, SelectCause.Player))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "PlumbBob.ForceSelectActor returned false."));
                return;
            }

            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // the one place the mod selects a Sim; every select names its cause. A replay keeps the player's
        // selection (SelectionGuard) and answers true, so its caller still queues on the actor.
        private static bool SelectSim(Sim sim, SelectCause cause)
        {
            if (SelectionGuard.KeepsSelection(cause))
            {
                return true;
            }

            return PlumbBob.ForceSelectActor(sim);
        }

        private static void HandleCancelInteraction(BridgeCommand command)
        {
            if (command.Parts.Length < 4)
            {
                throw new InvalidOperationException("CANCEL_INTERACTION requires actor and interaction ids.");
            }

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "CANCEL_INTERACTION requires an in-world session."));
                return;
            }

            ulong actorObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            ulong interactionId = ulong.Parse(command.Parts[3], CultureInfo.InvariantCulture);
            Sim sim = FindSim(actorObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + actorObjectId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            if (sim.InteractionQueue == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim has no interaction queue."));
                return;
            }

            InteractionInstance target = FindInteractionInstanceById(sim, interactionId);
            if (target == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Interaction not found in queue or running: " + interactionId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            sim.InteractionQueue.CancelInteraction(target, false);
            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // n14, server role: a player left their Sim's conversation from the client's HUD. The engine's own
        // HUD path runs here (InteractionQueue.CancelSocializing, as HudModel.CancelSocializing calls it); the
        // conversation stream (Cv) then ends it on every client. Not in one (a double click, a race) is no error.
        private static void HandleLeaveConversation(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("LEAVE_CONVERSATION requires an actor id.");
            }

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "LEAVE_CONVERSATION requires an in-world session."));
                return;
            }

            ulong actorObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            Sim sim = FindSim(actorObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + actorObjectId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            if (sim.InteractionQueue == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim has no interaction queue."));
                return;
            }

            // OK carries no detail: already out of the conversation is OK too, so the player sees no refusal.
            if (sim.SocialComponent != null && sim.SocialComponent.Conversation != null)
            {
                sim.InteractionQueue.CancelSocializing(sim);
            }

            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        private static InteractionInstance FindInteractionInstanceById(Sim sim, ulong interactionId)
        {
            if (sim == null || sim.InteractionQueue == null)
            {
                return null;
            }

            InteractionQueue queue = sim.InteractionQueue;
            foreach (InteractionInstance inst in queue.InteractionList)
            {
                if (inst != null && (ulong)inst.Id == interactionId)
                {
                    return inst;
                }
            }

            InteractionInstance pick = sim.CurrentInteraction;
            if (pick != null && (ulong)pick.Id == interactionId)
            {
                return pick;
            }

            try
            {
                pick = queue.GetHeadInteraction();
                if (pick != null && (ulong)pick.Id == interactionId)
                {
                    return pick;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            try
            {
                pick = queue.RunningInteraction;
                if (pick != null && (ulong)pick.Id == interactionId)
                {
                    return pick;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            try
            {
                pick = queue.TransitionInteraction;
                if (pick != null && (ulong)pick.Id == interactionId)
                {
                    return pick;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return null;
        }

        private static void HandleDisableAutonomy(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("DISABLE_AUTONOMY requires a Sim object id.");
            }

            ulong simObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            Sim sim = FindSim(simObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + simObjectId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            sim.Autonomy.IncrementAutonomyDisabled();

            // optional 4th part — cancel the Sim's current interaction queue so the first
            // player command after a claim takes effect promptly instead of waiting behind a
            // potentially hour-long autonomous interaction. Older controllers send 3 parts.
            bool cancelRunning = command.Parts.Length >= 4 && command.Parts[3] == "1";
            if (cancelRunning && sim.InteractionQueue != null)
            {
                sim.InteractionQueue.CancelAllInteractions();
            }

            sAutonomyWatchLastInteractionIds[simObjectId] = CurrentInteractionId(sim);
            SeedClaimedTransitionSitBaseline(sim, simObjectId);
            bool enabled = AutonomyRestrictions.IsAnyAutonomyEnabled(sim);
            string report = "IncrementAutonomyDisabled; IsAnyAutonomyEnabled=" + enabled.ToString() + "; CancelAll=" + (cancelRunning ? "true" : "false") + "; QueueWatch=enabled";
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", report));
        }

        private static void HandleEnableAutonomy(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("ENABLE_AUTONOMY requires a Sim object id.");
            }

            ulong simObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            Sim sim = FindSim(simObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + simObjectId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            // optional 4th part "reset" — decrement until autonomy is actually enabled
            // (bounded). A re-attached controller has no record of how many DISABLE_AUTONOMY
            // calls the dead session issued, so paired decrements can't unwind the counter.
            bool reset = command.Parts.Length >= 4 && command.Parts[3] == "reset";
            int decrements = 0;
            if (reset)
            {
                while (decrements < 16 && !AutonomyRestrictions.IsAnyAutonomyEnabled(sim))
                {
                    sim.Autonomy.DecrementAutonomyDisabled();
                    decrements++;
                }
            }
            else
            {
                sim.Autonomy.DecrementAutonomyDisabled();
                decrements = 1;
            }

            sAutonomyWatchLastInteractionIds.Remove(simObjectId);
            sClaimedTransitionSitLastEmittedIds.Remove(simObjectId);
            bool enabled = AutonomyRestrictions.IsAnyAutonomyEnabled(sim);
            string report = "DecrementAutonomyDisabled x" + decrements.ToString(CultureInfo.InvariantCulture) + (reset ? " (reset)" : "") + "; IsAnyAutonomyEnabled=" + enabled.ToString() + "; QueueWatch=disabled";
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", report));
        }

        // GATE_AUTONOMY|corr|simId — register a claimed Sim for the command-lifecycle
        // autonomy gate. Does NOT disable autonomy; the Sim keeps free will until a UserDirected
        // interaction enters its queue (then PollCommandLifecycleGates engages the gate). Idempotent
        // (a second register for an already-tracked Sim is a no-op). If a player command is ALREADY
        // in the Sim's queue at register time (claim landed mid-command), engage immediately so
        // there is no window where the command runs with autonomy still enabled.
        private static void HandleGateAutonomy(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("GATE_AUTONOMY requires a Sim object id.");
            }

            ulong simObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            Sim sim = FindSim(simObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + simObjectId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            bool determinate;
            bool userDirectedPresent = HasUserDirectedInteraction(sim, out determinate);
            // On an indeterminate read, assume no command present at register time; the next poll
            // tick re-reads and engages if a command is in fact queued.
            GateAction action = sCommandGate.Register(simObjectId, determinate && userDirectedPresent);
            ApplyGateAction(sim, action);

            // claim can land while the Sim is stuck in an orphaned SocializingPosture
            // (empty queue, nothing running) — every later interaction then fails in ~15ms. Repair
            // immediately so the claimed Sim is not frozen on the authoritative host until the
            // first player command happens to call RepairOrphanedSocializingPosture elsewhere.
            RepairOrphanedSocializingPosture(sim);

            bool gated = sCommandGate.IsGated(simObjectId);
            string report = "GateRegistered; engaged=" + (gated ? "true" : "false") + "; tracked=" + sCommandGate.TrackedCount.ToString(CultureInfo.InvariantCulture);
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", report));
        }

        // UNGATE_AUTONOMY|corr|simId[|reset] — clear the command-lifecycle gate for a Sim
        // (release/leave/disconnect). If the gate is engaged, unwind the one outstanding
        // suppression so autonomy is never left disabled past the claim. The optional "reset" part
        // is the controller-restart recovery path: force autonomy fully back on with a bounded
        // decrement-until-enabled loop (mirrors HandleEnableAutonomy's reset), for a gate the
        // controller believes is orphaned. A missing Sim is not an error here — the controller is
        // clearing session state, and a vanished Sim has no counter to unwind.
        private static void HandleUngateAutonomy(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("UNGATE_AUTONOMY requires a Sim object id.");
            }

            ulong simObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            bool reset = command.Parts.Length >= 4 && command.Parts[3] == "reset";

            GateAction action = sCommandGate.Clear(simObjectId);
            Sim sim = FindSim(simObjectId);

            int decrements = 0;
            if (sim != null && sim.Autonomy != null)
            {
                if (reset)
                {
                    while (decrements < 16 && !AutonomyRestrictions.IsAnyAutonomyEnabled(sim))
                    {
                        sim.Autonomy.DecrementAutonomyDisabled();
                        decrements++;
                    }
                }
                else if (action == GateAction.Enable)
                {
                    // The gate was engaged: unwind exactly the one suppression it owns, guarded so a
                    // foreign decrement can't drive the shared ref-count negative.
                    if (!AutonomyRestrictions.IsAnyAutonomyEnabled(sim))
                    {
                        sim.Autonomy.DecrementAutonomyDisabled();
                        decrements = 1;
                    }
                }
            }

            bool enabled = sim != null ? AutonomyRestrictions.IsAnyAutonomyEnabled(sim) : true;
            string report = "GateCleared" + (reset ? " (reset)" : "") + "; decrements=" + decrements.ToString(CultureInfo.InvariantCulture) + "; IsAnyAutonomyEnabled=" + enabled.ToString() + "; tracked=" + sCommandGate.TrackedCount.ToString(CultureInfo.InvariantCulture);
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", report));
        }

        // W1 (docs/server-authoritative-plan.md): SET_SIM_SCOPE|corr|lots|sims -- the lots players
        // occupy and the Sims they own (SimScopeWire id lists). The forked Sims3GameplaySystems
        // (gameplay_02 slice 4) has SimsOnline.Hooks.SimScopeHook, which makes those lots simulate
        // like the active lot and runs those Sims at high-LOD autonomy. Stock gameplay has no hook:
        // reply ok with hook=absent so the controller can log it and stop resending. The world lot
        // and unknown lot ids are dropped here (only the engine can tell them apart): on the world
        // lot every Sim on the street would otherwise be granted high-LOD.
        private static Type sSimScopeHookType;
        private static bool sSimScopeHookResolved;

        /// (docs/engine/scope-hooks.md): the fork's plumb-bob guard counter for SACS_RECORDER status,
        // the non-forced selects it refused (SimScopeHook.KeepsPlumbBob); absent without the fork's guard.
        private static string PlumbBobKeptStatus()
        {
            if (!sSimScopeHookResolved)
            {
                sSimScopeHookType = FindGameplayType("SimsOnline.Hooks.SimScopeHook");
                sSimScopeHookResolved = true;
            }

            FieldInfo field = sSimScopeHookType == null ? null : sSimScopeHookType.GetField("PlumbBobKept");
            return "plumbBobKept=" + (field == null ? "absent" : ((long)field.GetValue(null)).ToString(CultureInfo.InvariantCulture));
        }

        private static void HandleSetSimScope(BridgeCommand command)
        {
            if (command.Parts.Length < 4)
            {
                throw new InvalidOperationException("SET_SIM_SCOPE requires lots and sims fields.");
            }

            ulong[] requestedLots = SimScopeWire.Parse(command.Parts[2]);
            ulong[] sims = SimScopeWire.Parse(command.Parts[3]);
            if (sims != null)
            {
                sScopeSims = sims; // claimed Sims' dialogs go to their players
            }

            if (requestedLots == null || sims == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SET_SIM_SCOPE ids must be decimal, comma-separated, or '-'."));
                return;
            }

            List<ulong> lots = new List<ulong>();
            int dropped = 0;
            for (int i = 0; i < requestedLots.Length; i++)
            {
                Lot lot = LotManager.GetLot(requestedLots[i]);
                if (lot == null || lot.IsWorldLot)
                {
                    dropped++;
                    continue;
                }

                lots.Add(requestedLots[i]);
            }

            if (!sSimScopeHookResolved)
            {
                sSimScopeHookType = FindGameplayType("SimsOnline.Hooks.SimScopeHook");
                sSimScopeHookResolved = true;
            }

            string report = "lots=" + SimScopeWire.Format(lots) + "; sims=" + SimScopeWire.Format(sims) + "; droppedLots=" + dropped.ToString(CultureInfo.InvariantCulture);
            if (sSimScopeHookType == null)
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "hook=absent; " + report));
                return;
            }

            sSimScopeHookType.GetMethod("SetPlayerLots").Invoke(null, new object[] { lots.ToArray() });
            sSimScopeHookType.GetMethod("SetPlayerSims").Invoke(null, new object[] { sims });
            string lifecycle = ApplyScopeLifecycle(sims);
            string state = (string)sSimScopeHookType.GetMethod("Describe").Invoke(null, null);
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "hook=present; " + report + "; " + lifecycle + "; " + state));
        }

        // (H3, docs/engine/scope-hooks.md): the households that hold a claimed Sim, as of the last scope.
        private static List<Household> sScopeHouseholds = new List<Household>();

        // (H3): with the fork's player-scope slice, a household with a claimed Sim is selectable on the server
        // (SimScopeHook.IsPlayerHousehold), so it needs the selectable managers stock makes only for the plumb-bob
        // household (wishes, opportunities, map tags, career hooks: Sim.OnBecameSelectable). Called after the new set
        // is published, as stock flips the plumb bob before its lifecycle: a household that joined gets
        // OnBecameSelectable on every actor, one that left gets OnBecameUnselectable (the fork's guard in it lets the
        // call through once the household has no claimed Sim). The server's own active household is never touched:
        // stock runs its lifecycle. NPC roommates are skipped as PlumbBob.CheckForChangeInActiveHousehold does.
        private static string ApplyScopeLifecycle(ulong[] sims)
        {
            MethodInfo isPlayer = sSimScopeHookType.GetMethod("IsPlayerHousehold");
            if (isPlayer == null)
            {
                return "lifecycle=absent";
            }

            if (!SafeIsInWorld())
            {
                return "lifecycle=not-in-world";
            }

            List<Household> now = new List<Household>();
            for (int i = 0; i < sims.Length; i++)
            {
                Sim sim = FindSim(sims[i]);
                Household household = sim == null ? null : sim.Household;
                if (household != null && !now.Contains(household) && (bool)isPlayer.Invoke(null, new object[] { household }))
                {
                    now.Add(household);
                }
            }

            Household active = Household.ActiveHousehold;
            int joined = 0;
            int left = 0;
            int calls = 0;
            int errors = 0;
            for (int i = 0; i < now.Count; i++)
            {
                if (now[i] != active && !sScopeHouseholds.Contains(now[i]))
                {
                    joined++;
                    calls += RunLifecycle(now[i], true, ref errors);
                }
            }

            for (int i = 0; i < sScopeHouseholds.Count; i++)
            {
                if (sScopeHouseholds[i] != active && !now.Contains(sScopeHouseholds[i]))
                {
                    left++;
                    calls += RunLifecycle(sScopeHouseholds[i], false, ref errors);
                }
            }

            sScopeHouseholds = now;
            return "lifecycle=households:" + now.Count.ToString(CultureInfo.InvariantCulture)
                + ",joined:" + joined.ToString(CultureInfo.InvariantCulture)
                + ",left:" + left.ToString(CultureInfo.InvariantCulture)
                + ",calls:" + calls.ToString(CultureInfo.InvariantCulture)
                + ",errors:" + errors.ToString(CultureInfo.InvariantCulture);
        }

        private static int RunLifecycle(Household household, bool selectable, ref int errors)
        {
            List<Sim> actors;
            try
            {
                actors = new List<Sim>(household.AllActors);
            }
            catch (Exception)
            {
                errors++;
                return 0;
            }

            int calls = 0;
            for (int i = 0; i < actors.Count; i++)
            {
                Sim actor = actors[i];
                if (actor == null)
                {
                    continue;
                }

                try
                {
                    if (selectable)
                    {
                        if (!Household.RoommateManager.IsNPCRoommate(actor))
                        {
                            actor.OnBecameSelectable();
                            calls++;
                        }
                    }
                    else
                    {
                        actor.OnBecameUnselectable();
                        calls++;
                    }
                }
                catch (Exception)
                {
                    errors++;
                }
            }

            return calls;
        }

        // W2 Sim identity (internal documentation): only Sims in the world at load
        // share an ObjectId between the server and a client; a Sim instantiated later gets a different
        // one on each side. SET_SIM_ALIASES|corr|pairs[|reset] (client role only) records, for server
        // ObjectIds, the SimDescriptionId of the Sim they name (SimAliasWire; description 0 removes,
        // "reset" clears first). FindSim then resolves an aliased id by description, so every command
        // reaches the client's own instance of that Sim, and the snapshot reports that instance under
        // the server's id.
        private static readonly Dictionary<ulong, ulong> sSimAliases = new Dictionary<ulong, ulong>();
        private static readonly Dictionary<ulong, ulong> sSimAliasByDescription = new Dictionary<ulong, ulong>();

        private static void HandleSetSimAliases(BridgeCommand command)
        {
            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SET_SIM_ALIASES is client-role only."));
                return;
            }

            ulong[] objectIds;
            ulong[] descriptionIds;
            if (command.Parts.Length < 3 || !SimAliasWire.TryParse(command.Parts[2], out objectIds, out descriptionIds))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SET_SIM_ALIASES requires <objectId>:<simDescriptionId> pairs or '-'."));
                return;
            }

            if (command.Parts.Length > 3 && command.Parts[3] == "reset")
            {
                sSimAliases.Clear();
                sSimAliasByDescription.Clear();
            }

            int set = 0;
            int removed = 0;
            for (int i = 0; i < objectIds.Length; i++)
            {
                ulong previous;
                bool had = sSimAliases.TryGetValue(objectIds[i], out previous);
                if (had)
                {
                    sSimAliasByDescription.Remove(previous);
                    sSimAliases.Remove(objectIds[i]);
                }

                if (descriptionIds[i] == 0uL)
                {
                    if (had)
                    {
                        removed++;
                    }

                    continue;
                }

                AddSimAlias(objectIds[i], descriptionIds[i]);
                set++;
            }

            int resolved = 0;
            int differ = 0;
            foreach (KeyValuePair<ulong, ulong> alias in sSimAliases)
            {
                Sim local = FindSimByDescription(alias.Value);
                if (local != null)
                {
                    resolved++;
                    if (local.ObjectId.Value != alias.Key)
                    {
                        differ++;
                    }
                }
            }

            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                "set=" + set.ToString(CultureInfo.InvariantCulture) +
                "; removed=" + removed.ToString(CultureInfo.InvariantCulture) +
                "; aliases=" + sSimAliases.Count.ToString(CultureInfo.InvariantCulture) +
                "; resolved=" + resolved.ToString(CultureInfo.InvariantCulture) +
                "; differentObjectId=" + differ.ToString(CultureInfo.InvariantCulture)));
        }

        private static void AddSimAlias(ulong serverObjectId, ulong simDescriptionId)
        {
            ulong previous;
            if (sSimAliases.TryGetValue(serverObjectId, out previous))
            {
                sSimAliasByDescription.Remove(previous);
            }

            ulong staleObjectId;
            if (sSimAliasByDescription.TryGetValue(simDescriptionId, out staleObjectId))
            {
                sSimAliases.Remove(staleObjectId);
            }

            sSimAliases[serverObjectId] = simDescriptionId;
            sSimAliasByDescription[simDescriptionId] = serverObjectId;
        }

        private static void RemoveSimAlias(ulong serverObjectId)
        {
            ulong description;
            if (sSimAliases.TryGetValue(serverObjectId, out description))
            {
                sSimAliases.Remove(serverObjectId);
                ulong mapped;
                if (sSimAliasByDescription.TryGetValue(description, out mapped) && mapped == serverObjectId)
                {
                    sSimAliasByDescription.Remove(description);
                }
            }
        }

        // W2 Sim identity step 3: PROBE_SIM_PRESENCE|corr|sinceSeq (any role, in world). Each call diffs
        // LotManager.Actors against the previous call and journals Sims that appeared ("+") or left ("-")
        // (SimPresenceWire); the first call only takes the baseline. Replies the entries after sinceSeq.
        private const int PresenceJournalCapacity = 512;
        private static Dictionary<ulong, ulong> sPresence;
        private static readonly List<string> sPresenceJournal = new List<string>();
        private static long sPresenceFirstSeq = 1L;
        private static long sPresenceSeq;

        private static void AppendPresence(string entry)
        {
            sPresenceSeq++;
            sPresenceJournal.Add(entry);
            if (sPresenceJournal.Count > PresenceJournalCapacity)
            {
                sPresenceJournal.RemoveAt(0);
                sPresenceFirstSeq++;
            }
        }

        private static void RefreshPresence()
        {
            Dictionary<ulong, ulong> now = new Dictionary<ulong, ulong>();
            Dictionary<ulong, Sim> sims = new Dictionary<ulong, Sim>();
            foreach (Sim sim in LotManager.Actors)
            {
                if (sim == null || sim.SimDescription == null)
                {
                    continue;
                }

                now[sim.ObjectId.Value] = sim.SimDescription.SimDescriptionId;
                sims[sim.ObjectId.Value] = sim;
            }

            if (sPresence == null)
            {
                sPresence = now;
                return;
            }

            foreach (KeyValuePair<ulong, ulong> gone in sPresence)
            {
                if (!now.ContainsKey(gone.Key))
                {
                    AppendPresence(SimPresenceWire.FormatDespawn(gone.Key, gone.Value));
                }
            }

            foreach (KeyValuePair<ulong, ulong> came in now)
            {
                if (!sPresence.ContainsKey(came.Key))
                {
                    Sim sim = sims[came.Key];
                    Vector3 position = sim.Position;
                    ulong lotId = sim.LotCurrent != null ? sim.LotCurrent.LotId : 0uL;
                    AppendPresence(SimPresenceWire.FormatSpawn(came.Key, came.Value, lotId, position.x, position.y, position.z));
                }
            }

            sPresence = now;
        }

        private static void HandleProbeSimPresence(BridgeCommand command)
        {
            long since = 0L;
            if (command.Parts.Length > 2 && !long.TryParse(command.Parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out since))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "PROBE_SIM_PRESENCE sinceSeq must be decimal."));
                return;
            }

            if (!SafeIsInWorld())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "PROBE_SIM_PRESENCE needs a loaded world."));
                return;
            }

            RefreshPresence();
            if (since > sPresenceSeq)
            {
                since = 0L; // the journal restarted (world reload): replay it from the start
            }

            long lost = 0L;
            int start = (int)Math.Max(0L, since + 1L - sPresenceFirstSeq);
            if (since + 1L < sPresenceFirstSeq)
            {
                lost = sPresenceFirstSeq - (since + 1L);
            }

            List<string> entries = new List<string>();
            for (int i = start; i < sPresenceJournal.Count && entries.Count < SimPresenceWire.MaxEntriesPerReply; i++)
            {
                entries.Add(sPresenceJournal[i]);
            }

            long last = sPresenceFirstSeq + start + entries.Count - 1L;
            if (entries.Count == 0)
            {
                last = Math.Max(since, sPresenceSeq);
            }

            bool more = last < sPresenceSeq;
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SimPresenceWire.FormatReply(last, more, lost, entries)));
        }

        // SPAWN_SIM|corr|serverObjectId|simDescriptionId|x|y|z (client role): put this client's instance of
        // the server's Sim into the world (inside the server-apply window, so the authority gates allow it)
        // and record the alias. A description this client does not have is reported, not invented.
        private static void HandleSpawnSim(BridgeCommand command)
        {
            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SPAWN_SIM is client-role only."));
                return;
            }

            ulong serverObjectId;
            ulong simDescriptionId;
            float x;
            float y;
            float z;
            if (command.Parts.Length < 7
                || !ulong.TryParse(command.Parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out serverObjectId)
                || !ulong.TryParse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out simDescriptionId)
                || !float.TryParse(command.Parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(command.Parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !float.TryParse(command.Parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SPAWN_SIM requires serverObjectId simDescriptionId x y z."));
                return;
            }

            AddSimAlias(serverObjectId, simDescriptionId);
            SimDescription description = SimDescription.Find(simDescriptionId);
            if (description == null)
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=missing-description; description=" + simDescriptionId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            if (description.CreatedSim != null)
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=existed; local=" + description.CreatedSim.ObjectId.Value.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            Sim sim = description.Instantiate(new Vector3(x, y, z));
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", sim == null
                ? "result=failed; name=" + SafeString(description.FullName)
                : "result=spawned; local=" + sim.ObjectId.Value.ToString(CultureInfo.InvariantCulture) + "; name=" + SafeString(description.FullName)));
        }

        // step 5: PROBE_SIM_DETAILS|corr|<ids> (server) reports each Sim's job, household funds and skills
        // (SimDetailsWire). APPLY_SIM_DETAILS|corr|<entry> (client) brings this client's copy to them: a different
        // career is left and taken (no UI), then the level set; funds go through the Sim's own household (household
        // ids differ between save copies); skills are raised or lowered to the server's levels.
        private static void HandleProbeSimDetails(BridgeCommand command)
        {
            ulong[] ids = command.Parts.Length < 3 ? null : SimScopeWire.Parse(command.Parts[2]);
            if (ids == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "PROBE_SIM_DETAILS requires decimal, comma-separated Sim ids."));
                return;
            }

            StringBuilder text = new StringBuilder();
            int written = 0;
            for (int i = 0; i < ids.Length && written < SimDetailsWire.MaxSimsPerProbe; i++)
            {
                Sim sim = FindSim(ids[i]);
                if (sim == null || sim.SimDescription == null)
                {
                    continue;
                }

                Occupation occupation = sim.Occupation;
                Sims3.Gameplay.Careers.Career career = occupation as Sims3.Gameplay.Careers.Career;
                List<string> names = new List<string>();
                List<int> levels = new List<int>();
                if (sim.SkillManager != null)
                {
                    foreach (Skill skill in sim.SkillManager.List)
                    {
                        if (skill != null)
                        {
                            names.Add(skill.Guid.ToString());
                            levels.Add(skill.SkillLevel);
                        }
                    }
                }

                if (written > 0)
                {
                    text.Append(';');
                }

                text.Append(SimDetailsWire.FormatEntry(
                    ids[i],
                    occupation == null ? 0uL : (ulong)occupation.Guid,
                    occupation == null ? 0 : occupation.CareerLevel,
                    career != null && career.CurLevel != null ? career.CurLevel.BranchName : null,
                    sim.Household == null ? -1 : sim.Household.FamilyFunds,
                    SimDetailsWire.FormatSkills(names, levels)));
                written++;
            }

            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", written == 0 ? "-" : text.ToString()));
        }

        private static void HandleApplySimDetails(BridgeCommand command)
        {
            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_SIM_DETAILS is client-role only."));
                return;
            }

            ulong simId;
            ulong careerGuid;
            int level;
            string branch;
            int funds;
            string skills;
            if (command.Parts.Length < 3 || !SimDetailsWire.TryParseEntry(command.Parts[2], out simId, out careerGuid, out level, out branch, out funds, out skills))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_SIM_DETAILS requires one SimDetailsWire entry."));
                return;
            }

            Sim sim = FindSim(simId);
            if (sim == null || sim.SimDescription == null)
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=absent"));
                return;
            }

            List<string> changes = new List<string>();
            try
            {
                if (funds >= 0 && sim.Household != null && sim.Household.FamilyFunds != funds)
                {
                    sim.Household.ModifyFamilyFunds(funds - sim.Household.FamilyFunds);
                    changes.Add("funds");
                }

                Occupation occupation = sim.Occupation;
                ulong currentGuid = occupation == null ? 0uL : (ulong)occupation.Guid;
                if (currentGuid != careerGuid)
                {
                    if (occupation != null)
                    {
                        occupation.LeaveJob(false, Sims3.Gameplay.Careers.Career.LeaveJobReason.kJobBecameInvalid);
                    }

                    if (careerGuid != 0uL)
                    {
                        sim.AcquireOccupation(NewJobParameters(sim, (OccupationNames)careerGuid));
                    }

                    changes.Add(sim.Occupation == null ? (careerGuid == 0uL ? "job-left" : "job-failed") : "job");
                    occupation = sim.Occupation;
                }

                Sims3.Gameplay.Careers.Career career = occupation as Sims3.Gameplay.Careers.Career;
                if (career != null && level > 0
                    && (career.CareerLevel != level || (career.CurLevel != null && SimDetailsWire.CleanToken(career.CurLevel.BranchName) != branch)))
                {
                    CareerLevel target = FindCareerLevel(career, branch, level);
                    if (target != null)
                    {
                        career.CurLevel = target;
                        changes.Add("level");
                    }
                    else
                    {
                        changes.Add("level-missing");
                    }
                }

                List<string> names = new List<string>();
                List<int> levels = new List<int>();
                if (sim.SkillManager != null && SimDetailsWire.TryParseSkills(skills, names, levels))
                {
                    for (int i = 0; i < names.Count; i++)
                    {
                        SkillNames kind;
                        try
                        {
                            kind = (SkillNames)Enum.Parse(typeof(SkillNames), names[i], true);
                        }
                        catch (Exception)
                        {
                            changes.Add("skill-unknown");
                            continue;
                        }

                        Skill skill = sim.SkillManager.GetElement(kind);
                        if (skill != null && skill.SkillLevel == levels[i])
                        {
                            continue;
                        }

                        if (skill == null)
                        {
                            skill = sim.SkillManager.AddElement(kind);
                        }

                        if (skill == null)
                        {
                            continue;
                        }

                        int targetLevel = Math.Min(levels[i], skill.MaxSkillLevel);
                        if (skill.SkillLevel < targetLevel)
                        {
                            skill.ForceSkillLevelUp(targetLevel);
                        }
                        else if (skill.SkillLevel > targetLevel)
                        {
                            skill.SkillLevel = targetLevel;
                        }

                        changes.Add("skill");
                    }
                }
            }
            catch (Exception ex)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_SIM_DETAILS failed after [" + string.Join(",", changes.ToArray()) + "]: "
                    + ex.GetType().Name + ": " + ex.Message));
                return;
            }

            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", changes.Count == 0 ? "result=same" : "result=applied; " + string.Join(",", changes.ToArray())));
        }

        // Like the game's own job offers: at the closest work place (CareerManager passes the location into
        // SetAttributesForNewJob; without one a Career is left half set up, level -1), no UI, no "leave your old
        // job?" question.
        private static AcquireOccupationParameters NewJobParameters(Sim sim, OccupationNames career)
        {
            CareerLocation location = Sims3.Gameplay.Careers.Career.FindClosestCareerLocation(sim, career);
            AcquireOccupationParameters parameters = location == null
                ? new AcquireOccupationParameters(career, false, false)
                : new AcquireOccupationParameters(career, location, false, false);
            parameters.ForceAutomaticChange = true;
            return parameters;
        }

        // The career's level in the server's branch; below the branch split (or for a branch this copy doesn't
        // know), the same level in any branch.
        private static CareerLevel FindCareerLevel(Sims3.Gameplay.Careers.Career career, string branch, int level)
        {
            Dictionary<string, Dictionary<int, CareerLevel>> byBranch = career.CareerLevels;
            if (byBranch == null)
            {
                return null;
            }

            CareerLevel found;
            foreach (KeyValuePair<string, Dictionary<int, CareerLevel>> pair in byBranch)
            {
                if (pair.Value != null && SimDetailsWire.CleanToken(pair.Key) == branch && pair.Value.TryGetValue(level, out found))
                {
                    return found;
                }
            }

            foreach (KeyValuePair<string, Dictionary<int, CareerLevel>> pair in byBranch)
            {
                if (pair.Value != null && pair.Value.TryGetValue(level, out found))
                {
                    return found;
                }
            }

            return null;
        }

        // EXPORT_SIM_DESCRIPTION|corr|simDescriptionId (server role). One Sim description through the save
        // path (SimDescription.ExportContent), published to a host BULK slot as property stream + key table +
        // object id table. Report: result=exported; slot; bytes; crc; props; keys; objects; name (or result=missing).
        private static void HandleExportSimDescription(BridgeCommand command)
        {
            if (sCurrentRole != "server")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "EXPORT_SIM_DESCRIPTION is server-role only."));
                return;
            }

            ulong simDescriptionId;
            if (command.Parts.Length < 3 || !ulong.TryParse(command.Parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out simDescriptionId))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "EXPORT_SIM_DESCRIPTION requires simDescriptionId."));
                return;
            }

            SimDescription description = SimDescription.Find(simDescriptionId);
            if (description == null)
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=missing"));
                return;
            }

            if (!BridgeBulkBuffer.HasWritableHostSlots(1))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "EXPORT_SIM_DESCRIPTION BULK slot unavailable."));
                return;
            }

            byte[] payload;
            int propertyBytes;
            int keyBytes;
            int objectBytes;
            try
            {
                if (description.Household != null)
                {
                    // Genealogy.ExportContent throws on a missing back-reference.
                    PatchGenealogyBackReferences(description.Household);
                }

                MemoryStream stream = new MemoryStream();
                BinaryWriter binaryWriter = new BinaryWriter(stream);
                PropertyStreamWriter writer = new PropertyStreamWriter();
                writer.Open(binaryWriter);
                ResKeyTable resKeys = new ResKeyTable();
                ObjectIdTable objectIds = new ObjectIdTable();
                bool exported = description.ExportContent(resKeys, objectIds, writer);
                writer.Close();
                binaryWriter.Flush();
                if (!exported)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "EXPORT_SIM_DESCRIPTION SimDescription.ExportContent returned false."));
                    return;
                }

                byte[] properties = stream.ToArray();
                byte[] keys = resKeys.Serialize() ?? new byte[0];
                byte[] objects = objectIds.Serialize() ?? new byte[0];
                propertyBytes = properties.Length;
                keyBytes = keys.Length;
                objectBytes = objects.Length;
                payload = new byte[propertyBytes + keyBytes + objectBytes];
                Buffer.BlockCopy(properties, 0, payload, 0, propertyBytes);
                Buffer.BlockCopy(keys, 0, payload, propertyBytes, keyBytes);
                Buffer.BlockCopy(objects, 0, payload, propertyBytes + keyBytes, objectBytes);
            }
            catch (Exception ex)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "EXPORT_SIM_DESCRIPTION failed: " + ex.GetType().Name + ": " + ex.Message));
                return;
            }

            int slotIndex;
            uint crc32;
            if (payload.Length > BridgeSharedRegionLayout.BulkSlotPayloadMax
                || !BridgeBulkBuffer.TryPublishHostBulk(command.CorrelationId, payload, out slotIndex, out crc32))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "EXPORT_SIM_DESCRIPTION BULK publication failed."));
                return;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                "result=exported; slot=" + slotIndex.ToString(c) + "; bytes=" + payload.Length.ToString(c) + "; crc=" + crc32.ToString("X8", c)
                + "; props=" + propertyBytes.ToString(c) + "; keys=" + keyBytes.ToString(c) + "; objects=" + objectBytes.ToString(c)
                + "; name=" + SafeString(description.FullName)));
        }

        // IMPORT_SIM_DESCRIPTION|corr|simDescriptionId|totalBytes|props|keys|objects|crcHex (client role), after
        // BULK_REF uploaded the bytes. A fresh SimDescription keeps the server's id (ids are only remapped inside a
        // household import) and joins the NPC household, so SPAWN_SIM finds it. Report: result=imported|existed; name.
        private static void HandleImportSimDescription(BridgeCommand command)
        {
            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "IMPORT_SIM_DESCRIPTION is client-role only."));
                return;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ulong simDescriptionId;
            int totalBytes;
            int propertyBytes;
            int keyBytes;
            int objectBytes;
            uint crc32;
            if (command.Parts.Length < 8
                || !ulong.TryParse(command.Parts[2], NumberStyles.None, c, out simDescriptionId)
                || !int.TryParse(command.Parts[3], NumberStyles.None, c, out totalBytes)
                || !int.TryParse(command.Parts[4], NumberStyles.None, c, out propertyBytes)
                || !int.TryParse(command.Parts[5], NumberStyles.None, c, out keyBytes)
                || !int.TryParse(command.Parts[6], NumberStyles.None, c, out objectBytes)
                || !uint.TryParse(command.Parts[7], NumberStyles.HexNumber, c, out crc32))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "IMPORT_SIM_DESCRIPTION requires simDescriptionId|totalBytes|props|keys|objects|crc."));
                return;
            }

            SimDescription existing = SimDescription.Find(simDescriptionId);
            if (existing != null)
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=existed; name=" + SafeString(existing.FullName)));
                return;
            }

            if (sLastBlobBytes == null || sLastBlobBytes.Length != totalBytes || propertyBytes + keyBytes + objectBytes != totalBytes
                || ComputeCrc32(sLastBlobBytes) != crc32)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "IMPORT_SIM_DESCRIPTION needs the matching BULK blob (send BULK_REF first)."));
                return;
            }

            byte[] properties = new byte[propertyBytes];
            byte[] keys = new byte[keyBytes];
            byte[] objects = new byte[objectBytes];
            Buffer.BlockCopy(sLastBlobBytes, 0, properties, 0, propertyBytes);
            Buffer.BlockCopy(sLastBlobBytes, propertyBytes, keys, 0, keyBytes);
            Buffer.BlockCopy(sLastBlobBytes, propertyBytes + keyBytes, objects, 0, objectBytes);
            try
            {
                PropertyStreamReader reader = new PropertyStreamReader();
                reader.Open(new BinaryReader(new MemoryStream(properties)));
                SimDescription description = new SimDescription();
                bool imported = description.ImportContent(new ResKeyTable(keys), new ObjectIdTable(objects), reader);
                reader.Close();
                if (!imported || description.SimDescriptionId != simDescriptionId)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "IMPORT_SIM_DESCRIPTION import failed (imported=" + imported
                        + ", id=" + description.SimDescriptionId.ToString(c) + ")."));
                    return;
                }

                Household.NpcHousehold.Add(description);
                string fixup = string.Empty;
                try
                {
                    description.Fixup();
                }
                catch (Exception fx)
                {
                    fixup += "; fixup=" + fx.GetType().Name;
                }

                try
                {
                    // Commits the imported genealogy: parents, spouse and siblings are found by id.
                    Household.NpcHousehold.FixupGenealogy();
                }
                catch (Exception gx)
                {
                    fixup += "; genealogy=" + gx.GetType().Name;
                }

                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=imported; name=" + SafeString(description.FullName) + fixup));
            }
            catch (Exception ex)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "IMPORT_SIM_DESCRIPTION failed: " + ex.GetType().Name + ": " + ex.Message));
            }
        }

        // DESPAWN_SIM|corr|serverObjectId|simDescriptionId (client role): the server removed this Sim from
        // its world; remove this client's instance. Never a Sim this client runs natively or can select.
        private static ulong[] sAuthorityOwned = new ulong[0];

        private static void HandleDespawnSim(BridgeCommand command)
        {
            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "DESPAWN_SIM is client-role only."));
                return;
            }

            ulong serverObjectId;
            ulong simDescriptionId;
            if (command.Parts.Length < 4
                || !ulong.TryParse(command.Parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out serverObjectId)
                || !ulong.TryParse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out simDescriptionId))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "DESPAWN_SIM requires serverObjectId simDescriptionId."));
                return;
            }

            RemoveSimAlias(serverObjectId);
            Sim local = FindSimByDescription(simDescriptionId);
            if (local == null)
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=absent"));
                return;
            }

            ulong localId = local.ObjectId.Value;
            for (int i = 0; i < sAuthorityOwned.Length; i++)
            {
                if (sAuthorityOwned[i] == localId)
                {
                    SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=native"));
                    return;
                }
            }

            if (local.IsSelectable)
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=selectable"));
                return;
            }

            string name = SafeString(local.FullName);
            local.Destroy();
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "result=despawned; local=" + localId.ToString(CultureInfo.InvariantCulture) + "; name=" + name));
        }

        // server-vs-client comparison (scripts/presentation_compare.py): what each Sim looks like
        // right now, read-only, any role. PROBE_PRESENTATION|corr[|lots[|sims]]: Sims on the given lots
        // (SimScopeWire list, "-" = every lot) plus the listed Sims. Reply: "sims=N;tick=T" then one line
        // per Sim, '|'-separated (the report is hex-encoded on the wire):
        // objectId|simDescriptionId|lotId|x|y|z|postureType|containerId|containerType|interactionKey|postureMachine|interactionMachine|rightHand|leftHand|sacs
        // sacs = SacsRecorder's last machine:state for the Sim ("-" until SACS_RECORDER on and a state change).
        // postureMachine / interactionMachine = the StateMachineClient names animating the Sim ("-" when none).
        private static void HandleProbePresentation(BridgeCommand command)
        {
            ulong[] lots = command.Parts.Length > 2 ? SimScopeWire.Parse(command.Parts[2]) : new ulong[0];
            ulong[] sims = command.Parts.Length > 3 ? SimScopeWire.Parse(command.Parts[3]) : new ulong[0];
            if (lots == null || sims == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "PROBE_PRESENTATION lots and sims must be SimScopeWire id lists or '-'."));
                return;
            }

            bool allLots = command.Parts.Length <= 2 || (lots.Length == 0 && sims.Length == 0);
            Dictionary<ulong, bool> lotSet = new Dictionary<ulong, bool>();
            for (int i = 0; i < lots.Length; i++)
            {
                lotSet[lots[i]] = true;
            }

            Dictionary<ulong, bool> simSet = new Dictionary<ulong, bool>();
            for (int i = 0; i < sims.Length; i++)
            {
                simSet[sims[i]] = true;
            }

            StringBuilder text = new StringBuilder();
            int count = 0;
            foreach (Sim sim in LotManager.Actors)
            {
                if (sim == null || sim.SimDescription == null)
                {
                    continue;
                }

                ulong lotId = sim.LotCurrent != null ? sim.LotCurrent.LotId : 0uL;
                ulong wireId = WireSimObjectId(sim);
                if (!allLots && !lotSet.ContainsKey(lotId) && !simSet.ContainsKey(wireId) && !simSet.ContainsKey(sim.ObjectId.Value))
                {
                    continue;
                }

                try
                {
                    text.Append('\n').Append(PresentationLine(sim, wireId, lotId));
                    count++;
                }
                catch (Exception ex)
                {
                    text.Append('\n').Append(wireId.ToString(CultureInfo.InvariantCulture)).Append("|!").Append(ex.GetType().Name);
                }
            }

            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                "sims=" + count.ToString(CultureInfo.InvariantCulture) + ";tick=" + SafeTick().ToString(CultureInfo.InvariantCulture) + text.ToString()));
        }

        // SACS_RECORDER|corr|on|off|status|ring-on|ring-off|dump[|sinceSeq[|targets]] (any role): install / remove the
        // SacsRecorder (ISacsProxy decorator, forwards every call), or read its call ring. dump replies
        // "seq=<last>;more=<0|1>" then one ring line per row ("seq|kind|...").
        private static void HandleSacsRecorder(BridgeCommand command)
        {
            string action = command.Parts.Length > 2 ? command.Parts[2] : "status";
            string state;
            if (action == "on")
            {
                state = SacsRecorder.Install();
            }
            else if (action == "off")
            {
                SacsRecorder.RingEnabled = false;
                SacsRecorder.TraceTimestamps = false;
                state = SacsRecorder.Uninstall();
            }
            else if (action == "ring-on")
            {
                state = SacsRecorder.Install();
                SacsRecorder.RingEnabled = true;
                // Opt-in only; old clients/controllers continue using the same wire.
                SacsRecorder.TraceTimestamps = command.Parts.Length > 3 && command.Parts[3] == "trace";
            }
            else if (action == "routes-on" || action == "routes-off")
            {
                // the route stream is opt-in (the controller sends routes-on for SIMSONLINE_ROUTE_STREAM=1).
                sRouteStreamEnabled = action == "routes-on";
                sPublishedRoutes.Clear();
                state = "routes=" + (sRouteStreamEnabled ? "on" : "off");
            }
            else if (action == "rust-road-place-on")
            {
                RustRoadPlace.Enabled = command.Parts.Length <= 3 || command.Parts[3] != "0";
                RustRoadPlace.RequestSender = RustRoadPlace.Enabled ? new RustRoutePlan.LineSender(SendLine) : null;
                state = RustRoadPlace.DescribeStatus() + ";" + (RustRoadPlace.Enabled ? WorldRecorder.Install() : "Wd=off");
            }
            else if (action == "road-place-census")
            {
                state = RustRoadPlace.DescribeStatus();
            }
            else if (action == "rust-route-client" || action == "rust-route-plans")
            {
                ulong corr = 0uL;
                if (command.Parts.Length > 3 && !ulong.TryParse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out corr))
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "bad navigation correlation"));
                    return;
                }
                if (command.Parts.Length >= 6)
                {
                    ulong actor;
                    uint cursor, maximum = action == "rust-route-plans" ? 16u : 128u;
                    ulong epoch = 0;
                    if (corr == 0 || command.Parts.Length > 8 ||
                        !ulong.TryParse(command.Parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out actor) ||
                        !uint.TryParse(command.Parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out cursor) ||
                        (command.Parts.Length >= 7 && !uint.TryParse(command.Parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out maximum)) ||
                        (command.Parts.Length == 8 && !ClientBoundaryRoute.TryNavEpoch(command.Parts[7], out epoch)))
                    {
                        SendLine(BridgeCodec.Error(command.CorrelationId, "bad navigation page")); return;
                    }
                    SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", action == "rust-route-plans"
                        ? ClientBoundaryRoute.Receipts.ReportPlansPage(corr, actor, cursor, maximum, command.Parts.Length == 8 ? (ulong?)epoch : null)
                        : ClientBoundaryRoute.Receipts.ReportClientPage(corr, actor, cursor, maximum, command.Parts.Length == 8 ? (ulong?)epoch : null)));
                    return;
                }
                if (command.Parts.Length == 5)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "navigation page requires cursor")); return;
                }
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                    ClientBoundaryRoute.Receipts.Report(corr, action == "rust-route-plans")));
                return;
            }
            else if (action == "rust-navigation-sync" || action == "rust-navigation-result" || action == "rust-navigation-offer" ||
                action == "rust-simnav-install" || action == "rust-navigation-status")
            {
                try
                {
                    string answer;
                    if (action == "rust-navigation-sync")
                    {
                        if (command.Parts.Length != 7) throw new FormatException("sync fields");
                        answer = RustLiveSimNavBuilder.AcceptSync(ulong.Parse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture),
                            uint.Parse(command.Parts[4], NumberStyles.None, CultureInfo.InvariantCulture),
                            uint.Parse(command.Parts[5], NumberStyles.None, CultureInfo.InvariantCulture), command.Parts[6]);
                    }
                    else if (action == "rust-navigation-result")
                    {
                        if (command.Parts.Length != 4) throw new FormatException("result fields");
                        answer = RustLiveSimNavBuilder.Result(RustPrivateNavigation.Unhex(command.Parts[3]));
                    }
                    else if (action == "rust-navigation-offer")
                    {
                        if (command.Parts.Length != 9) throw new FormatException("offer fields");
                        byte[] bytes = sNavBlobs.Take(command.Parts[3]);
                        if (bytes == null) throw new FormatException("named navigation blob missing");
                        answer = RustLiveSimNavBuilder.Offer(command.Parts[3],
                            ulong.Parse(command.Parts[4], NumberStyles.None, CultureInfo.InvariantCulture),
                            ulong.Parse(command.Parts[5], NumberStyles.None, CultureInfo.InvariantCulture),
                            ulong.Parse(command.Parts[6], NumberStyles.None, CultureInfo.InvariantCulture),
                            ulong.Parse(command.Parts[7], NumberStyles.None, CultureInfo.InvariantCulture),
                            ulong.Parse(command.Parts[8], NumberStyles.None, CultureInfo.InvariantCulture), bytes);
                    }
                    else if (action == "rust-simnav-install")
                    {
                        if (command.Parts.Length < 4 || command.Parts.Length > 5) throw new FormatException("install fields");
                        ulong actor = command.Parts.Length == 5 ? ulong.Parse(command.Parts[4], NumberStyles.None, CultureInfo.InvariantCulture) : 0uL;
                        answer = RustLiveSimNavBuilder.BeginCapture(ulong.Parse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture),
                            actor, command.CorrelationId);
                        if (answer == null) return;
                    }
                    else answer = RustLiveSimNavBuilder.Status();
                    SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", answer));
                }
                catch (Exception e)
                {
                    if (RustRoutePlan.MustRethrow(e)) throw;
                    if (action == "rust-navigation-sync") RustLiveSimNavBuilder.Sync.AbortSync();
                    SendLine(BridgeCodec.Error(command.CorrelationId, e is FormatException ? e.Message : "invalid navigation command"));
                }
                return;
            }
            else if (action == "rust-road-place" || action == "rust-road-place-none" || action == "rust-road-place-decline")
            {
                ulong corr;
                RustRoadPlace.Answer answer;
                bool accepted = false;
                if (action == "rust-road-place")
                {
                    accepted = RustRoadPlace.TryParseAnswer(command.Parts, 3, out corr, out answer) && RustRoadPlace.Offer(corr, answer);
                }
                else if (command.Parts.Length > 3 && ulong.TryParse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out corr))
                {
                    accepted = action == "rust-road-place-none"
                        ? RustRoadPlace.OfferNone(corr, command.Parts.Length > 4 ? command.Parts[4] : null)
                        : RustRoadPlace.Decline(corr, command.Parts.Length > 4 ? command.Parts[4] : null);
                }
                state = "roadPlaceAnswer=" + (accepted ? "accepted" : "refused");
            }
            else if (action == "rust-route-on" || action == "rust-route-off")
            {
                // card 55: the server's walks take the Rust plan at the RouteCtor seam (RouteRecorder's plan
                // overrides). Opt-in: the controller sends rust-route-on for SIMSONLINE_RUST_ROUTE=1.
                RustRoutePlan.Enabled = action == "rust-route-on";
                RustRoutePlan.RequestSender = RustRoutePlan.Enabled ? new RustRoutePlan.LineSender(SendLine) : null;
                RustLiveSimNavBuilder.Sender = RustRoutePlan.RequestSender;
                if (!RustRoutePlan.Enabled) RustLiveSimNavBuilder.Reset();
                state = "rustRoute=" + (RustRoutePlan.Enabled ? "on" : "off") + ";routeSeam=" + (RouteRecorder.Installed ? "on" : "off");
            }
            else if (action == "rust-plan" || action == "rust-plan-none")
            {
                // card 55 R3: the controller's answer to EVENT|RoutePlanRequest. rust-plan <sim> <x,y,z,...> offers the
                // plan (RustRoutePlan.Offer: the waiting Plan* call takes it); rust-plan-none <sim> ends the wait now.
                ulong planSim = 0uL;
                float[] planPoints;
                bool parsed = command.Parts.Length > 3 && ulong.TryParse(command.Parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out planSim);
                if (action == "rust-plan-none")
                {
                    ulong corr;
                    state = "rustPlan=" + (parsed && command.Parts.Length > 4
                        && ulong.TryParse(command.Parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out corr)
                        && RustRoutePlan.DeclineWait(corr) ? "declined" : "ignored");
                }
                else
                {
                    ulong corr;
                    state = "rustPlan=" + (parsed && command.Parts.Length > 4 && RustRoutePlan.TryParsePoints(command.Parts[4], out planPoints)
                        && (command.Parts.Length > 5
                            ? ulong.TryParse(command.Parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out corr) && RustRoutePlan.OfferWait(corr, planPoints)
                            : RustRoutePlan.Offer(planSim, planPoints)) ? "offered" : "refused");
                }
            }
            else if (action == "sidecar-on")
            {
                // Experiment C: sent by the controller only after the SimLink sidecar reported its internal calls
                // registered (an unregistered internal call is fatal). Shadow only: the bridge stream is unchanged.
                state = SimLinkShadow.Enable();
            }
            else if (action == "sidecar-off")
            {
                SimLinkShadow.Enabled = false;
                state = SimLinkShadow.Describe();
            }
            else if (action == "sidecar-status")
            {
                state = SimLinkShadow.Describe();
            }
            else if (action == "sidecar-client-on")
            {
                // Experiment C, client side: sent by the client controller only after the sidecar registered.
                state = SimLinkShadow.EnableClient();
            }
            else if (action == "sidecar-client-off")
            {
                SimLinkShadow.ClientEnabled = false;
                state = SimLinkShadow.Describe();
            }
            else if (action == "push-on")
            {
                // Server ring path: the mod runs the dump itself every tick and pushes its pages through the SimLink
                // ring. push-on|cursor|targets; sent only after the sidecar registered. Exclusive with dump.
                long cursor = 0L;
                if (command.Parts.Length > 3)
                {
                    long.TryParse(command.Parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out cursor);
                }

                if (!SacsRecorder.RingEnabled || !SimLinkShadow.EnablePush())
                {
                    state = "push=refused,ring=" + (SacsRecorder.RingEnabled ? "on" : "off") + "," + SimLinkShadow.Describe();
                }
                else
                {
                    sPushCursor = Math.Min(Math.Max(0L, cursor), SacsRecorder.Sequence);
                    sPushTargets = ParsePushTargets(command.Parts.Length > 4 ? command.Parts[4] : "-");
                    sPushNextObserve = DateTime.MinValue;
                    sPushLastPage = DateTime.MinValue;
                    sPushEnabled = true;
                    ScheduleServerPushTask();
                    state = DescribePush();
                }
            }
            else if (action == "push-targets")
            {
                sPushTargets = ParsePushTargets(command.Parts.Length > 3 ? command.Parts[3] : "-");
                sPushNextObserve = DateTime.MinValue; // adopt the new targets at the next tick, not up to 250 ms later
                state = DescribePush();
            }
            else if (action == "push-off")
            {
                sPushEnabled = false;
                state = DescribePush();
            }
            else if (action == "push-status")
            {
                state = DescribePush();
            }
            else if (action == "presenter-idle")
            {
                // presenter-idle on|off|status: whether the stream owns a puppet's standing idle (fork PresenterHook), for
                // A/B runs in one session.
                // Client only: on and off go through PresenterOn, which switches presenter mode on; status only resolves
                // the hook, so reading the counters (replay_check, each scenario) never switches it on.
                string mode = command.Parts.Length > 3 ? command.Parts[3] : "status";
                bool client = sCurrentRole == "client" && (mode == "on" || mode == "off" ? PresenterOn() : ResolvePresenter());
                MethodInfo set = !client || sPresenterHookType == null ? null : sPresenterHookType.GetMethod("SetStreamOwnsStandingIdle");
                if (set != null && (mode == "on" || mode == "off"))
                {
                    set.Invoke(null, new object[] { mode == "on" });
                }

                state = "presenterIdle=" + (set == null ? "absent" : mode) + "," + PresenterReport();
            }
            else if (action == "push-every")
            {
                // Experiment: push on every Nth simulator update (1 = every update, the default; 2 = the cadence the
                // self-rescheduling task had before push-097). For measuring the push task's cost at high speed.
                int every;
                if (command.Parts.Length > 3 && int.TryParse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out every)
                    && every >= 1 && every <= 8)
                {
                    sPushEvery = every;
                }

                state = DescribePush();
            }
            else if (action == "sidecar-client-ring")
            {
                // Experiment C, client ring path: sent by the client controller only after the sidecar registered.
                state = SimLinkShadow.EnableClientRing();
                if (SimLinkShadow.RingEnabled)
                {
                    ScheduleRingTask();
                }
            }
            else if (action == "sidecar-client-ring-off")
            {
                SimLinkShadow.RingEnabled = false;
                state = SimLinkShadow.Describe();
            }
            else if (action == "pump-ms")
            {
                // Runtime A/B: lower the next idle game-bridge wait without a
                // controller restart (the default is 50 since 2026-10-21; 250 is the old
                // one); no value reports the current one (scripts/pump_ms.py status).
                int requested;
                if (command.Parts.Length < 4)
                {
                    requested = sBridgeIdlePollMs;
                }
                else if (!int.TryParse(command.Parts[3], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out requested)
                    || requested < 25 || requested > 250)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId,
                        "SACS_RECORDER pump-ms requires an interval between 25 and 250 ms."));
                    return;
                }
                sBridgeIdlePollMs = requested;
                state = "pumpMs=" + requested.ToString(CultureInfo.InvariantCulture);
            }
            else if (action == "ring-off")
            {
                SacsRecorder.RingEnabled = false;
                SacsRecorder.TraceTimestamps = false;
                state = "ring=off";
            }
            else if (action == "dump" && sPushEnabled)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SACS_RECORDER dump while push is on (send push-off first)."));
                return;
            }
            else if (action == "dump")
            {
                long since = 0L;
                if (command.Parts.Length > 3)
                {
                    long.TryParse(command.Parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out since);
                }

                // Optional 4th field: target object ids (SimScopeWire list); only their drivers' lines.
                Dictionary<ulong, bool> targets = null;
                if (command.Parts.Length > 4)
                {
                    ulong[] ids = SimScopeWire.Parse(command.Parts[4]);
                    targets = new Dictionary<ulong, bool>();
                    for (int i = 0; ids != null && i < ids.Length; i++)
                    {
                        targets[ids[i]] = true;
                    }
                }

                if (since > SacsRecorder.Sequence)
                {
                    since = SacsRecorder.Sequence; // a cursor from before a reload, or "from now on"
                }

                AdoptTargetDrivers(targets, false);
                PublishRoutes(targets);
                PollPropVisibility(targets);
                PublishPositions(targets);

                // A small optional metadata header samples the first and last REAL
                // ring event times. No replay line is modified; no extra bridge call.
                // Reserve control-frame space when trace metadata is present.
                bool trace = SacsRecorder.TraceTimestamps
                    && command.Parts.Length > 5 && command.Parts[5] == "trace";
                long last;
                List<string> lines = SacsRecorder.RingSince(since, trace ? 1300 : 1500, targets, out last);
                string clock = "";
                if (trace)
                {
                    long firstSeq = 0L;
                    long lastSeq = 0L;
                    StringBuilder sacsSamples = new StringBuilder();
                    int sacsSampleCount = 0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string row = lines[i];
                        int delimiter = row.IndexOf('|');
                        long rowSeq;
                        if (delimiter > 0 && long.TryParse(row.Substring(0, delimiter),
                            NumberStyles.None, CultureInfo.InvariantCulture, out rowSeq) && rowSeq > 0L)
                        {
                            if (firstSeq == 0L) firstSeq = rowSeq;
                            lastSeq = rowSeq;
                            if (sacsSampleCount < 4 && SacsRecorder.IsDirectSacsSequence(rowSeq))
                            {
                                long stamp = SacsRecorder.SourceUtcTicks(rowSeq);
                                if (stamp > 0L)
                                {
                                    if (sacsSampleCount > 0) sacsSamples.Append(',');
                                    sacsSamples.Append(rowSeq.ToString(CultureInfo.InvariantCulture))
                                        .Append('@').Append(stamp.ToString(CultureInfo.InvariantCulture));
                                    sacsSampleCount++;
                                }
                            }
                        }
                    }
                    if (firstSeq != 0L && lastSeq != 0L)
                    {
                        long firstUtc = SacsRecorder.SourceUtcTicks(firstSeq);
                        long lastUtc = SacsRecorder.SourceUtcTicks(lastSeq);
                        if (firstUtc > 0L && lastUtc > 0L)
                        {
                            clock = ";source_first_seq=" + firstSeq.ToString(CultureInfo.InvariantCulture)
                                + ";source_first_utc_ticks=" + firstUtc.ToString(CultureInfo.InvariantCulture)
                                + ";source_last_seq=" + lastSeq.ToString(CultureInfo.InvariantCulture)
                                + ";source_last_utc_ticks=" + lastUtc.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    if (sacsSampleCount > 0)
                    {
                        clock += ";sacs_sources=" + sacsSamples.ToString();
                    }
                }

                LineKindCounts.AddLines(sSentByKind, lines);
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                    "seq=" + last.ToString(CultureInfo.InvariantCulture) + ";more=" + (last < SacsRecorder.Sequence ? "1" : "0") + clock + "\n" + string.Join("\n", lines.ToArray())));
                return;
            }
            else if (action == "snapshot")
            {
                // late join: the live drivers animating the given targets (handles, comma-joined).
                Dictionary<ulong, bool> targets = new Dictionary<ulong, bool>();
                ulong[] ids = command.Parts.Length > 3 ? SimScopeWire.Parse(command.Parts[3]) : null;
                for (int i = 0; ids != null && i < ids.Length; i++)
                {
                    targets[ids[i]] = true;
                }

                AdoptTargetDrivers(targets, true);
                List<uint> handles = SacsRecorder.SnapshotDrivers(targets);
                List<string> text = new List<string>();
                for (int i = 0; i < handles.Count; i++)
                {
                    text.Add(handles[i].ToString(CultureInfo.InvariantCulture));
                }

                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "drivers=" + (text.Count == 0 ? "-" : string.Join(",", text.ToArray()))));
                return;
            }
            else if (action == "snapshot-driver")
            {
                // snapshot-driver <handle> [offset]: the driver's own calls (its path to the current state),
                // paged to fit one bridge frame; next = offset of the following page or -1.
                uint handle = 0u;
                int offset = 0;
                if (command.Parts.Length > 3)
                {
                    uint.TryParse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out handle);
                }

                if (command.Parts.Length > 4)
                {
                    int.TryParse(command.Parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
                }

                int next;
                List<string> lines = SacsRecorder.SnapshotDriver(handle, offset, 1500, out next);
                LineKindCounts.AddLines(sSentByKind, lines); // the history's lines (seq 0) count as sent too
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                    "lines=" + lines.Count.ToString(CultureInfo.InvariantCulture) + ";next=" + next.ToString(CultureInfo.InvariantCulture)
                    + ";dropped=" + SacsRecorder.SnapshotDropped(handle).ToString(CultureInfo.InvariantCulture)
                    + "\n" + string.Join("\n", lines.ToArray())));
                return;
            }
            else if (action == "local")
            {
                // the client's own state calls by machine, with the first caller's stack.
                int offset = 0;
                if (command.Parts.Length > 3)
                {
                    int.TryParse(command.Parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
                }

                NameAllMachines();
                int next;
                List<string> rows = SacsRecorder.DescribeLocal(offset, 1400, out next);
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                    "localCalls=" + SacsRecorder.LocalCalls.ToString(CultureInfo.InvariantCulture) + ";next=" + next.ToString(CultureInfo.InvariantCulture)
                    + "\n" + string.Join("\n", rows.ToArray())));
                return;
            }
            else if (action == "request-trace")
            {
                string mode = command.Parts.Length > 3 ? command.Parts[3] : "";
                if (mode == "on") { SacsRecorder.RequestTrace.Enabled = true; }
                if (mode == "off") { SacsRecorder.RequestTrace.Enabled = false; }
                if (mode == "clear") { SacsRecorder.RequestTrace.Clear(); }
                int offset = 0;
                if (command.Parts.Length > 4)
                {
                    int.TryParse(command.Parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
                }
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                    SacsRecorder.RequestTrace.Read(mode == "failures", offset, 1300)));
                return;
            }
            else if (action == "replay-failures")
            {
                // Newest first, within one bridge frame (replies over a few KB time out).
                List<string> rows = new List<string>();
                int chars = 0;
                int first = Math.Max(0, sReplayFailureCount - sReplayFailureLines.Length);
                for (int i = sReplayFailureCount - 1; i >= first && chars < 1400; i--)
                {
                    string row = sReplayFailureLines[i % sReplayFailureLines.Length];
                    rows.Add(row);
                    chars += row.Length + 1;
                }

                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                    "failures=" + sReplayFailureCount.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", rows.ToArray())));
                return;
            }
            else if (action == "presentation-load")
            {
                // Client: presentation-load [keep]: the presentation module last uploaded (BULK_UPLOAD / DIAG_BLOB)
                // replaces the active one (scripts/presentation_reload.py). The old module's own state always resets;
                // without keep, what the shell presented resets too (a rejoin then rebuilds it). keep swaps the module
                // alone, in a second or so: right for code whose state is per walk or per line. (No load from a path:
                // the game's runtime has no System.IO.File, and a reference to it broke this whole handler in-game.)
                bool keepPresentation = command.Parts.Length > 3 && command.Parts[3] == "keep";
                byte[] moduleBytes = sLastBlobBytes;
                if (moduleBytes == null)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "presentation-load: no module uploaded (BULK_UPLOAD / DIAG_BLOB the module DLL first)"));
                    return;
                }

                DateTime loadStarted = DateTime.UtcNow;
                string loaded = PresentationModules.Load(moduleBytes);
                string reset = "kept";
                if (loaded.StartsWith("module=", StringComparison.Ordinal) && !keepPresentation)
                {
                    sLastPresentationReset = ResetPresentation();
                    reset = sLastPresentationReset;
                }

                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, loaded.StartsWith("module=", StringComparison.Ordinal) ? "ok" : "error",
                    loaded + ";reset=" + reset + ";bytes=" + moduleBytes.Length.ToString(CultureInfo.InvariantCulture)
                    + ";loadMs=" + ((long)(DateTime.UtcNow - loadStarted).TotalMilliseconds).ToString(CultureInfo.InvariantCulture)));
                return;
            }
            else if (action == "presentation-reset")
            {
                // Client: drop what the presentation built (ResetPresentation); a rejoin or the next history rebuilds it.
                sLastPresentationReset = ResetPresentation();
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "reset=" + sLastPresentationReset));
                return;
            }
            else if (action == "watch")
            {
                // watch <ids comma-joined> <seconds>: sample those objects every simulator update (MotionWatch).
                ulong[] ids = command.Parts.Length > 3 ? SimScopeWire.Parse(command.Parts[3]) : null;
                int seconds = 10;
                if (command.Parts.Length > 4)
                {
                    int.TryParse(command.Parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
                }

                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", MotionWatch.Start(ids, seconds)));
                return;
            }
            else if (action == "watch-dump")
            {
                // watch-dump [offset] [maxChars]: the samples from offset; next = offset of the next page or -1. A page fits
                // one control slot (1500 chars, hex-encoded). A larger maxChars asks for one page through a bulk slot
                // (TrySendBulkReply): a whole watch in one round trip instead of ~100. No bulk slot free: the small page.
                int offset = 0;
                if (command.Parts.Length > 3)
                {
                    int.TryParse(command.Parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
                }

                int maxChars = 1500;
                if (command.Parts.Length > 4)
                {
                    int.TryParse(command.Parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxChars);
                    maxChars = Math.Max(1500, Math.Min(maxChars, BulkReplyMaxChars));
                }

                int next;
                List<string> samples = MotionWatch.Page(offset, maxChars, out next);
                string line = WatchDumpLine(command.CorrelationId, samples, next);
                if (maxChars > 1500 && !TrySendBulkReply(command.CorrelationId, line))
                {
                    samples = MotionWatch.Page(offset, 1500, out next);
                    line = WatchDumpLine(command.CorrelationId, samples, next);
                    SendLine(line);
                }
                else if (maxChars <= 1500)
                {
                    SendLine(line);
                }

                return;
            }
            else if (action == "peek")
            {
                // peek <since> [text]: the raw ring since that sequence, unfiltered, without moving any cursor (works while
                // push is on); with text, only the lines containing it (an object id: what was recorded about it).
                long since = 0L;
                if (command.Parts.Length > 3)
                {
                    long.TryParse(command.Parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out since);
                }

                string text = command.Parts.Length > 4 ? command.Parts[4] : null;
                long last;
                List<string> raw = SacsRecorder.RingSince(since, 1400, null, out last);
                List<string> shown = new List<string>();
                for (int i = 0; i < raw.Count; i++)
                {
                    if (text == null || raw[i].IndexOf(text, StringComparison.Ordinal) >= 0)
                    {
                        shown.Add(raw[i]);
                    }
                }

                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "seq=" + last.ToString(CultureInfo.InvariantCulture)
                    + ";more=" + (last < SacsRecorder.Sequence ? "1" : "0") + ";worldLines=" + SacsRecorder.WorldLines.ToString(CultureInfo.InvariantCulture)
                    + "\n" + string.Join("\n", shown.ToArray())));
                return;
            }
            else if (action == "tune")
            {
                // tune [name [value|default]]: live-tunable numbers (Tunables); no name lists them.
                string report = command.Parts.Length > 3
                    ? Tunables.Set(command.Parts[3], command.Parts.Length > 4 ? command.Parts[4] : null)
                    : Tunables.Report();
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, report.StartsWith("error=", StringComparison.Ordinal) ? "error" : "ok", report));
                return;
            }
            else if (action == "actor")
            {
                // actor <objectId>: the machines animating that object here, with its latest state on each
                // (SacsRecorder.ActorMachines).
                ulong actorId = 0uL;
                if (command.Parts.Length > 3)
                {
                    ulong.TryParse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out actorId);
                }

                List<string> rows = SacsRecorder.ActorMachines(actorId);
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "machines=" + rows.Count.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", rows.ToArray())));
                return;
            }
            else if (action == "param")
            {
                // param <driver> <name|hash x8>: one parameter of a driver here (SacsRecorder.ReadParam; item 4's
                // live check reads highLOD on each game's Idle driver, found with actor <objectId>).
                uint handle = 0u;
                if (command.Parts.Length > 3)
                {
                    uint.TryParse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out handle);
                }

                string report = SacsRecorder.ReadParam(handle, command.Parts.Length > 4 ? command.Parts[4] : string.Empty);
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, report.StartsWith("error=", StringComparison.Ordinal) ? "error" : "ok", report));
                return;
            }
            else if (action == "local-contexts")
            {
                NameAllMachines();
                List<string> rows = SacsRecorder.DescribeLocalStacks(1400);
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "stacks=" + rows.Count.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", rows.ToArray())));
                return;
            }
            else if (action == "replay-skips")
            {
                // Per skip site (source line) counts, then the newest skipped lines, within one bridge frame.
                CultureInfo c = CultureInfo.InvariantCulture;
                List<string> sites = new List<string>();
                foreach (KeyValuePair<int, long> site in sReplaySkipBySite)
                {
                    sites.Add(site.Key.ToString(c) + ":" + site.Value.ToString(c));
                }

                // receivedByKind: every line the replay was handed, by kind, before any filter (the server's sentByKind,
                // push-status, is the other end of the stream).
                string head = "skips=" + sReplaySkipCount.ToString(c) + ";sites=" + string.Join(",", sites.ToArray())
                    + ";receivedByKind=" + LineKindCounts.Format(sReplayReceivedByKind);
                // replay-skips <kind>: that kind's skips by site and its last skipped line at each site (SkipsByKind keeps
                // them after the ring turns over), then its newest rows still in the ring.
                string kind = command.Parts.Length > 3 && command.Parts[3].Length > 0 ? command.Parts[3] : null;
                if (kind != null)
                {
                    head += ";kind=" + kind + ";bySite=" + sReplaySkipsByKind.Sites(kind);
                }

                int budget = Math.Max(200, 1200 - head.Length);
                List<string> rows = new List<string>();
                int chars = 0;
                if (kind != null)
                {
                    foreach (string last in sReplaySkipsByKind.LastLines(kind))
                    {
                        if (chars < budget)
                        {
                            rows.Add(last);
                            chars += last.Length + 1;
                        }
                    }
                }

                int first = Math.Max(0, sReplaySkipCount - sReplaySkipLines.Length);
                for (int i = sReplaySkipCount - 1; i >= first && chars < budget; i--)
                {
                    string row = sReplaySkipLines[i % sReplaySkipLines.Length];
                    if (kind != null && (!SkipsByKind.RowOfKind(row, kind) || rows.Contains(row)))
                    {
                        continue;
                    }

                    rows.Add(row);
                    chars += row.Length + 1;
                }

                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", head + "\n" + string.Join("\n", rows.ToArray())));
                return;
            }
            else if (action == "picks")
            {
                // tier 3: SimLink's pick log from seq <since>, at most [max] records, within one bridge frame
                // (the reply is hex-encoded: about 1700 characters of text). scripts/sacs_picks.py pages through it.
                CultureInfo c = CultureInfo.InvariantCulture;
                int since = command.Parts.Length > 3 ? int.Parse(command.Parts[3], c) : 0;
                int max = command.Parts.Length > 4 ? int.Parse(command.Parts[4], c) : 64;
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SacsPicks.Page(since, max, 1700)));
                return;
            }
            else if (action == "orders")
            {
                // site A': SimLink's order log (one record per phase-0 execution) from seq <since>, at most
                // [max] records, within one bridge frame. scripts/sacs_picks.py pages through it.
                CultureInfo c = CultureInfo.InvariantCulture;
                int since = command.Parts.Length > 3 ? int.Parse(command.Parts[3], c) : 0;
                int max = command.Parts.Length > 4 ? int.Parse(command.Parts[4], c) : 64;
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SacsOrder.Page(since, max, 1700)));
                return;
            }
            else if (action == "randoms")
            {
                // item 3: SimLink's input log (one record per Random-node evaluate while sacs.random-log is on)
                // from seq <since>, at most [max] records, within one bridge frame. scripts/sacs_picks.py pages through it.
                CultureInfo c = CultureInfo.InvariantCulture;
                int since = command.Parts.Length > 3 ? int.Parse(command.Parts[3], c) : 0;
                int max = command.Parts.Length > 4 ? int.Parse(command.Parts[4], c) : 64;
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SacsRandom.Page(since, max, 1700)));
                return;
            }
            else if (action == "seed-coverage")
            {
                // tier 1 coverage (client): the mirrored drivers with replay lines since seed-reset that got no seed,
                // [rows] of them from the [skip]-th.
                CultureInfo c = CultureInfo.InvariantCulture;
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SacsSeedCopy.CoverageReport(
                    command.Parts.Length > 4 ? int.Parse(command.Parts[4], c) : 0, command.Parts.Length > 3 ? int.Parse(command.Parts[3], c) : 30)));
                return;
            }
            else if (action == "seed-sent")
            {
                // tier 1 coverage (server): what happened to the seeds of the given comma-separated driver handles.
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SacsSeedCopy.SentReport(command.Parts.Length > 3 ? command.Parts[3] : string.Empty)));
                return;
            }
            else if (action == "seed-mirrors")
            {
                // (client): every mapped driver with its spare seeds, [rows] of them from the [skip]-th.
                CultureInfo c = CultureInfo.InvariantCulture;
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SacsSeedCopy.MirrorsReport(
                    command.Parts.Length > 4 ? int.Parse(command.Parts[4], c) : 0, command.Parts.Length > 3 ? int.Parse(command.Parts[3], c) : 20)));
                return;
            }
            else if (action == "seed-reset")
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SacsSeedCopy.ResetCoverage()));
                return;
            }
            else if (action == "walker-status")
            {
                // option C: the walker copy's counters, SimLink's included (residual windows and what fell in them).
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SacsWalkerCopy.Describe()));
                return;
            }
            else if (action == "walker-outcomes")
            {
                // option C (client): the last AW| line's placements for the given comma-separated server drivers.
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", SacsWalkerCopy.OutcomeReport(command.Parts.Length > 3 ? command.Parts[3] : string.Empty)));
                return;
            }
            else if (action == "authority")
            {
                // authority: the fork's authority gate counters (AuthorityHook's public static int and long fields:
                // Mode, Checked, Blocked, AllowedOwned, ...), so a scenario can say what the client's own game tried to
                // run on Sims it does not own (scripts/replay_check.py counters). Stock gameplay: hook=absent.
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", AuthorityCounters()));
                return;
            }
            else if (action == "fingerprint-scope")
            {
                // fingerprint-scope <sim ids|->: the claimed Sims every later save's join fingerprint covers
                // (JoinFingerprint); the controller sends it before each save.
                ulong[] ids = command.Parts.Length > 3 ? SimScopeWire.Parse(command.Parts[3]) : new ulong[0];
                if (ids == null)
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "SACS_RECORDER fingerprint-scope: ids are decimal, comma-joined, or -"));
                    return;
                }

                sJoinFingerprintScope = ids;
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "fingerprint-scope=" + ids.Length.ToString(CultureInfo.InvariantCulture)));
                return;
            }
            else if (action == "fingerprint-saved")
            {
                // the fingerprint the last save that answered OK took, or "none".
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", sSavedJoinFingerprint ?? "none"));
                return;
            }
            else if (action == "fingerprint-now")
            {
                // fingerprint-now <sim ids|->: the join fingerprint of the world as it is now (a joiner's scan).
                ulong[] ids = command.Parts.Length > 3 ? SimScopeWire.Parse(command.Parts[3]) : new ulong[0];
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                    ids == null ? "error=bad sim ids" : TakeJoinFingerprint(ids)));
                return;
            }
            else if (action != "status")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SACS_RECORDER on|off|status|authority|ring-on|ring-off|dump [sinceSeq [targets]]|snapshot <targets>|snapshot-driver <handle> [offset]|picks <since> [max]|orders <since> [max]|seed-coverage [rows [skip]]|seed-mirrors [rows [skip]]|seed-sent <handles>|seed-reset|fingerprint-scope <sims>|fingerprint-saved|fingerprint-now <sims>"));
                return;
            }
            else
            {
                // The replay's own-driver counters too (the batch reply that carries them is not logged), and how many
                // controller sessions this game has had (sessions; warmResets: the re-attaches that dropped what the last
                // session presented, quick_loop rejoin:warm), for scenarios.py health; joinResets: the seq-0 resets of
                // late-join histories this game applied (its own joins only, never another player's:).
                CultureInfo c = CultureInfo.InvariantCulture;
                state = "status;replayDrivers=" + sReplayDrivers.Count.ToString(c) + ";borrowedNow=" + sReplayBorrowed.Count.ToString(c)
                    + ";acquiredOnSims=" + sReplayAcquiredOn.Count.ToString(c) + ";ownRepairs=" + sOwnMachineRepairs.ToString(c)
                    + ";idleRepairDupDropped=" + idleRepairDupDroppedCount.ToString(c)
                    + ";idleRepairStream=" + sIdleRepairStream.ToString(c) + ";idleRepairDefault=" + sIdleRepairDefault.ToString(c)
                    + ";handovers=" + sReplayHandovers.ToString(c) + ";sessions=" + sControllerSessions.ToString(c)
                    + ";warmResets=" + sWarmResets.ToString(c) + ";joinResets=" + sJoinResets.ToString(c)
                    + ";" + PlumbBobKeptStatus();
            }

            // The seam recorders' server counters (SACS_REPLAY is client-only): the batch B scenarios' rose:/assert:server checks.
            CultureInfo ci = CultureInfo.InvariantCulture;
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", state + ";" + SacsRecorder.Describe()
                + ";" + MethodSwap.Describe() + ";audioServer=pointLines:" + AudioRecorder.PointLines.ToString(ci)
                + ",pointsOwn:" + AudioRecorder.PointsOwn.ToString(ci) + ",pointsSent:" + SacsRecorder.PointSoundsSent.ToString(ci)
                + ",stings:" + AudioRecorder.Stings.ToString(ci) + ",stingsSent:" + sStingsSent.ToString(ci)
                + ",stingsUnowned:" + AudioRecorder.StingsUnowned.ToString(ci) + ";lookat=" + LookAtRecorder.Report()
                + ";worldServer=lines:" + WorldRecorder.Lines.ToString(ci)));
        }

        // presentation replay spike. SACS_REPLAY|corr|<hex lines> (client role): the server's
        // SacsRecorder ring lines ("seq|kind|..."), '\n'-joined and hex-encoded, replayed through the live
        // proxy. Server driver handles map to drivers acquired here; object ids map through the Sim alias
        // table (FindSim) or, for other objects, resolve as-is (furniture loaded from the save shares ids).
        // States are requested without yielding. Reply: applied / skipped (unmapped driver or object) counts.
        private static readonly Dictionary<uint, uint> sReplayDrivers = new Dictionary<uint, uint>();
        private static readonly Dictionary<uint, List<string>> sReplayPending = new Dictionary<uint, List<string>>();
        // stream gaps: a Sim on the client already drives some machines itself, through clients
        // restored from its save (its standing posture idle above all). Acquiring a second driver of the
        // same machine on the same host made the server's requests fail (kGenericFailure /
        // kProxyInvalid), so the client stayed in the previous machine. The replay borrows the Sim's own
        // driver instead and never releases it.
        private static readonly Dictionary<uint, bool> sReplayBorrowed = new Dictionary<uint, bool>();
        private static long sReplayBorrowCount;

        // The acquire line ("seq|A|...") of each mapped driver, to resolve it again when its handle goes stale.
        private static readonly Dictionary<uint, string> sReplayAcquireLines = new Dictionary<uint, string>();
        private static long sReplayReresolved;
        private static long sReplaySetupResent; // setup lines (sReplaySetup) re-queued by re-resolves
        // what each mapped driver was set up with (seeds, actors, parameters): a re-resolve acquires a new driver
        // from the acquire line alone, so these go with it, before the request that failed.
        private static readonly ReplaySetupCache sReplaySetup = new ReplaySetupCache();
        // B2: the latest state the stream gave each server driver (the entry RepairOwnMachines picks).
        private static readonly ReplayStateCache sReplayStates = new ReplayStateCache();
        private static long sReplayBridges; // requests replayed with the bridge they named (a posture machine handing the Sim on)
        // A request handing the Sim to a machine this client does not have yet (its machine still loading, so its lines are
        // pending; or later in a late-join history): replayed right after that machine's own request. By the server's
        // driver it bridges to. Bounded.
        private static readonly Dictionary<uint, List<string>> sBridgeParked = new Dictionary<uint, List<string>>();
        private static long sReplayBridgesParked;

        private static void ReleaseReplayDriver(ISacsProxy proxy, uint serverDriver, uint driver)
        {
            sReplayAcquireLines.Remove(serverDriver);
            sReplaySetup.Forget(serverDriver);
            sReplayStates.Forget(serverDriver);
            if (!sReplayBorrowed.Remove(serverDriver))
            {
                proxy.ReleaseDriver(driver);
                EventQueueRecorder.Detach(driver);
            }

            StateCatchUp.Forget(serverDriver);

            SacsRecorder.Authority.Unfollow(driver);
            sReplayDrivers.Remove(serverDriver);
        }

        private static uint FindOwnDriver(ObjectGuid host, ulong machine)
        {
            try
            {
                Sim sim = host.ObjectFromId<Sim>();
                if (sim == null)
                {
                    return 0u;
                }

                Posture posture = sim.Posture;
                for (int depth = 0; posture != null && depth < 6; depth++)
                {
                    uint handle = OwnDriverHandle(posture.CurrentStateMachine, machine);
                    if (handle != 0u)
                    {
                        return handle;
                    }

                    posture = posture.PreviousPosture;
                }

                // the standing posture's machine is the Sim's idle machine even when the posture chain does not
                // reach it: a seated puppet's chain is only PuppetPosture, with no machine (sim_act --own-machines). An
                // idle driver the server acquired while the copy sat then got a second machine here, and the copy kept
                // two live idle machines (its own parked in Standing, the replay's in standing_idle); the server has one.
                if (sim.Standing != null)
                {
                    return OwnDriverHandle(sim.Standing.CurrentStateMachine, machine);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return 0u;
        }

        // after a join's Z, create the posture's idle at A, before applying the stream's setup and states.
        // Acquiring a detached driver first let batch-end repair create a second idle; handover then discarded the
        // streamed driver's setup/state. Like the engine's own creation, enter standing before exposing the owner:
        // RequestState cannot initialize an actor walker that has neither a current nor a next native state.
        private static uint BootstrapStandingIdle(Sim sim)
        {
            uint own = FindOwnDriver(sim.ObjectId, ResourceUtils.HashString64("idle"));
            if (own != 0u)
            {
                return own;
            }

            Sim.StandingPosture standing = sim.Standing as Sim.StandingPosture;
            if (standing == null)
            {
                return 0u;
            }

            StateMachineClient sm = StateMachineClient.Acquire(sim.ObjectId, "idle", (AnimationPriority)(-2), true);
            if (sm == null)
            {
                return 0u;
            }

            sm.SetActor("x", sim);
            sm.EnterState("x", "standing");
            standing.CurrentStateMachine = sm;
            standing.SetDefaultIdleAnim();
            standing.SetIdleCallbacks();
            return ClientDriverHandle(sm);
        }

        private static uint OwnDriverHandle(StateMachineClient client, ulong machine)
        {
            if (client == null || client.Name == null || ResourceUtils.HashString64(client.Name) != machine)
            {
                return 0u;
            }

            // A machine whose driver died (a replayed StopAllAnimation) is no host for the server's lines:
            // borrowing it made every request fail kProxyInvalid, and re-resolving found it again.
            if (!client.IsValid)
            {
                return 0u;
            }

            return ClientDriverHandle(client);
        }

        // A StateMachineClient's driver handle, live or dead (0 when it has none): B2 reads a dead machine's to
        // find the server drivers the replay borrowed onto it.
        private static uint ClientDriverHandle(StateMachineClient client)
        {
            if (client == null)
            {
                return 0u;
            }

            AdoptClient(null); // binds the reflection fields
            if (sSmcDriver == null || sSmcDriverHandle == null)
            {
                return 0u;
            }

            object driver = sSmcDriver.GetValue(client);
            if (driver == null)
            {
                return 0u;
            }

            uint handle = (uint)sSmcDriverHandle.GetValue(driver);
            return handle < AcquireErrorFloor ? handle : 0u;
        }

        // stream gaps: AcquireDriver error codes (>= 0xFFFFFFF0) by code, and machines being loaded.
        private const uint AcquireErrorFloor = 0xFFFFFFF0u;
        private static readonly Dictionary<string, long> sReplayAcquireFailures = new Dictionary<string, long>();
        private static readonly Dictionary<ulong, bool> sWarmingMachines = new Dictionary<ulong, bool>();

        private sealed class MachineWarmer
        {
            private readonly ObjectGuid mHost;
            private readonly ResourceKey mKey;
            private readonly int mPriority;

            public MachineWarmer(ObjectGuid host, ResourceKey key, int priority)
            {
                mHost = host;
                mKey = key;
                mPriority = priority;
            }

            // Runs as a simulator task, where AcquireDriver may yield until the machine is loaded.
            public void Run()
            {
                try
                {
                    ISacsProxy proxy = SacsRecorder.Current;
                    if (proxy != null)
                    {
                        Sim sim = mHost.ObjectFromId<Sim>();
                        if (sim != null && StreamOwnsIdle(sim) && mKey.InstanceId == ResourceUtils.HashString64("idle"))
                        {
                            BootstrapStandingIdle(sim);
                            return;
                        }

                        uint handle = proxy.AcquireDriver(true, mHost, mKey, mPriority);
                        if (handle != 0u && handle < AcquireErrorFloor)
                        {
                            proxy.ReleaseDriver(handle);
                        }
                    }
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
                finally
                {
                    sWarmingMachines.Remove(mKey.InstanceId);
                }
            }
        }

        private static void WarmMachine(ObjectGuid host, ResourceKey key, int priority)
        {
            if (sWarmingMachines.ContainsKey(key.InstanceId))
            {
                return;
            }

            sWarmingMachines[key.InstanceId] = true;
            Simulator.AddObject(new OneShotFunctionTask(new Function(new MachineWarmer(host, key, priority).Run)));
        }

        private static string DescribeCounts(Dictionary<string, long> counts)
        {
            if (counts.Count == 0)
            {
                return "-";
            }

            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, long> pair in counts)
            {
                parts.Add(pair.Key + ":" + pair.Value.ToString(CultureInfo.InvariantCulture));
            }

            return string.Join(",", parts.ToArray());
        }

        // stream gaps: the last failed state requests ("code|client=handle|line"), for
        // SACS_RECORDER replay-failures on a client.
        private static readonly string[] sReplayFailureLines = new string[32];
        private static int sReplayFailureCount;

        // Diagnostics (SACS_RECORDER replay-skips): the latest skipped replay lines with the source line of the skip,
        // and a count per skip site.
        private static readonly string[] sReplaySkipLines = new string[64];
        private static int sReplaySkipCount;
        private static readonly Dictionary<int, long> sReplaySkipBySite = new Dictionary<int, long>();
        private static readonly Dictionary<string, long> sReplayReceivedByKind = new Dictionary<string, long>();
        private static readonly SkipsByKind sReplaySkipsByKind = new SkipsByKind();
        // Skip sites that are not a replay decision: lines a presentation reset dropped while they waited (a driver's
        // pending lines, a client walk's held lines), and a walk's held lines past RouteHeldLinesCap.
        private const int SkipSiteResetDropped = 7960;
        private const int SkipSiteRouteHoldFull = 7961;

        private static void RecordReplaySkip(int site, string line)
        {
            sReplaySkipLines[sReplaySkipCount % sReplaySkipLines.Length] = site.ToString(CultureInfo.InvariantCulture) + "|" + line;
            sReplaySkipCount++;
            long seen;
            sReplaySkipBySite.TryGetValue(site, out seen);
            sReplaySkipBySite[site] = seen + 1;
            sReplaySkipsByKind.Add(site, line);
        }

        private static void RecordReplayFailure(string text)
        {
            sReplayFailureLines[sReplayFailureCount % sReplayFailureLines.Length] = text;
            sReplayFailureCount++;
        }

        // stream gaps: RequestState results other than kSuccess, by code.
        private static readonly Dictionary<string, long> sReplayResults = new Dictionary<string, long>();
        private const int ReplayPendingCap = 256;
        private const int ReplayPendingDrivers = 512;
        private static long sReplayApplied;
        private static long sReplaySkipped;
        private static long sReplayErrors;
        private static string sReplayLastError = "-";

        private static ObjectGuid ReplayObject(ulong serverObjectId)
        {
            if (serverObjectId == 0uL)
            {
                return ObjectGuid.InvalidObjectGuid;
            }

            Sim sim = FindSim(serverObjectId);
            if (sim != null)
            {
                return sim.ObjectId;
            }

            ObjectGuid aliased;
            if (sObjectAliases.TryGetValue(serverObjectId, out aliased))
            {
                return aliased;
            }

            GameObject obj = new ObjectGuid(serverObjectId).ObjectFromId<GameObject>();
            return obj != null ? obj.ObjectId : ObjectGuid.InvalidObjectGuid;
        }

        // stream gaps: adopt the drivers of target Sims that were restored from the save rather
        // than acquired (SacsRecorder.Adopt). Their StateMachineClients hang off the Sim's posture chain,
        // its queued interactions and its facial idle. At most once a second unless forced (snapshot).
        private static int sLastAdoptTick = -100;
        private static FieldInfo sSmcDriver;
        private static FieldInfo sSmcDriverHandle;
        private static FieldInfo sSmcScript;
        private static FieldInfo sSmcActorNames;

        private static void AdoptTargetDrivers(Dictionary<ulong, bool> targets, bool force)
        {
            if (targets == null || targets.Count == 0 || (!force && sBridgeProbeTicks - sLastAdoptTick < 4))
            {
                return;
            }

            sLastAdoptTick = sBridgeProbeTicks;
            try
            {
                foreach (ulong id in targets.Keys)
                {
                    Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                    if (sim == null)
                    {
                        continue;
                    }

                    Posture posture = sim.Posture;
                    for (int depth = 0; posture != null && depth < 6; depth++)
                    {
                        AdoptClient(posture.CurrentStateMachine);
                        posture = posture.PreviousPosture;
                    }

                    if (sim.InteractionQueue != null)
                    {
                        foreach (InteractionInstance interaction in sim.InteractionQueue.InteractionList)
                        {
                            if (interaction != null)
                            {
                                AdoptClient(interaction.mCurrentStateMachine);
                            }
                        }
                    }

                    if (sim.IdleManager != null)
                    {
                        AdoptClient(sim.IdleManager.FacialIdleStateMachineClient);
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // name the machines the Sims in this world hold, with where they hang (posture, an
        // interaction, the facial idle), for SACS_RECORDER local.
        private static void NameAllMachines()
        {
            try
            {
                foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    Posture posture = sim.Posture;
                    for (int depth = 0; posture != null && depth < 6; depth++)
                    {
                        NameMachine(posture.CurrentStateMachine, "posture:" + posture.GetType().Name);
                        posture = posture.PreviousPosture;
                    }

                    if (sim.InteractionQueue != null)
                    {
                        foreach (InteractionInstance interaction in sim.InteractionQueue.InteractionList)
                        {
                            if (interaction != null)
                            {
                                NameMachine(interaction.mCurrentStateMachine, "interaction:" + interaction.GetType().Name);
                            }
                        }
                    }

                    if (sim.IdleManager != null)
                    {
                        NameMachine(sim.IdleManager.FacialIdleStateMachineClient, "facial");
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static void NameMachine(StateMachineClient client, string holder)
        {
            if (client != null && client.Name != null)
            {
                SacsRecorder.NameMachine(ResourceUtils.HashString64(client.Name), holder + ":" + client.Name);
            }
        }

        private static void AdoptClient(StateMachineClient client)
        {
            try
            {
                if (sSmcDriver == null)
                {
                    BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    sSmcDriver = typeof(StateMachineClient).GetField("mDriver", flags);
                    sSmcScript = typeof(StateMachineClient).GetField("mScriptHandle", flags);
                    sSmcActorNames = typeof(StateMachineClient).GetField("mActorNames", flags);
                    sSmcDriverHandle = sSmcDriver == null ? null : sSmcDriver.FieldType.GetField("mHandle", flags);
                }

                if (client == null || sSmcDriver == null || sSmcDriverHandle == null || sSmcScript == null)
                {
                    return;
                }

                uint handle = (uint)sSmcDriverHandle.GetValue(sSmcDriver.GetValue(client));
                if (handle == 0u || SacsRecorder.IsKnown(handle))
                {
                    return;
                }

                Dictionary<uint, ulong> actors = new Dictionary<uint, ulong>();
                Dictionary<string, ObjectGuid> names = sSmcActorNames == null ? null : sSmcActorNames.GetValue(client) as Dictionary<string, ObjectGuid>;
                if (names != null)
                {
                    foreach (KeyValuePair<string, ObjectGuid> actor in names)
                    {
                        actors[ResourceUtils.HashString32(actor.Key)] = actor.Value.Value;
                    }
                }

                ObjectGuid host = (ObjectGuid)sSmcScript.GetValue(client);
                SacsRecorder.Adopt(handle, host.Value, client.Name, actors);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // the authority gate lets cosmetic inserts (Sim+CustomIdle) run on puppets so they would
        // not look stiff. With the presentation stream the server's idles arrive as animation, and a local
        // custom idle (the standing idle machine's PlayCustomIdle handler, fired by replayed states) only
        // plays a different idle with its own props: a phone in hand while the server's Sim reads
        // (cold-test-035). Once the stream replays, nothing is cosmetic.
        private static bool sCosmeticCleared;

        private static void ClearCosmeticAllowanceOnce()
        {
            if (sCosmeticCleared)
            {
                return;
            }

            sCosmeticCleared = true;
            try
            {
                Type hook = AuthorityHook();
                MethodInfo set = hook == null ? null : hook.GetMethod("SetCosmeticPrefixes");
                if (set != null)
                {
                    set.Invoke(null, new object[] { new string[0] });
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // item 4: replay one prop line on this client's objects (Sims through the alias table,
        // other objects by the shared save id). False when an object is not here (a prop the server
        // spawned at runtime: object identity,).
        private static long sReplayPropsUnresolved;

        private static bool ReplayProp(string[] f, string kind)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            ObjectGuid obj = ReplayObject(ulong.Parse(f[2], c));
            if (!obj.IsValid)
            {
                return false;
            }

            if (kind == "U")
            {
                Slots.DetachFromSlot(obj);
                return true;
            }

            if (kind == "H")
            {
                World.ObjectSetHiddenFlags(obj, uint.Parse(f[3], c));
                return true;
            }

            ObjectGuid parent = ReplayObject(ulong.Parse(f[3], c));
            if (!parent.IsValid)
            {
                return false;
            }

            uint slot = uint.Parse(f[4], c);
            bool occupy = f[5] == "1";
            bool force = f[6] == "1";
            if (kind == "T")
            {
                return (int)Slots.AttachToSlot(obj, parent, slot, occupy, force) == 0;
            }

            Vector3 trans = new Vector3(float.Parse(f[7], c), float.Parse(f[8], c), float.Parse(f[9], c));
            Vector3 axis = new Vector3(float.Parse(f[10], c), float.Parse(f[11], c), float.Parse(f[12], c));
            float angle = float.Parse(f[13], c);
            if (kind == "Tx")
            {
                return (int)Slots.AttachToSlot(obj, parent, slot, occupy, force, ref trans, ref axis, angle) == 0;
            }

            // AttachToBone has no static wrapper on Slots: go through the live implementation.
            ISlots live = CurrentSlots();
            return live != null && (int)live.AttachToBone(obj, parent, slot, occupy, force, ref trans, ref axis, angle) == 0;
        }

        private static ISlots CurrentSlots()
        {
            Type slots = typeof(ISlots).Assembly.GetType("Sims3.SimIFace.Slots");
            FieldInfo field = slots == null ? null : slots.GetField("gSlots", BindingFlags.NonPublic | BindingFlags.Static);
            return field == null ? null : field.GetValue(null) as ISlots;
        }

        // item 4, server: putting a prop away (into an inventory, back on a shelf) hides it through
        // World.ObjectSetHiddenFlags rather than a slot call. Every second, read the hidden flags of the
        // props streamed Sims held and write an H line when they change.
        private static readonly Dictionary<ulong, uint> sPropHidden = new Dictionary<ulong, uint>();
        private static int sLastPropPollTick = -100;

        private static void PollPropVisibility(Dictionary<ulong, bool> targets)
        {
            if (sBridgeProbeTicks - sLastPropPollTick < 4)
            {
                return;
            }

            sLastPropPollTick = sBridgeProbeTicks;
            bool resendHands = ++sHandResendCounter >= 10;
            if (resendHands)
            {
                sHandResendCounter = 0;
            }

            // What streamed Sims hold now is a prop too, even when it got there without a slot call (a phone
            // out of the inventory: unhidden, then parented by the carry animation; cold-test-039).
            if (targets != null)
            {
                foreach (ulong id in targets.Keys)
                {
                    try
                    {
                        Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                        if (sim == null)
                        {
                            continue;
                        }

                        AddHeldProp(sim.GetObjectInRightHand());
                        AddHeldProp(sim.GetObjectInLeftHand());
                        PublishHands(sim, resendHands);
                        PublishOutfit(sim, resendHands);
                        PublishQueue(sim, resendHands);
                        PublishBuffs(sim, resendHands);
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }
                }
            }

            if (targets != null)
            {
                PublishRelationships(targets, resendHands);
                PublishConversations(targets, resendHands);
                PublishInventories(targets, resendHands);
                PublishUses(targets, resendHands);
            }

            RetestRefusals(resendHands);

            VfxRecorder.ForgetCollected();

            ForgetVanishedObjects();
            if (SacsRecorder.Props.Count == 0)
            {
                return;
            }

            List<ulong> gone = new List<ulong>();
            List<ulong> found = new List<ulong>();
            foreach (ulong id in SacsRecorder.Props.Keys)
            {
                try
                {
                    ObjectGuid propId = new ObjectGuid(id);
                    if (propId.ObjectFromId<GameObject>() == null && !Sims3.SimIFace.Objects.IsValid(propId))
                    {
                        gone.Add(id);
                        continue;
                    }

                    uint flags = World.ObjectGetHiddenFlags(propId);
                    uint last;
                    // On change, and every 10 seconds for a client that joined since.
                    if (!sPropHidden.TryGetValue(id, out last) || last != flags || resendHands)
                    {
                        sPropHidden[id] = flags;
                        SacsRecorder.RingLine("H|" + id.ToString(CultureInfo.InvariantCulture) + "|" + flags.ToString(CultureInfo.InvariantCulture));
                    }

                    PublishPlacement(propId, resendHands, found);
                }
                catch (Exception)
                {
                    gone.Add(id);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                SacsRecorder.Props.Remove(gone[i]);
                sPropHidden.Remove(gone[i]);
                sPropPlacements.Remove(gone[i]);
            }

            for (int i = 0; i < found.Count && SacsRecorder.Props.Count < 256; i++)
            {
                SacsRecorder.Props[found[i]] = true;
            }
        }

        // Prop placement (2026-10-14). Slot lines (T) only stream when the object or its new parent is already of
        // interest, and they are not held while a client route walks the Sim, so a copy could end in the wrong place: a
        // plate stayed in the copy's hand at the table, flickering as the hand check hid it, while the server's sat on
        // the table (cold-test-095). Server, every prop poll: where each prop is attached, on change and every 10 s:
        //   Pl|obj|parent|slot|bone   (parent 0 = not attached)
        // Objects in a prop's own containment slots (the food on a plate) become props too. Attachments to a Sim are the
        // hand check's (Hd).
        private static readonly Dictionary<ulong, string> sPropPlacements = new Dictionary<ulong, string>();

        private static void PublishPlacement(ObjectGuid propId, bool resend, List<ulong> found)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            ulong id = propId.Value;
            if (propId.ObjectFromId<Sim>() != null)
            {
                return;
            }

            ObjectGuid parent = Slots.GetParentId(propId);
            string state = "0|0|0";
            if (parent.IsValid && parent.ObjectFromId<Sim>() == null)
            {
                bool bone = false;
                uint slot = (uint)Slots.GetParentSlot(propId, ref bone);
                state = parent.Value.ToString(c) + "|" + slot.ToString(c) + "|" + (bone ? "1" : "0");
            }
            else if (parent.IsValid)
            {
                state = "sim";
            }

            string last;
            if (state != "sim" && (resend || !sPropPlacements.TryGetValue(id, out last) || last != state))
            {
                sPropPlacements[id] = state;
                SacsRecorder.RingLine("Pl|" + id.ToString(c) + "|" + state);
            }

            try
            {
                Slot[] slots = Slots.GetContainmentSlotNames(propId);
                for (int i = 0; slots != null && i < slots.Length && found.Count < 32; i++)
                {
                    ObjectGuid child = Slots.GetContainedObject(propId, (uint)slots[i]);
                    if (child.IsValid && !SacsRecorder.Props.ContainsKey(child.Value) && child.ObjectFromId<Sim>() == null && !found.Contains(child.Value))
                    {
                        found.Add(child.Value);
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // Client side of Pl: attach the copy where the server's is (not to a Sim: hands are Hd's). The copy's attachment
        // is kept by object for the hand check, which puts an object it takes out of a hand there instead of hiding it.
        private static readonly Dictionary<ulong, string> sClientPlacements = new Dictionary<ulong, string>();
        private static long sPlacementsApplied;
        private static long sPlacementsFailed;

        private static bool ReplayPlacement(string[] f)
        {
            if (f.Length < 6)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ObjectGuid obj = ReplayObject(ulong.Parse(f[2], c));
            if (!obj.IsValid)
            {
                return false;
            }

            ulong serverParent = ulong.Parse(f[3], c);
            if (serverParent == 0uL)
            {
                sClientPlacements.Remove(obj.Value);
                return true; // not attached on the server: where it is comes from the stream's own lines
            }

            ObjectGuid parent = ReplayObject(serverParent);
            if (!parent.IsValid || f[5] == "1")
            {
                return false; // a bone attachment comes with its offset in the stream's B line
            }

            uint slot = uint.Parse(f[4], c);
            string placement = parent.Value.ToString(c) + "|" + slot.ToString(c);
            string previous;
            if (!sClientPlacements.TryGetValue(obj.Value, out previous) || previous != placement)
            {
                sClientPlacements[obj.Value] = placement;
                sPlacementSince[obj.Value] = DateTime.UtcNow;
            }

            return true;
        }

        // Every replay batch: a copy still not where the server's prop is PlacementSettle after the server reported it
        // goes there. Applied at once, it fought the replayed carry animation mid-transition (a bowl jumped about while
        // Jennifer poured into it).
        private static readonly Dictionary<ulong, DateTime> sPlacementSince = new Dictionary<ulong, DateTime>();
        // STOPGAP(boundary-recording): settle-placements drift correction (2 s); the slot boundary should leave it nothing to correct.
        private static readonly TimeSpan PlacementSettle = TimeSpan.FromSeconds(2);

        private static void SettlePlacements()
        {
            if (sPlacementSince.Count == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            List<ulong> due = new List<ulong>();
            foreach (KeyValuePair<ulong, DateTime> pair in sPlacementSince)
            {
                if (now - pair.Value >= PlacementSettle)
                {
                    due.Add(pair.Key);
                }
            }

            for (int i = 0; i < due.Count; i++)
            {
                sPlacementSince.Remove(due[i]);
                string placed;
                if (sClientPlacements.TryGetValue(due[i], out placed))
                {
                    string[] p = placed.Split('|');
                    ApplyPlacement(new ObjectGuid(due[i]), new ObjectGuid(ulong.Parse(p[0], CultureInfo.InvariantCulture)), uint.Parse(p[1], CultureInfo.InvariantCulture));
                }
            }
        }

        private static bool ApplyPlacement(ObjectGuid obj, ObjectGuid parent, uint slot)
        {
            try
            {
                bool bone = false;
                ObjectGuid current = Slots.GetParentId(obj);
                if (current.IsValid && current.Value == parent.Value && (uint)Slots.GetParentSlot(obj, ref bone) == slot)
                {
                    return true;
                }

                if (current.IsValid && current.ObjectFromId<Sim>() != null)
                {
                    return true; // in a hand on this client: the hand check (Hd) takes it out first
                }

                Slots.DetachFromSlot(obj);
                bool ok = (int)Slots.AttachToSlot(obj, parent, slot, false, false) == 0;
                if (ok)
                {
                    sPlacementsApplied++;
                }
                else
                {
                    sPlacementsFailed++;
                }

                return ok;
            }
            catch (Exception)
            {
                sPlacementsFailed++;
                return false;
            }
        }

        // Teleport fix (cold-test-077): a streamed Sim's position goes in the stream, at its cadence (every recorder
        // poll, ~250 ms), when it moved more than PositionStep and every 10 seconds:
        //   Ps|sim|x|y|z
        // The snapshot position mirror was the only way a walking Sim moved on clients, and its snapshots arrived
        // every ~9 s on average and could be seconds old when applied: Sims jumped metres, and back to where the
        // server's Sim had been (into a bed it had already left). In the stream the position stays in order with
        // the animation lines.
        private static readonly Dictionary<ulong, Vector3> sStreamPositions = new Dictionary<ulong, Vector3>();

        // Seated pose fix (cold-test-078/079): a streamed Sim's posture, on change and every 10 seconds:
        //   Po|sim|PostureTypeName|container|sacsPosture|censor|containmentSlot|machineDriver|machineName
        // container = the posture's container object (0 = none, or the Sim itself), sacsPosture = its SACS posture
        // parameter (ScriptPosture), censor = Sim.CensorType (the toilet's blur box), containmentSlot = the seat's
        // containment slot for a seat posture (0 = none), machineDriver / machineName = the posture's state machine:
        // its driver handle as the recorder's lines name it (0 = none) and its name ("-"). A client's puppet posture
        // has no state machine of its own; its probe reports the replayed copy of this driver (ReplayedPostureMachine).
        // Then the HUD's fields (displayName|implicit|socializing|partner) and socials: the posture's answer for socials
        // (bit 1 AllowsNormalSocials, bit 2 ISeatedSocialPosture; -1 = unknown), which a client's puppet posture gives
        // back so its pie menu offers what the server's does for a seated target (SeatedSocialInteractionA,).
        // Older clients read the first seven fields. Presenter mode gives the client's copy that posture
        // (PresenterHook, which settles a seated copy onto that slot); the censor is applied straight through
        // Sim.EnableCensor.
        // Positions are Pz lines: "Ps" is the recorder's string-parameter line (Ps|driver|param|value), and
        // cold-tests 078-086 parsed those as positions and dropped them, so no string parameter reached a client
        // (the chair sit's sitTemplateSuffix among them: copies stood at chairs).
        // When the server's Sim sits down or lies down, its game stops the standing animation (a streamed Z). The client
        // used to restart its copy's standing idle right after (RepairOwnMachines), and that idle played over the
        // replayed seated animation: the copy stood at the chair while the replay was in the server's sit state.
        private static readonly Dictionary<ulong, string> sStreamPostures = new Dictionary<ulong, string>();
        private static int sPositionResendCounter;
        private const float PositionStep = 0.05f;
        // Facing (2026-10-14): Pz|sim|x|y|z|fx|fy|fz, also when the Sim turned more than about 8 degrees. Copies used to
        // keep whatever way they last walked: partners in a conversation and a Sim at the sink faced the wrong way.
        private static readonly Dictionary<ulong, Vector3> sStreamForwards = new Dictionary<ulong, Vector3>();
        private const float ForwardStepCos = 0.99f;

        // Cosine of the angle between two facings, on the floor plane (1 = the same way).
        private static float FacingCos(Vector3 a, Vector3 b)
        {
            float la = (float)Math.Sqrt(a.x * a.x + a.z * a.z);
            float lb = (float)Math.Sqrt(b.x * b.x + b.z * b.z);
            if (la < 0.0001f || lb < 0.0001f)
            {
                return 1f;
            }

            return (a.x * b.x + a.z * b.z) / (la * lb);
        }

        private static void PublishPositions(Dictionary<ulong, bool> targets)
        {
            if (targets == null)
            {
                return;
            }

            bool resend = ++sPositionResendCounter >= 40;
            if (resend)
            {
                sPositionResendCounter = 0;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            foreach (ulong id in targets.Keys)
            {
                try
                {
                    Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                    if (sim == null)
                    {
                        continue;
                    }

                    string posture = sim.Posture == null ? "-" : sim.Posture.GetType().Name;
                    ulong container = 0uL;
                    int sacsPosture = 0;
                    if (sim.Posture != null)
                    {
                        IGameObject holder = sim.Posture.Container;
                        if (holder != null && holder.ObjectId != sim.ObjectId)
                        {
                            container = holder.ObjectId.Value;
                        }

                        try
                        {
                            sacsPosture = (int)sim.Posture.GetSacsPostureParameter();
                        }
                        catch (Exception)
                        {
                            SwallowedErrors.Note();
                        }
                    }

                    int censor = 0;
                    try
                    {
                        censor = (int)sim.GetCensorType;
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }

                    uint containmentSlot = 0u;
                    try
                    {
                        ISeatPosture seatPosture = sim.Posture as ISeatPosture;
                        if (seatPosture != null && seatPosture.Seat != null)
                        {
                            containmentSlot = (uint)(int)seatPosture.Seat.ContainmentSlot;
                        }
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }

                    uint machineDriver = 0u;
                    string machineName = "-";
                    try
                    {
                        StateMachineClient machine = sim.Posture == null ? null : sim.Posture.CurrentStateMachine;
                        if (machine != null)
                        {
                            machineDriver = DriverHandleOf(machine);
                            machineName = PresentationField(machine.Name);
                        }
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }

                    // The HUD's posture item: the posture's own name ("Sitting") and whether it shows one at all (Implicit).
                    string displayName = string.Empty;
                    bool implicitPosture = true;
                    try
                    {
                        if (sim.Posture != null)
                        {
                            displayName = sim.Posture.Name ?? string.Empty;
                            implicitPosture = sim.Posture.Implicit;
                        }
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }

                    // The posture's answer for socials: a client's menu for this Sim is built from its copy's posture.
                    int socials = -1;
                    try
                    {
                        if (sim.Posture != null)
                        {
                            socials = (sim.Posture.AllowsNormalSocials() ? 1 : 0) | (sim.Posture is ISeatedSocialPosture ? 2 : 0);
                        }
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }

                    // The HUD's conversation item: the posture counts as socializing (HudModel.IsPostureSocializing) and the
                    // Sim's partner (TalkedToSimDesc), 0 when not in a conversation.
                    bool postureSocializing = false;
                    ulong partner = 0uL;
                    try
                    {
                        InteractionInstance current = sim.CurrentInteraction ?? (sim.InteractionQueue == null ? null : sim.InteractionQueue.GetNextInteraction());
                        postureSocializing = sim.Posture is Sim.SocializingPosture || current is SeatedSocialInteractionA || current is Sim.GroupChatB;
                        if (sim.SocialComponent != null && sim.IsSocializing)
                        {
                            Sim talkingTo = sim.SocialComponent.LastTalkingTo;
                            if (talkingTo != null && !talkingTo.HasBeenDestroyed)
                            {
                                partner = talkingTo.ObjectId.Value;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }

                    posture = posture + "|" + container.ToString(c) + "|" + sacsPosture.ToString(c) + "|" + censor.ToString(c) + "|" + containmentSlot.ToString(c)
                        + "|" + machineDriver.ToString(c) + "|" + machineName + "|" + BridgeCodec.EncodeText(displayName) + "|" + (implicitPosture ? "1" : "0")
                        + "|" + (postureSocializing ? "1" : "0") + "|" + partner.ToString(c) + "|" + socials.ToString(c);
                    string lastPosture;
                    if (resend || !sStreamPostures.TryGetValue(id, out lastPosture) || lastPosture != posture)
                    {
                        sStreamPostures[id] = posture;
                        SacsRecorder.RingLine("Po|" + id.ToString(c) + "|" + posture);
                    }

                    Vector3 position = sim.Position;
                    Vector3 forward = sim.ForwardVector;
                    Vector3 last;
                    Vector3 lastForward;
                    if (!resend && sStreamPositions.TryGetValue(id, out last) && (position - last).Length() < PositionStep
                        && sStreamForwards.TryGetValue(id, out lastForward) && FacingCos(forward, lastForward) >= ForwardStepCos)
                    {
                        continue;
                    }

                    sStreamPositions[id] = position;
                    sStreamForwards[id] = forward;
                    SacsRecorder.RingLine("Pz|" + id.ToString(c) + "|" + position.x.ToString("R", c) + "|" + position.y.ToString("R", c) + "|" + position.z.ToString("R", c)
                        + "|" + forward.x.ToString("R", c) + "|" + forward.y.ToString("R", c) + "|" + forward.z.ToString("R", c));
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
            }
        }

        // While the presentation stream describes a Sim, the snapshot position mirror (APPLY_POSITIONS) leaves it alone
        // (IsRouteHeld): the stream alone places it, through the mirror's rules (at rest, not in a posture). Described =
        // a stream position (Pz, sent when the server's Sim moves) or its posture line (Po, repeated every 10 s for every
        // streamed Sim) within StreamPositionFresh. Pz alone lapsed for a Sim lying still, and the snapshot mirror moved
        // the copy in bed 0.78 m (sleep-bed, 2026-10-15). A Sim that leaves the stream goes back to the snapshot mirror.
        private static readonly Dictionary<ulong, DateTime> sStreamPositioned = new Dictionary<ulong, DateTime>();
        private static readonly TimeSpan StreamPositionFresh = TimeSpan.FromSeconds(15);
        private static long sStreamMirrorSkips;

        internal static void MarkStreamPositioned(ulong simObjectId)
        {
            sStreamPositioned[simObjectId] = DateTime.UtcNow;
        }

        // Client side of Po. While the server's Sim isn't standing, its copy's own standing idle stays stopped (the
        // replayed posture animation shows); when the server's Sim stands, the idle comes back, and a copy left in another
        // posture by a loaded save (seated when the server's Sim had moved on) is stood up.
        private static readonly Dictionary<ulong, string> sServerPostures = new Dictionary<ulong, string>();
        private static readonly Dictionary<ulong, bool> sServerPostureOnObject = new Dictionary<ulong, bool>();
        // The seat's containment slot from the last Po (0 when not seated on one). The fork's seat settle owns a copy on a
        // seat; the position mirror's join settle leaves it to it.
        private static readonly Dictionary<ulong, uint> sServerSeatSlot = new Dictionary<ulong, uint>();
        // The server's Sim's conversation for the HUD: "postureSocializing:partner" (this client's partner id, 0 = none).
        private static readonly Dictionary<ulong, string> sServerSocial = new Dictionary<ulong, string>();
        private static readonly Dictionary<ulong, DateTime> sPostureChangedUtc = new Dictionary<ulong, DateTime>();

        // The server's Sim is in a posture on an object (Po container), not standing or in a conversation.
        internal static bool ServerSimInContainerPosture(ulong simObjectId)
        {
            bool onObject;
            return sServerPostureOnObject.TryGetValue(simObjectId, out onObject) && onObject && !ServerSimIsStanding(simObjectId);
        }

        // The server's Sim sits on a seat with a containment slot (Po's containmentSlot), not standing.
        internal static bool ServerSimOnSeat(ulong simObjectId)
        {
            uint slot;
            return sServerSeatSlot.TryGetValue(simObjectId, out slot) && slot != 0u && ServerSimInContainerPosture(simObjectId);
        }
        // STOPGAP(boundary-recording): posture-enter-clip a 3 s guess at the enter clip's length; wait for the copy's machine to reach the server's state instead.
        private static TimeSpan PostureEnterClip { get { return Tunables.Seconds("posture.enter-clip", 3.0); } }

        internal static bool RecentlyPostured(ulong simObjectId)
        {
            DateTime at;
            return sPostureChangedUtc.TryGetValue(simObjectId, out at) && DateTime.UtcNow - at < PostureEnterClip;
        }
        private static long sPostureStandUps;

        private static bool ReplayStreamPosture(string[] f)
        {
            if (f.Length < 4)
            {
                return false;
            }

            ObjectGuid simId = ReplayObject(ulong.Parse(f[2], CultureInfo.InvariantCulture));
            if (!simId.IsValid)
            {
                return false;
            }

            string posture = f[3];
            string previous;
            if (!sServerPostures.TryGetValue(simId.Value, out previous) || previous != posture)
            {
                sPostureChangedUtc[simId.Value] = DateTime.UtcNow;
            }

            sServerPostures[simId.Value] = posture;
            sServerPostureOnObject[simId.Value] = f.Length > 4 && f[4] != "0";
            uint seatSlot = 0u;
            if (f.Length > 7)
            {
                uint.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out seatSlot);
            }

            sServerSeatSlot[simId.Value] = seatSlot;
            if (f.Length > 13)
            {
                ulong partner = ulong.Parse(f[13], CultureInfo.InvariantCulture);
                ObjectGuid local = partner == 0uL ? ObjectGuid.InvalidObjectGuid : ReplayObject(partner);
                string social = (f[12] == "1" ? "1" : "0") + ":" + (local.IsValid ? local.Value : 0uL).ToString(CultureInfo.InvariantCulture);
                string lastSocial;
                if (!sServerSocial.TryGetValue(simId.Value, out lastSocial) || lastSocial != social)
                {
                    sServerSocial[simId.Value] = social;
                    if (simId.Value == sDisplayedQueueSim)
                    {
                        List<Sims3.UI.Hud.IInteractionInstance> shown;
                        if (sMirroredQueues.TryGetValue(simId.Value, out shown))
                        {
                            ShowMirroredQueue(simId.Value, shown);
                        }
                    }
                }
            }
            if (f.Length > 6)
            {
                ApplyCensor(simId, f[6]);
            }

            if (f.Length > 9)
            {
                uint machineDriver;
                uint.TryParse(f[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out machineDriver);
                sPostureMachineDrivers[simId.Value] = machineDriver;
                sPostureMachineNames[simId.Value] = f[9];
            }

            if (PresenterOn())
            {
                ulong container = 0uL;
                int sacsPosture = 0;
                if (f.Length > 5 && f[4] != "0")
                {
                    ObjectGuid holder = ReplayObject(ulong.Parse(f[4], CultureInfo.InvariantCulture));
                    if (holder.IsValid)
                    {
                        container = holder.Value;
                    }

                    int.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out sacsPosture);
                }

                uint containmentSlot = seatSlot;

                string displayName = null;
                bool implicitPosture = false;
                if (f.Length > 11)
                {
                    displayName = BridgeCodec.DecodeText(f[10]);
                    implicitPosture = f[11] == "1";
                }

                int socials;
                if (f.Length <= 14 || !int.TryParse(f[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out socials))
                {
                    socials = -1;
                }

                string presenterContext = SacsRecorder.LocalContext;
                SacsRecorder.LocalContext = "presenter posture (Po)";
                try
                {
                    PresenterApply(simId.Value, posture, container, sacsPosture, containmentSlot, displayName, implicitPosture, socials);
                }
                finally
                {
                    SacsRecorder.LocalContext = presenterContext;
                }
            }

            if (posture != "StandingPosture")
            {
                return true;
            }

            Sim sim = simId.ObjectFromId<Sim>();
            if (sim == null)
            {
                return false;
            }

            try
            {
                if (sim.Posture != null && !(sim.Posture is Sim.StandingPosture) && sim.Standing != null)
                {
                    SacsRecorder.LocalContext = "stand-up";
                    sim.Posture = sim.Standing;
                    sPostureStandUps++;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
            finally
            {
                SacsRecorder.LocalContext = null;
            }

            SacsRecorder.LocalContext = RepairLabel.Posture;
            try
            {
                RepairOwnMachines(simId);
            }
            finally
            {
                SacsRecorder.LocalContext = null;
            }

            return true;
        }

        // Presenter mode (internal documentation): on a client, every Sim it does not own is a puppet of the
        // server's (SimsOnline.Hooks.PresenterHook in the gameplay fork): its autonomy searches nothing and it takes
        // the server's posture (Po) as a PuppetPosture, which animates nothing of its own. Turned on with the first
        // SACS_REPLAY batch, unless a probe set PresenterHook.Disabled. Stock gameplay: no hook, no-op.
        private static Type sPresenterHookType;
        private static bool sPresenterResolved;
        private static FieldInfo sPresenterMode;
        private static FieldInfo sPresenterDisabled;
        private static MethodInfo sPresenterApply;
        private static MethodInfo sPresenterReport;

        // The fork's PresenterHook and its members, looked up once; nothing switched on (presenter-idle status reads the
        // counters through this, so a reading never turns presenter mode on: tooling, the per-scenario counters).
        private static bool ResolvePresenter()
        {
            if (!sPresenterResolved)
            {
                sPresenterResolved = true;
                sPresenterHookType = FindGameplayType("SimsOnline.Hooks.PresenterHook");
                if (sPresenterHookType != null)
                {
                    sPresenterMode = sPresenterHookType.GetField("Mode");
                    sPresenterDisabled = sPresenterHookType.GetField("Disabled");
                    sPresenterApply = sPresenterHookType.GetMethod("ApplyServerPosture4") ?? sPresenterHookType.GetMethod("ApplyServerPosture3") ?? sPresenterHookType.GetMethod("ApplyServerPosture2") ?? sPresenterHookType.GetMethod("ApplyServerPosture");
                    sPresenterReport = sPresenterHookType.GetMethod("Report");
                }
            }

            return sPresenterHookType != null;
        }

        internal static bool PresenterOn()
        {
            ResolvePresenter();
            if (sPresenterHookType == null || sPresenterMode == null || sPresenterApply == null)
            {
                return false;
            }

            try
            {
                if ((int)sPresenterMode.GetValue(null) != 0)
                {
                    return true;
                }

                if (sPresenterDisabled != null && (bool)sPresenterDisabled.GetValue(null))
                {
                    return false;
                }

                sPresenterMode.SetValue(null, 1);
                // presenter-idle (fork): the standing idle handlers the engine attached at load come off the puppets; the
                // stream owns their standing idle from here on.
                MethodInfo detach = sPresenterHookType.GetMethod("DetachIdleCallbacks");
                if (detach != null)
                {
                    detach.Invoke(null, null);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string PresenterApply(ulong simObjectId, string posture, ulong container, int sacsPosture)
        {
            return PresenterApply(simObjectId, posture, container, sacsPosture, 0u);
        }

        private static string PresenterApply(ulong simObjectId, string posture, ulong container, int sacsPosture, uint containmentSlot)
        {
            return PresenterApply(simObjectId, posture, container, sacsPosture, containmentSlot, null, false);
        }

        // displayName / implicitPosture: what the server's HUD shows for the posture (ApplyServerPosture3, fork
        // presenter-hud; null from an older server: the puppet posture shows its type name).
        private static string PresenterApply(ulong simObjectId, string posture, ulong container, int sacsPosture, uint containmentSlot, string displayName, bool implicitPosture)
        {
            return PresenterApply(simObjectId, posture, container, sacsPosture, containmentSlot, displayName, implicitPosture, -1);
        }

        // socials: the server posture's answer for socials (ApplyServerPosture4, fork cold-test-106; -1 = not sent).
        private static string PresenterApply(ulong simObjectId, string posture, ulong container, int sacsPosture, uint containmentSlot, string displayName, bool implicitPosture, int socials)
        {
            string context = SacsRecorder.LocalContext;
            SacsRecorder.LocalContext = "presenter:" + posture;
            try
            {
                int count = sPresenterApply.GetParameters().Length;
                object[] args = count == 8
                    ? new object[] { simObjectId, posture, container, sacsPosture, containmentSlot, displayName, implicitPosture, socials }
                    : count == 7
                    ? new object[] { simObjectId, posture, container, sacsPosture, containmentSlot, displayName, implicitPosture }
                    : count == 5
                    ? new object[] { simObjectId, posture, container, sacsPosture, containmentSlot }
                    : new object[] { simObjectId, posture, container, sacsPosture };
                return (string)sPresenterApply.Invoke(null, args);
            }
            catch (Exception)
            {
                return "error";
            }
            finally
            {
                SacsRecorder.LocalContext = context;
            }
        }

        private static string PresenterReport()
        {
            if (sPresenterHookType == null || sPresenterReport == null)
            {
                return "absent";
            }

            try
            {
                return ((string)sPresenterReport.Invoke(null, null)).Replace(';', ',');
            }
            catch (Exception)
            {
                return "error";
            }
        }

        // The toilet's blur box (UseToilet: Sim.EnableCensor(LowerBody), later AutoEnableCensor() to clear it; the
        // copy never runs the interaction). EnableCensor only raises the type; AutoEnableCensor resets it from the
        // outfit, which clears it unless the copy is in a naked outfit.
        private static long sCensorChanges;

        private static void ApplyCensor(ObjectGuid simId, string field)
        {
            int wanted;
            if (!int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out wanted))
            {
                return;
            }

            try
            {
                Sim sim = simId.ObjectFromId<Sim>();
                if (sim == null)
                {
                    return;
                }

                int current = (int)sim.GetCensorType;
                if (current == wanted)
                {
                    return;
                }

                if (wanted == 0)
                {
                    sim.AutoEnableCensor();
                }
                else
                {
                    if (current > wanted)
                    {
                        sim.AutoEnableCensor();
                    }

                    sim.EnableCensor((Sim.CensorType)wanted);
                }

                sCensorChanges++;
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        internal static bool ServerSimIsStanding(ulong simObjectId)
        {
            string posture;
            return !sServerPostures.TryGetValue(simObjectId, out posture) || posture == "StandingPosture";
        }

        private static bool IsStreamPositioned(ulong simObjectId)
        {
            DateTime at;
            return sStreamPositioned.TryGetValue(simObjectId, out at) && DateTime.UtcNow - at < StreamPositionFresh;
        }

        // / item 4: hands reconciled from the server. Objects reach and leave a Sim's hands
        // by several paths (script ParentToSlot / UnParent, native carry parenting from the animation,
        // nested food containers, copies made and dropped), and a client copy could stay in a hand
        // (cold-test-043: a bowl). Every second the server writes what a streamed Sim holds in its four
        // hand slots when it changes, and all of them every 10 seconds:
        //   Hd|sim|rightHand|rightCarry|leftHand|leftCarry   (object ids, 0 = empty)
        // and the client makes its copy of the Sim hold the same.
        private static readonly Dictionary<ulong, string> sHandStates = new Dictionary<ulong, string>();
        private static int sHandResendCounter;
        private static readonly uint[] sHandSlots = new uint[]
        {
            (uint)(int)Sim.ContainmentSlots.RightHand,
            (uint)(int)Sim.ContainmentSlots.RightCarry,
            (uint)(int)Sim.ContainmentSlots.LeftHand,
            (uint)(int)Sim.ContainmentSlots.LeftCarry,
        };

        private static void PublishHands(Sim sim, bool resend)
        {
            StringBuilder state = new StringBuilder();
            for (int i = 0; i < sHandSlots.Length; i++)
            {
                ObjectGuid held = Slots.GetContainedObject(sim.ObjectId, sHandSlots[i]);
                state.Append('|').Append(held.IsValid ? held.Value.ToString(CultureInfo.InvariantCulture) : "0");
            }

            string text = state.ToString();
            string last;
            ulong id = sim.ObjectId.Value;
            if (!resend && sHandStates.TryGetValue(id, out last) && last == text)
            {
                return;
            }

            sHandStates[id] = text;
            SacsRecorder.RingLine("Hd|" + id.ToString(CultureInfo.InvariantCulture) + text);
        }

        // queue mirror (docs/engine/input-forwarding.md). Server: a streamed Sim's interaction queue,
        // on change and every 10 seconds: Qu|sim|id:running:target:hexName:group,... ("-" when empty; hidden
        // interactions left out, as the HUD hides them). group = the item's GroupId: a continuation keeps its chain's
        // group (Prepare Mac and Cheese -> Continue Cooking), and the HUD shows a run of items sharing a group as one
        // connected item. Older clients read the first four fields.
        private static readonly Dictionary<ulong, string> sQueueStates = new Dictionary<ulong, string>();

        private static void PublishQueue(Sim sim, bool resend)
        {
            if (sim.InteractionQueue == null)
            {
                return;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            InteractionInstance running = sim.InteractionQueue.RunningInteraction;
            StringBuilder entries = new StringBuilder();
            foreach (InteractionInstance interaction in sim.InteractionQueue.InteractionList)
            {
                if (interaction == null || interaction.Hidden)
                {
                    continue;
                }

                string name;
                try
                {
                    name = interaction.GetInteractionNameForUI() ?? string.Empty;
                }
                catch (Exception)
                {
                    name = string.Empty;
                }

                if (entries.Length > 0)
                {
                    entries.Append(',');
                }

                ulong target = interaction.Target != null && !(interaction.Target is Terrain) ? interaction.Target.ObjectId.Value : 0uL;
                entries.Append(interaction.Id.ToString(c)).Append(':').Append(interaction == running ? "1" : "0").Append(':')
                    .Append(target.ToString(c)).Append(':').Append(BridgeCodec.EncodeText(name)).Append(':').Append(interaction.GroupId.ToString(c))
                    .Append(':').Append(NestedForPosture(sim, interaction) ? "1" : "0").Append(':').Append(EncodeIcon(SafeIconKey(interaction)))
                    .Append(':').Append(StageProgress(interaction));
            }

            string text = entries.Length == 0 ? "-" : entries.ToString();
            string last;
            ulong id = sim.ObjectId.Value;
            if (!resend && sQueueStates.TryGetValue(id, out last) && last == text)
            {
                return;
            }

            sQueueStates[id] = text;
            SacsRecorder.RingLine("Qu|" + id.ToString(c) + "|" + text);
        }

        // What HudModel.IsNestedInteractionForCurrentPosture answers for the server's own HUD: the item runs in the
        // Sim's current posture (a social only within the Sim's own conversation).
        private static bool NestedForPosture(Sim sim, InteractionInstance interaction)
        {
            try
            {
                Posture posture = sim.Posture;
                if (posture == null)
                {
                    return false;
                }

                SocialInteraction social = interaction as SocialInteraction;
                if (social != null && posture is Sim.SocializingPosture)
                {
                    Sim partner = social.Target as Sim;
                    if (sim.Conversation == null || partner == null || sim.Conversation != partner.Conversation)
                    {
                        return false;
                    }
                }

                return posture.Satisfies(interaction.PosturePreconditions, interaction.Target, posture.GetRequiredCheck());
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The HUD item's progress bar (sleep, homework): the active stage's portion done when it shows progress, "-" else.
        private static string StageProgress(InteractionInstance interaction)
        {
            try
            {
                Sims3.UI.Hud.IInteractionInstance item = interaction as Sims3.UI.Hud.IInteractionInstance;
                Sims3.UI.Hud.IStage stage = item == null ? null : item.ActiveIStage;
                if (stage == null || !stage.VisibleProgress)
                {
                    return "-";
                }

                return Math.Max(0f, Math.Min(1f, stage.PortionComplete(item))).ToString("F2", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "-";
            }
        }

        private static ThumbnailKey SafeIconKey(InteractionInstance interaction)
        {
            try
            {
                return interaction.GetIconKey();
            }
            catch (Exception)
            {
                return ThumbnailKey.kInvalidThumbnailKey;
            }
        }

        // A thumbnail key in one stream field: instance~type~group~templateObject~index~size~camera~bodyType~ageGender~
        // materialState~geometryState~technique ("-" = none). A template object (a Sim's or an object's own picture)
        // is the server's id; the client maps it to its copy.
        internal static string EncodeIconForStream(ThumbnailKey key)
        {
            return EncodeIcon(key);
        }

        private static string EncodeIcon(ThumbnailKey key)
        {
            if (key.mDescKey == ResourceKey.kInvalidResourceKey && !key.mTemplateObjectId.IsValid)
            {
                return "-";
            }

            // A Sim's portrait names the Sim's outfit resource, which a client's save numbers differently (a balloon
            // with Pablo's face showed Jennifer's there): send it as a reference to the Sim, drawn from the client's copy.
            Sim pictured = SimOfThumbnail(key);
            if (pictured != null)
            {
                key = ThumbnailKey.kInvalidThumbnailKey;
                key.mDescKey = ResourceKey.kInvalidResourceKey;
                key.mTemplateObjectId = pictured.ObjectId;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            return key.mDescKey.InstanceId.ToString(c) + "~" + key.mDescKey.TypeId.ToString(c) + "~" + key.mDescKey.GroupId.ToString(c)
                + "~" + key.mTemplateObjectId.Value.ToString(c) + "~" + key.mIndex.ToString(c) + "~" + ((int)key.mSize).ToString(c)
                + "~" + ((int)key.mCamera).ToString(c) + "~" + key.mBodyType.ToString(c) + "~" + key.mAgeGender.ToString(c)
                + "~" + key.mMaterialState.ToString(c) + "~" + key.mGeometryState.ToString(c) + "~" + ((int)key.mTechnique).ToString(c);
        }

        private static Sim SimOfThumbnail(ThumbnailKey key)
        {
            if (key.mDescKey == ResourceKey.kInvalidResourceKey)
            {
                return null;
            }

            try
            {
                foreach (Sim sim in LotManager.Actors)
                {
                    if (sim != null && !sim.HasBeenDestroyed && sim.GetThumbnailKey().mDescKey == key.mDescKey)
                    {
                        return sim;
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return null;
        }

        private static bool DecodeIcon(string text, out ThumbnailKey key)
        {
            key = ThumbnailKey.kInvalidThumbnailKey;
            if (string.IsNullOrEmpty(text) || text == "-")
            {
                return false;
            }

            string[] p = text.Split('~');
            if (p.Length < 12)
            {
                return false;
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                key.mDescKey = new ResourceKey(ulong.Parse(p[0], c), uint.Parse(p[1], c), uint.Parse(p[2], c));
                ulong template = ulong.Parse(p[3], c);
                key.mTemplateObjectId = template == 0uL ? ObjectGuid.InvalidObjectGuid : ReplayObject(template);
                if (key.mDescKey == ResourceKey.kInvalidResourceKey && key.mTemplateObjectId.IsValid)
                {
                    // A Sim's or an object's own picture: this client's copy draws its own (the server's key drew blank).
                    GameObject pictured = key.mTemplateObjectId.ObjectFromId<GameObject>();
                    if (pictured != null)
                    {
                        ThumbnailSize size = (ThumbnailSize)int.Parse(p[5], c);
                        key = pictured.GetThumbnailKey();
                        key.mSize = size;
                        return true;
                    }
                }

                key.mIndex = int.Parse(p[4], c);
                key.mSize = (ThumbnailSize)int.Parse(p[5], c);
                key.mCamera = (ThumbnailCamera)int.Parse(p[6], c);
                key.mBodyType = uint.Parse(p[7], c);
                key.mAgeGender = uint.Parse(p[8], c);
                key.mMaterialState = uint.Parse(p[9], c);
                key.mGeometryState = uint.Parse(p[10], c);
                key.mTechnique = (ThumbnailTechnique)int.Parse(p[11], c);
                return key.mDescKey != ResourceKey.kInvalidResourceKey || key.mTemplateObjectId.IsValid;
            }
            catch (Exception)
            {
                key = ThumbnailKey.kInvalidThumbnailKey;
                return false;
            }
        }

        // Client: the server's queue per Sim (by this client's Sim id), shown for the active Sim through the
        // fork's HudModel (AuthorityHook.DisplayQueue).
        private static readonly Dictionary<ulong, List<Sims3.UI.Hud.IInteractionInstance>> sMirroredQueues = new Dictionary<ulong, List<Sims3.UI.Hud.IInteractionInstance>>();
        private static readonly Dictionary<ulong, ulong> sMirroredQueueServerSim = new Dictionary<ulong, ulong>();
        private static ulong sDisplayedQueueSim;
        private static int sCancelIntentSeen;
        // n14: the fork's LeaveIntents ring (HudModel.CancelSocializing on a mirrored conversation).
        private static int sLeaveIntentSeen;
        private static long sLeaveIntentsSent;

        private static bool ReplayQueue(string[] f)
        {
            if (f.Length < 4)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ulong serverSim = ulong.Parse(f[2], c);
            Sim sim = ReplayObject(serverSim).ObjectFromId<Sim>();
            if (sim == null)
            {
                return false;
            }

            // The server's queue is this copy's queue: its real one stays empty. A client runs no interactions, but the save
            // it loaded brings some back after the join (Pablo showering on the client while the server's Pablo talked to
            // Jennifer), and one running there animates the copy on its own.
            CancelOwnInteractions(sim.ObjectId);

            List<Sims3.UI.Hud.IInteractionInstance> items = new List<Sims3.UI.Hud.IInteractionInstance>();
            if (f[3] != "-")
            {
                string[] entries = f[3].Split(',');
                for (int i = 0; i < entries.Length; i++)
                {
                    string[] e = entries[i].Split(':');
                    if (e.Length < 4)
                    {
                        continue;
                    }

                    ulong target = ulong.Parse(e[2], c);
                    ulong group = 0uL;
                    if (e.Length > 4)
                    {
                        ulong.TryParse(e[4], NumberStyles.None, c, out group);
                    }

                    bool nested = e.Length > 5 && e[5] == "1";
                    float progress = -1f;
                    if (e.Length > 7 && e[7] != "-")
                    {
                        float.TryParse(e[7], NumberStyles.Float, c, out progress);
                    }

                    ThumbnailKey icon = ThumbnailKey.kInvalidThumbnailKey;
                    bool hasIcon = e.Length > 6 && DecodeIcon(e[6], out icon);

                    items.Add(new QueueMirrorItem(ulong.Parse(e[0], c), group, BridgeCodec.DecodeText(e[3]), e[1] == "1",
                        target == 0uL ? ObjectGuid.InvalidObjectGuid : ReplayObject(target), sim, nested, icon, hasIcon, progress));
                }
            }

            sMirroredQueues[sim.ObjectId.Value] = items;
            sMirroredQueueServerSim[sim.ObjectId.Value] = serverSim;
            if (Sim.ActiveActor != null && Sim.ActiveActor.ObjectId.Value == sim.ObjectId.Value)
            {
                ShowMirroredQueue(sim.ObjectId.Value, items);
            }

            return true;
        }

        // Client tick: follow the active Sim, and send the HUD's cancels to the server.
        private static void UpdateQueueMirror()
        {
            if (sCurrentRole != "client" || sMirroredQueues.Count == 0)
            {
                return;
            }

            try
            {
                Sim active = Sim.ActiveActor;
                ulong activeId = active != null ? active.ObjectId.Value : 0uL;
                List<Sims3.UI.Hud.IInteractionInstance> items;
                if (activeId != sDisplayedQueueSim && sMirroredQueues.TryGetValue(activeId, out items))
                {
                    ShowMirroredQueue(activeId, items);
                }

                Type hook = AuthorityHook();
                DrainLeaveIntents(hook);
                FieldInfo sequenceField = hook == null ? null : hook.GetField("CancelIntentSequence");
                if (sequenceField == null)
                {
                    return;
                }

                int sequence = (int)sequenceField.GetValue(null);
                if (sequence == sCancelIntentSeen)
                {
                    return;
                }

                ulong[] ring = (ulong[])hook.GetField("CancelIntents").GetValue(null);
                int first = Math.Max(sCancelIntentSeen + 1, sequence - ring.Length + 1);
                sCancelIntentSeen = sequence;
                for (int i = first; i <= sequence; i++)
                {
                    QueueMirrorCancel(ring[i % ring.Length]);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // n14: the HUD's leave of the active Sim's mirrored conversation (the fork's HudModel.CancelSocializing
        // parks this client's Sim id in AuthorityHook.LeaveIntents): the server leaves; its Cv stream ends it here.
        private static void DrainLeaveIntents(Type hook)
        {
            FieldInfo sequenceField = hook == null ? null : hook.GetField("LeaveIntentSequence");
            if (sequenceField == null)
            {
                return; // a fork without the leave intent
            }

            int sequence = (int)sequenceField.GetValue(null);
            if (sequence == sLeaveIntentSeen)
            {
                return;
            }

            ulong[] ring = (ulong[])hook.GetField("LeaveIntents").GetValue(null);
            int first = Math.Max(sLeaveIntentSeen + 1, sequence - ring.Length + 1);
            sLeaveIntentSeen = sequence;
            for (int i = first; i <= sequence; i++)
            {
                ulong localSim = ring[i % ring.Length];
                ulong serverSim;
                if (!sMirroredQueueServerSim.TryGetValue(localSim, out serverSim))
                {
                    serverSim = 0uL;
                    foreach (Sim candidate in LotManager.Actors)
                    {
                        if (candidate != null && candidate.ObjectId.Value == localSim)
                        {
                            serverSim = WireSimObjectId(candidate);
                            break;
                        }
                    }
                }

                if (serverSim == 0uL)
                {
                    continue;
                }

                SendLine(BridgeCodec.EventLeaveConversationIntent(serverSim));
                sLeaveIntentsSent++;
                ShowToast("Leave sent to server");
            }
        }

        private static void ShowMirroredQueue(ulong localSim, List<Sims3.UI.Hud.IInteractionInstance> items)
        {
            Type hook = AuthorityHook();
            FieldInfo queueField = hook == null ? null : hook.GetField("DisplayQueue");
            if (queueField == null)
            {
                return; // a fork without the queue mirror
            }

            hook.GetField("DisplayQueueSim").SetValue(null, localSim);
            FieldInfo nestedField = hook.GetField("DisplayNested");
            if (nestedField != null && nestedField.GetValue(null) == null)
            {
                nestedField.SetValue(null, new Predicate<object>(IsNestedStandIn));
            }

            FieldInfo socializingField = hook.GetField("DisplayPostureSocializing");
            FieldInfo partnerField = hook.GetField("DisplaySocialPartner");
            if (socializingField != null && partnerField != null)
            {
                string social;
                sServerSocial.TryGetValue(localSim, out social);
                string[] s = (social ?? "0:0").Split(':');
                socializingField.SetValue(null, s[0] == "1");
                partnerField.SetValue(null, ulong.Parse(s[1], CultureInfo.InvariantCulture));
            }

            queueField.SetValue(null, items);
            sDisplayedQueueSim = localSim;
            try
            {
                object hud = Sims3.UI.Responder.Instance.HudModel;
                MethodInfo dirtied = hud == null ? null : hud.GetType().GetMethod("OnInteractionQueueDirtied", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (dirtied != null)
                {
                    dirtied.Invoke(hud, null);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // Fork HudModel.IsNestedInteractionForCurrentPosture for a stand-in (AuthorityHook.DisplayNested): the
        // server's answer for its own Sim, so the HUD pins the item to the posture (reading in a chair, sleeping).
        private static bool IsNestedStandIn(object item)
        {
            QueueMirrorItem mirrored = item as QueueMirrorItem;
            return mirrored != null && mirrored.Nested;
        }

        // A cancel from the HUD (HudModel.CancelInteraction via the fork, or the item itself): the server
        // cancels; its next queue line updates the HUD.
        internal static void QueueMirrorCancel(ulong interactionId)
        {
            foreach (KeyValuePair<ulong, List<Sims3.UI.Hud.IInteractionInstance>> queue in sMirroredQueues)
            {
                foreach (Sims3.UI.Hud.IInteractionInstance item in queue.Value)
                {
                    if (item.Id == interactionId)
                    {
                        ulong serverSim;
                        if (sMirroredQueueServerSim.TryGetValue(queue.Key, out serverSim))
                        {
                            SendLine(BridgeCodec.EventCancelIntent(serverSim, interactionId));
                            ShowToast("Cancel sent to server");
                        }

                        return;
                    }
                }
            }
        }

        // item 5: outfits. A Sim's visible outfit is its current category and index; changing it is
        // script (Sim.SwitchToOutfitWithoutSpin -> CASUtils.SetOutfitInGameObject), the spin around it is
        // animation and already streams. Every second the server writes a streamed Sim's outfit when it
        // changes, and every 10 seconds anyway: Ou|sim|category|index. The client switches its copy.
        private static readonly Dictionary<ulong, string> sOutfitStates = new Dictionary<ulong, string>();
        private static long sOutfitChanges;
        private static long sOutfitMissing;

        private static void PublishOutfit(Sim sim, bool resend)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string text = ((uint)sim.CurrentOutfitCategory).ToString(c) + "|" + sim.CurrentOutfitIndex.ToString(c);
            string last;
            ulong id = sim.ObjectId.Value;
            if (!resend && sOutfitStates.TryGetValue(id, out last) && last == text)
            {
                return;
            }

            sOutfitStates[id] = text;
            SacsRecorder.RingLine("Ou|" + id.ToString(c) + "|" + text);
        }

        // seq|Ou|sim|category|index
        private static bool ReplayOutfit(string[] f)
        {
            if (f.Length < 5)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            Sim sim = ReplayObject(ulong.Parse(f[2], c)).ObjectFromId<Sim>();
            if (sim == null || sim.SimDescription == null)
            {
                return false;
            }

            Sims3.SimIFace.CAS.OutfitCategories category = (Sims3.SimIFace.CAS.OutfitCategories)uint.Parse(f[3], c);
            int index = int.Parse(f[4], c);
            if (sim.CurrentOutfitCategory == category && sim.CurrentOutfitIndex == index)
            {
                return true;
            }

            // The client's description may lack the outfit (a description created at runtime on the server).
            if (index < 0 || index >= sim.SimDescription.GetOutfitCount(category))
            {
                sOutfitMissing++;
                return false;
            }

            sim.SwitchToOutfitWithoutSpin(category, index);
            sOutfitChanges++;
            return true;
        }

        // a replayed StopAllAnimation (Z) kills every driver on this client's copy of the Sim,
        // including two machines the copy owns and the server never streams: the standing posture's idle
        // machine (actor x, state standing: the anchor a route and the replay's borrowed posture drivers
        // use) and the facial-idle machine. Re-create them the way the engine does at Sim creation
        // (StandingPosture.StartHighLODIdle, OverlayData.GetGenericStateMachine), and drop a route bridge
        // (Sim.BridgeOrigin) left on a dead machine. The dead clients are left alone, not disposed: their
        // handles may already belong to another driver. Also run before every client route. B2: a puppet whose
        // standing idle the stream owns gets the stream's state instead of the creation entry (RepairStandingIdle).
        private static long sOwnMachineRepairs;

        // B2+(a): the state each repair entered on a client driver, and the tick count then (RepairEntryGate). The
        // replay's first R for that state with 0x100 and no bridge, while Simulator.TicksElapsed() has not moved (the SACS
        // pump has not taken the entry yet), is dropped: sent too, the copy entered the state twice in one pump. Tunable
        // presenter.repair-dup-drop (1 on, 0 off: an A/B arm); idleRepairDupDropped in the status and the batch reply.
        private static readonly RepairEntryGate sRepairEntries = new RepairEntryGate();
        private static readonly CachedTunable sRepairDupDrop = new CachedTunable("presenter.repair-dup-drop", 1f);
        // The requests dropped (a total since the mod loaded). Named for scripts/idle_repair_probe.py, which reads by
        // reflection the static field whose name contains idleRepairDupDropped: keep the name.
        private static long idleRepairDupDroppedCount;

        // From SacsRecorder.EnterState, after the engine took the call, whenever a repair label is the local context:
        // every repair entry, whichever caller (repair:z, repair:po, repair:walk) and whichever state it chose.
        internal static void NoteRepairEntry(uint driverHandle, uint stateHash)
        {
            long ticks = RepairEntryTicks();
            if (ticks >= 0)
            {
                sRepairEntries.Mark(driverHandle, stateHash, ticks);
            }
        }

        // The engine's tick counter, -1 when it cannot be read (then nothing is marked, and no mark matches).
        private static long RepairEntryTicks()
        {
            try
            {
                return Simulator.TicksElapsed();
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
                return -1;
            }
        }

        private static long sOwnInteractionsCancelled;

        // when each copy's late-join history last began (its seq-0 StopAllAnimation). The position mirror settles
        // the copy once after the history's clips have played (Walks.SettleMirror). The count is also the two-client check
        // of a client already presenting a Sim gets no reset when another player joins.
        private static readonly Dictionary<ulong, DateTime> sJoinResetUtc = new Dictionary<ulong, DateTime>();
        private static long sJoinResets;

        private static void NoteJoinReset(ulong simId)
        {
            sJoinResetUtc[simId] = DateTime.UtcNow;
            sJoinResets++;
        }

        internal static DateTime JoinResetUtc(ulong simObjectId)
        {
            DateTime at;
            return sJoinResetUtc.TryGetValue(simObjectId, out at) ? at : DateTime.MinValue;
        }

        private static void CancelOwnInteractions(ObjectGuid simId)
        {
            try
            {
                Sim sim = simId.ObjectFromId<Sim>();
                if (sim != null && sim.InteractionQueue != null)
                {
                    sOwnInteractionsCancelled += sim.InteractionQueue.Count;
                    sim.InteractionQueue.CancelAllInteractions();
                    // A save-loaded head can stay locked after Z stopped the driver its coroutine was waiting on.
                    // Cancellation only requests its exit; reset the queue that can no longer dequeue itself.
                    try
                    {
                        if (sim.InteractionQueue.Count > 0 || sim.InteractionQueue.GetCurrentInteraction() != null)
                        {
                            sim.InteractionQueue.OnReset();
                        }
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }
                }
            }
            catch (Exception ex)
            {
                sReplayErrors++;
                sReplayLastError = "CancelOwnInteractions:" + ex.GetType().Name;
            }
        }

        internal static void RepairOwnMachines(ObjectGuid simId)
        {
            string context = SacsRecorder.LocalContext;
            SacsRecorder.LocalContext = RepairLabel.For(context); // the caller's label (repair:z, repair:po, repair:walk) kept
            try
            {
                Sim sim = simId.ObjectFromId<Sim>();
                if (sim == null || sim.HasBeenDestroyed)
                {
                    return;
                }

                Sim.StandingPosture standing = sim.Standing as Sim.StandingPosture;
                if (standing != null && standing.CurrentStateMachine != null && !standing.CurrentStateMachine.IsValid
                    && ServerSimIsStanding(simId.Value)
                    && !(StreamOwnsIdle(sim) && ReplayDriverBootstrap.Pending(simId.Value, ResourceUtils.HashString64("idle"),
                        sReplayPending, delegate(ulong id) { return ReplayObject(id).Value; })))
                {
                    RepairStandingIdle(sim, standing, SacsRecorder.LocalContext);
                    sOwnMachineRepairs++;
                }

                // The bridge a route starts from (SimScriptAdaptor.OnAboutToStartFollowingRoute requests a state
                // on it): left on a dead machine, the copy's next route throws once (cold-test-053).
                BridgeOrigin bridge = sim.BridgeOrigin;
                if (bridge != null && (bridge.StateMachineClient == null || !bridge.StateMachineClient.IsValid))
                {
                    sim.BridgeOrigin = null;
                    sOwnMachineRepairs++;
                }

                IdleManager idle = sim.IdleManager;
                if (idle != null && idle.FacialIdleStateMachineClient != null && !idle.FacialIdleStateMachineClient.IsValid)
                {
                    idle.FacialIdleStateMachineClient = OverlayData.GetGenericStateMachine(sim, false, true, false, true);
                    idle.FacialIdleStateMachineCurrentState = null;
                    sOwnMachineRepairs++;
                }

                HandOverToOwnDrivers(simId.Value);
            }
            catch (Exception ex)
            {
                sReplayErrors++;
                sReplayLastError = "RepairOwnMachines:" + ex.GetType().Name + ":" + (ex.Message ?? "").Replace('|', '/').Replace('\n', ' ');
            }
            finally
            {
                SacsRecorder.LocalContext = context;
            }
        }

        // B2: re-create a dead standing idle machine (IdleRepairPlan). When the stream owns the idle (presenter on,
        // the fork's PresenterHook.StreamOwnsIdle) and the replay borrowed a server idle driver onto the dead machine, a
        // new idle machine is set up as the engine's own creation sets one up (Sim.SacsInitStateMachineClient: the idle
        // machine at priority -2, actor x; StandingPosture.SetDefaultIdleAnim, SetIdleCallbacks), put on the posture before
        // anything else runs (a posture with no machine stops all animation), and entered at the state the stream last
        // gave that driver, or standing_idle, instead of the creation state. Otherwise the engine's own creation
        // (StartHighLODIdle, entering standing). The entry runs under the caller's repair label plus /stream or /default.
        private static long sIdleRepairStream;
        private static long sIdleRepairDefault;
        private static MethodInfo sPresenterStreamOwnsIdle;
        private static bool sPresenterStreamOwnsResolved;

        private static void RepairStandingIdle(Sim sim, Sim.StandingPosture standing, string label)
        {
            bool streamOwns = StreamOwnsIdle(sim);
            List<uint> servers = streamOwns
                ? IdleRepairPlan.ServerDriversOn(ClientDriverHandle(standing.CurrentStateMachine), sReplayDrivers, sReplayBorrowed)
                : new List<uint>();
            uint serverIdle;
            uint actor;
            uint state;
            bool known = sReplayStates.Latest(servers, out serverIdle, out actor, out state);
            int plan = IdleRepairPlan.Decide(true, true, streamOwns, servers.Count > 0, known);
            StateMachineClient sm = null;
            if (plan != IdleRepairPlan.EngineDefault)
            {
                sm = StateMachineClient.Acquire(sim.ObjectId, "idle", (AnimationPriority)(-2), false);
                if (sm == null)
                {
                    plan = IdleRepairPlan.EngineDefault;
                }
            }

            SacsRecorder.LocalContext = label + IdleRepairPlan.Kind(plan);
            try
            {
                if (plan == IdleRepairPlan.EngineDefault)
                {
                    standing.CurrentStateMachine = null;
                    standing.StartHighLODIdle();
                    sIdleRepairDefault++;
                    return;
                }

                sm.SetActor("x", sim);
                standing.CurrentStateMachine = sm;
                standing.SetDefaultIdleAnim();
                standing.SetIdleCallbacks();
                ISacsProxy proxy = SacsRecorder.Current;
                uint handle = ClientDriverHandle(sm);
                if (plan == IdleRepairPlan.ServerState && proxy != null && handle != 0u)
                {
                    proxy.EnterState(handle, actor, state); // the stream's hashes; StateMachineClient.EnterState hashes names
                }
                else
                {
                    sm.EnterState("x", IdleRepairPlan.StandingIdleState);
                }

                sIdleRepairStream++;
            }
            finally
            {
                SacsRecorder.LocalContext = label;
            }
        }

        // Whether the fork's presenter owns this Sim's standing idle (PresenterHook.StreamOwnsIdle: a puppet, presenter
        // mode on, the presenter-idle switch on). Read only: never turns presenter mode on. A fork without it: false.
        private static bool StreamOwnsIdle(Sim sim)
        {
            if (!ResolvePresenter() || sPresenterMode == null)
            {
                return false;
            }

            try
            {
                if ((int)sPresenterMode.GetValue(null) == 0)
                {
                    return false;
                }

                if (!sPresenterStreamOwnsResolved)
                {
                    sPresenterStreamOwnsResolved = true;
                    sPresenterStreamOwnsIdle = sPresenterHookType.GetMethod("StreamOwnsIdle", BindingFlags.Public | BindingFlags.Static);
                }

                return sPresenterStreamOwnsIdle != null && (bool)sPresenterStreamOwnsIdle.Invoke(null, new object[] { sim });
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
                return false;
            }
        }

        // (2026-10-21): the replay maps a server driver onto the copy's own driver of the same machine when there is
        // one at the acquire line (FindOwnDriver). A copy whose own standing idle was dead then (a warm rejoin while it lay on
        // a bed) got a driver the replay acquired itself; RepairOwnMachines later started the copy's own idle again, and the
        // body had two live drivers of one machine (recorder: 549090747 own, 539026009 acquired): the crumpled pose after
        // the get-up (playbook pb-005). Whenever the copy has its own live driver of a machine the replay acquired, the
        // replay borrows the own one, as at the acquire line, and releases its own. Checked in RepairOwnMachines (where the
        // own idle is started again) and at most every 250 ms at a batch's end (an own driver the game started itself).
        private static long sReplayHandovers;
        private static DateTime sNextHandoverCheck = DateTime.MinValue;
        // The drivers the replay acquired on a Sim (not borrowed): server driver -> (local Sim id, machine instance).
        private static readonly Dictionary<uint, KeyValuePair<ulong, ulong>> sReplayAcquiredOn = new Dictionary<uint, KeyValuePair<ulong, ulong>>();

        private static void HandOverToOwnDrivers(ulong simFilter)
        {
            ISacsProxy proxy = SacsRecorder.Current;
            if (proxy == null || sReplayAcquiredOn.Count == 0)
            {
                return;
            }

            List<KeyValuePair<uint, KeyValuePair<ulong, ulong>>> acquiredOn = new List<KeyValuePair<uint, KeyValuePair<ulong, ulong>>>(sReplayAcquiredOn);
            for (int i = 0; i < acquiredOn.Count; i++)
            {
                uint serverDriver = acquiredOn[i].Key;
                ulong simId = acquiredOn[i].Value.Key;
                uint acquired;
                if (!sReplayDrivers.TryGetValue(serverDriver, out acquired) || sReplayBorrowed.ContainsKey(serverDriver))
                {
                    sReplayAcquiredOn.Remove(serverDriver); // released, re-resolved or borrowed since
                    continue;
                }

                if (simFilter != 0uL && simId != simFilter)
                {
                    continue;
                }

                uint own = FindOwnDriver(new ObjectGuid(simId), acquiredOn[i].Value.Value);
                if (own == 0u || own == acquired || ReplayAcquiredDriver(own))
                {
                    continue;
                }

                sReplayAcquiredOn.Remove(serverDriver);
                proxy.ReleaseDriver(acquired);
                EventQueueRecorder.Detach(acquired);
                SacsRecorder.Authority.Unfollow(acquired);
                sReplayDrivers[serverDriver] = own;
                sReplayBorrowed[serverDriver] = true;
                SacsRecorder.Authority.Follow(own);
                SacsSeedCopy.NoteMapped(serverDriver, own, acquiredOn[i].Value.Value, true);
                sReplayHandovers++;
            }
        }

        // Whether a client driver is one the replay acquired for some server driver (a posture client never wraps one
        // today; the handover leaves such a driver alone rather than borrow it twice).
        private static bool ReplayAcquiredDriver(uint driver)
        {
            foreach (KeyValuePair<uint, uint> mapped in sReplayDrivers)
            {
                if (mapped.Value == driver && !sReplayBorrowed.ContainsKey(mapped.Key))
                {
                    return true;
                }
            }

            return false;
        }

        // route stream, server: when a streamed Sim starts following a route, ring its winning goal
        // (Rt|sim|x|y|z|tag|destSlot|destObj); when it stops routing, ring where it stopped (Rte|sim|x|y|z). The running route is the first route action's
        // FollowRouteAction.RouteToBeFollowed (RoutingComponent.mRouteActions is protected: reflection);
        // a new route object is a new route. Zero-length routes (a social turning to face) are not sent.
        private static FieldInfo sRouteActionsField;
        private static readonly Dictionary<ulong, Route> sPublishedRoutes = new Dictionary<ulong, Route>();
        private static long sRoutesPublished;
        // STOPGAP(boundary-recording): route-min-distance a turn to face is not sent, so the facing mirror turns the copy later; send every route.
        private const float RouteMinPublishDistance = 0.3f;
        // Off by default: in two-client runs (cold-test-057..059) walking improved (moving 0.66 -> 0.88-0.96)
        // but animation and standing position slipped about as much, so the visible score did not move.
        private static bool sRouteStreamEnabled;

        private static void PublishRoutes(Dictionary<ulong, bool> targets)
        {
            if (!sRouteStreamEnabled || targets == null || targets.Count == 0)
            {
                return;
            }

            if (RouteRecorder.Installed)
            {
                RouteRecorder.PublishEnds(); // the motion boundary sends starts and stops (Rs / Rx)
                return;
            }

            if (sRouteActionsField == null)
            {
                sRouteActionsField = typeof(RoutingComponent).GetField("mRouteActions", BindingFlags.NonPublic | BindingFlags.Instance);
                if (sRouteActionsField == null)
                {
                    return;
                }
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            foreach (ulong id in targets.Keys)
            {
                try
                {
                    Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                    if (sim == null)
                    {
                        continue;
                    }

                    Route current = null;
                    RoutingComponent routing = sim.RoutingComponent;
                    if (routing != null && routing.IsRouting)
                    {
                        List<Sims3.Gameplay.Routing.RouteAction> actions = sRouteActionsField.GetValue(routing) as List<Sims3.Gameplay.Routing.RouteAction>;
                        if (actions != null && actions.Count > 0)
                        {
                            Sims3.Gameplay.Routing.FollowRouteAction follow = actions[0] as Sims3.Gameplay.Routing.FollowRouteAction;
                            if (follow != null)
                            {
                                current = follow.RouteToBeFollowed;
                            }
                        }
                    }

                    Route last;
                    sPublishedRoutes.TryGetValue(id, out last);
                    if (current == null)
                    {
                        if (last != null)
                        {
                            sPublishedRoutes.Remove(id);
                            Vector3 end = sim.Position;
                            SacsRecorder.RingLine("Rte|" + id.ToString(c) + "|" + end.x.ToString("F3", c) + "|" + end.y.ToString("F3", c) + "|" + end.z.ToString("F3", c));
                        }

                        continue;
                    }

                    if (object.ReferenceEquals(current, last))
                    {
                        continue;
                    }

                    sPublishedRoutes[id] = current;
                    RoutePlanResult plan = current.PlanResult;
                    Vector3 goal = plan.mDestination;
                    if (!plan.Succeeded() || (goal - sim.Position).Length() < RouteMinPublishDistance)
                    {
                        continue;
                    }

                    ulong destObj = 0uL;
                    if (current.DestObj != null && current.DestObj.Proxy != null)
                    {
                        destObj = current.DestObj.Proxy.ObjectId.Value;
                    }

                    SacsRecorder.RingLine("Rt|" + id.ToString(c) + "|" + goal.x.ToString("F3", c) + "|" + goal.y.ToString("F3", c) + "|" + goal.z.ToString("F3", c)
                        + "|" + plan.mDestinationTag.ToString(c) + "|" + plan.mDestSlotNameHash.ToString(c) + "|" + destObj.ToString(c)
                        + "|" + RouteWaypoints(current, c) + "|" + (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond).ToString(c));
                    sRoutesPublished++;
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
            }
        }

        // Walking on clients (2026-10-12): the server's planned path as waypoints, its segments' end points in order
        // ("x:y:z,x:y:z,..."), so a client walks the same path instead of planning its own (Route.Append(Vector3) =
        // Route_AppendSegment, which extends an existing path: the client plans to the first waypoint, then appends the
        // rest). "-" when the route has a path that isn't plain walking (portals, stairs, water, vehicles: those keep
        // the client's own plan to the goal) or more than RouteMaxWaypoints segments.
        private const int RouteMaxWaypoints = 64;

        internal static bool RouteStreamOn
        {
            get { return sRouteStreamEnabled; }
        }

        internal static string RouteWaypointsForStream(Route route)
        {
            return RouteWaypoints(route, CultureInfo.InvariantCulture);
        }

        private static string RouteWaypoints(Route route, CultureInfo c)
        {
            try
            {
                uint paths = route.GetNumPaths();
                for (uint p = 0; p < paths; p++)
                {
                    if (route.GetPathData(p).PathType != PathType.SimPath)
                    {
                        return "-";
                    }
                }

                uint segments = route.GetNumSegments();
                if (segments == 0 || segments > RouteMaxWaypoints)
                {
                    return "-";
                }

                StringBuilder text = new StringBuilder();
                for (uint i = 0; i < segments; i++)
                {
                    Vector3 point;
                    if (!route.GetSegmentEndPoint(route.GetSegmentIdAtIndex(i), out point))
                    {
                        return "-";
                    }

                    if (i > 0)
                    {
                        text.Append(',');
                    }

                    text.Append(point.x.ToString("F2", c)).Append(':').Append(point.y.ToString("F2", c)).Append(':').Append(point.z.ToString("F2", c));
                }

                return text.ToString();
            }
            catch (Exception)
            {
                return "-";
            }
        }

        // While a client route walks a Sim, the stream's animation lines for that Sim (drivers it hosts or
        // acts in, its StopAllAnimation) wait, and play in order once the route ends: the copy finishes
        // walking, then does what the server's Sim did next. Played straight away they were overridden
        // by the route (the follower, then the route's end in the copy's idle machine) and long loop
        // states never came back (cold-test-052/053).
        private static readonly Dictionary<uint, List<ulong>> sReplayDriverObjects = new Dictionary<uint, List<ulong>>();
        private static readonly Dictionary<ulong, List<string>> sRouteHeldLines = new Dictionary<ulong, List<string>>();
        private static readonly List<string> sRouteReleasedLines = new List<string>();
        private const int RouteHeldLinesCap = 2000;
        private static long sRouteLinesHeld;
        private static long sRouteLinesReleased;
        private static long sRouteLinesDropped;
        // What became of released lines (applied, skipped: unknown driver or unresolved host, request
        // failures among the applied) and the last release itself, for a probe.
        private static long sRouteReleaseApplied;
        private static long sRouteReleaseSkipped;
        private static long sRouteReleaseFailed;
        private static long sReplayRequestFailureTotal;
        private static string sRouteLastRelease = "-";

        private static void CountReleaseOutcome(int appliedLines, int skippedLines, long failedRequests)
        {
            sRouteReleaseApplied += appliedLines;
            sRouteReleaseSkipped += skippedLines;
            sRouteReleaseFailed += failedRequests;
        }

        private static bool IsClientRouting(ulong clientObjectId)
        {
            return (PresentationModules.WalkState(clientObjectId) & PresentationModules.WalkRunning) != 0;
        }

        // The objects a driver line concerns (its host from A, actors from S), remembered per server driver.
        private static void NoteDriverObjects(string kind, string[] f, uint serverDriver)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            ulong objectId = 0uL;
            if (kind == "A" && f.Length > 3)
            {
                objectId = ReplayObject(ulong.Parse(f[3], c)).Value;
                sReplayDriverObjects[serverDriver] = new List<ulong>();
            }
            else if (kind == "S" && f.Length > 4)
            {
                objectId = ReplayObject(ulong.Parse(f[4], c)).Value;
            }

            if (objectId == 0uL)
            {
                return;
            }

            List<ulong> objects;
            if (!sReplayDriverObjects.TryGetValue(serverDriver, out objects))
            {
                objects = new List<ulong>();
                sReplayDriverObjects[serverDriver] = objects;
            }

            if (!objects.Contains(objectId))
            {
                objects.Add(objectId);
            }
        }

        // When the stream last moved a machine the replay acquired itself (an E line, or an R line that machine took), by
        // each local object that machine's lines concern (NoteDriverObjects). Borrowed drivers are the copy's own idles and
        // do not count. A recorder only: the position mirror decides what to hold (Walks.SettleMirror, mirror.anim-hold).
        private static readonly Dictionary<ulong, DateTime> sStreamAnimatedUtc = new Dictionary<ulong, DateTime>();
        private static long sStreamAnimatedStamps;

        private static void NoteStreamAnimated(uint serverDriver)
        {
            List<ulong> objects;
            if (sReplayBorrowed.ContainsKey(serverDriver) || !sReplayDriverObjects.TryGetValue(serverDriver, out objects))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < objects.Count; i++)
            {
                sStreamAnimatedUtc[objects[i]] = now;
            }

            sStreamAnimatedStamps++;
        }

        // DateTime.MinValue when the stream has not animated this object through an acquired machine.
        internal static DateTime StreamAnimatedUtc(ulong objectId)
        {
            DateTime at;
            return sStreamAnimatedUtc.TryGetValue(objectId, out at) ? at : DateTime.MinValue;
        }

        // The Sim this driver line must wait for, or 0.
        private static ulong RoutingSimForDriver(uint serverDriver)
        {
            List<ulong> objects;
            if (!PresentationModules.HasWalks || !sReplayDriverObjects.TryGetValue(serverDriver, out objects))
            {
                return 0uL;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                if (IsClientRouting(objects[i]))
                {
                    return objects[i];
                }
            }

            return 0uL;
        }

        // STOPGAP(boundary-recording): hold-for-route a client walk holds the lines behind it; ordered presentation needs it until walks match the server's.
        private static void HoldForRoute(ulong sim, string line)
        {
            List<string> held;
            if (!sRouteHeldLines.TryGetValue(sim, out held))
            {
                held = new List<string>();
                sRouteHeldLines[sim] = held;
            }

            if (held.Count >= RouteHeldLinesCap)
            {
                sRouteLinesDropped++;
                RecordReplaySkip(SkipSiteRouteHoldFull, line);
                return;
            }

            held.Add(line);
            sRouteLinesHeld++;
        }

        internal static void ReleaseHeldLines(ulong sim)
        {
            List<string> held;
            if (sRouteHeldLines.TryGetValue(sim, out held))
            {
                sRouteHeldLines.Remove(sim);
                sRouteReleasedLines.AddRange(held);
                sRouteLinesReleased += held.Count;
            }
        }
        // This client's copy (local id) is walking on the server's path (the module's Walks): placements wait for the walk.
        private static bool IsClientWalking(ulong localSim)
        {
            return (PresentationModules.WalkState(localSim) & PresentationModules.WalkMoving) != 0;
        }

        // Handler verb RouteHeld: true while a client walk (the presentation module's Walks) holds this Sim (this client's
        // object id). The position mirror then leaves it alone; the walk's end places it.
        private static bool IsRouteHeld(ulong simObjectId, Vector3 target)
        {
            if ((PresentationModules.WalkState(simObjectId) & PresentationModules.WalkHoldsPlace) != 0)
            {
                return true;
            }

            // The stream positions this Sim (Ps): the snapshot mirror's late positions would pull it back.
            if (IsStreamPositioned(simObjectId))
            {
                sStreamMirrorSkips++;
                return true;
            }

            return false;
        }

        // Client side of the sound boundary (AudioRecorder): the same object sound on this client's copy, through this game's
        // own IAudio; the server's sound handle maps to this client's.
        private static IAudio sClientAudio;
        private static readonly Dictionary<uint, uint> sClientSoundHandles = new Dictionary<uint, uint>();
        private static long sSoundsPlayed;
        private static long sSoundsFailed;
        private static long sSoundArrays; // sound arrays started (Au a): the dj-booth scenario's check

        private static bool ReplayAudio(string[] f)
        {
            if (f.Length < 4)
            {
                return false;
            }

            if (sClientAudio == null)
            {
                FieldInfo field = AudioRecorder.HolderField();
                sClientAudio = field == null ? null : field.GetValue(null) as IAudio;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            if (f[2] == "0")
            {
                return sClientAudio != null && ReplayPointSound(f);
            }

            ObjectGuid obj = ReplayObject(ulong.Parse(f[2], c));
            if (sClientAudio == null || !obj.IsValid)
            {
                return false;
            }

            try
            {
                uint local;
                switch (f[3])
                {
                    case "o":
                        local = sClientAudio.StartObjectSound(obj, BridgeCodec.DecodeText(f[5]), f[6] == "1");
                        KeepSound(uint.Parse(f[4], c), local);
                        break;
                    case "w":
                        local = sClientAudio.StartObjectSoundWithFlags(obj, BridgeCodec.DecodeText(f[5]), uint.Parse(f[6], c));
                        KeepSound(uint.Parse(f[4], c), local);
                        break;
                    case "x":
                        {
                            uint server = uint.Parse(f[4], c);
                            if (sClientSoundHandles.TryGetValue(server, out local))
                            {
                                sClientSoundHandles.Remove(server);
                                sClientAudio.StopObjectSound(obj, local);
                            }

                            break;
                        }
                    case "z":
                        sClientAudio.StopAllObjectSound(obj);
                        break;
                    case "u":
                        sClientAudio.MuteAllObjectSounds(obj, f[4] == "1");
                        break;
                    case "s":
                        if (sClientSoundHandles.TryGetValue(uint.Parse(f[4], c), out local))
                        {
                            sClientAudio.SetSoundProperty(local, uint.Parse(f[5], c), float.Parse(f[6], c));
                        }

                        break;
                    case "p":
                    case "g":
                        {
                            ObjectGuid target = ReplayObject(ulong.Parse(f[4], c));
                            if (!target.IsValid)
                            {
                                return false;
                            }

                            if (f[3] == "p")
                            {
                                sClientAudio.PlayObjectPutSound(obj, target);
                            }
                            else
                            {
                                sClientAudio.PlayObjectGetSound(obj, target);
                            }

                            break;
                        }
                    case "a":
                    case "A":
                        {
                            SoundSpec[] children = ReplaySoundSpecs(f[f[3] == "a" ? 6 : 5]);
                            if (children == null)
                            {
                                return false;
                            }

                            uint server = uint.Parse(f[4], c);
                            if (f[3] == "a")
                            {
                                local = sClientAudio.StartSoundArray(BridgeCodec.DecodeText(f[5]), children);
                                KeepSound(server, local);
                                if (local != 0u)
                                {
                                    sSoundArrays++;
                                }
                            }
                            else if (sClientSoundHandles.TryGetValue(server, out local))
                            {
                                sClientAudio.UpdateSoundArray(local, children);
                            }

                            break;
                        }
                    case "y":
                        {
                            uint server = uint.Parse(f[4], c);
                            if (sClientSoundHandles.TryGetValue(server, out local))
                            {
                                sClientSoundHandles.Remove(server);
                                sClientAudio.StopSoundArray(local);
                            }

                            break;
                        }
                    case "q":
                        if (sClientSoundHandles.TryGetValue(uint.Parse(f[4], c), out local))
                        {
                            sClientAudio.SetSoundArrayGain(local, float.Parse(f[5], c));
                        }

                        break;
                    case "P":
                        sClientAudio.PlayObjectPutOnFloorSound(obj);
                        break;
                    case "G":
                        sClientAudio.PlayObjectGetFromFloorSound(obj);
                        break;
                    default:
                        return false;
                }

                sSoundsPlayed++;
                return true;
            }
            catch (Exception)
            {
                sSoundsFailed++;
                return false;
            }
        }

        // A sound at a point in the world (Au|0|t|handle|x|y|z|hexName, Au|0|x|handle, Au|0|s|handle|key|value): the same
        // point on this client (lots and terrain are where they are on every game); the server's handle maps to this client's.
        private static long sPointSounds;

        private static bool ReplayPointSound(string[] f)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            try
            {
                uint local;
                uint server = uint.Parse(f[4], c);
                switch (f[3])
                {
                    case "t":
                        local = sClientAudio.StartSound(BridgeCodec.DecodeText(f[8]), new Vector3(float.Parse(f[5], c), float.Parse(f[6], c), float.Parse(f[7], c)));
                        KeepSound(server, local);
                        if (local == 0u)
                        {
                            sSoundsFailed++;
                            return false;
                        }

                        sPointSounds++;
                        break;
                    case "x":
                        if (sClientSoundHandles.TryGetValue(server, out local))
                        {
                            sClientSoundHandles.Remove(server);
                            sClientAudio.StopSound(local);
                        }

                        break;
                    case "s":
                        if (sClientSoundHandles.TryGetValue(server, out local))
                        {
                            sClientAudio.SetSoundProperty(local, uint.Parse(f[5], c), float.Parse(f[6], c));
                        }

                        break;
                    default:
                        return false;
                }

                sSoundsPlayed++;
                return true;
            }
            catch (Exception)
            {
                sSoundsFailed++;
                return false;
            }
        }

        // A sound array's children (obj~hexName;...), each object mapped to this client's copy; null when one has none.
        private static SoundSpec[] ReplaySoundSpecs(string text)
        {
            if (text == "-")
            {
                return new SoundSpec[0];
            }

            string[] parts = text.Split(';');
            SoundSpec[] specs = new SoundSpec[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int tilde = parts[i].IndexOf('~');
                ulong server = ulong.Parse(tilde < 0 ? parts[i] : parts[i].Substring(0, tilde), CultureInfo.InvariantCulture);
                ObjectGuid copy = server == 0uL ? ObjectGuid.InvalidObjectGuid : ReplayObject(server);
                if (server != 0uL && !copy.IsValid)
                {
                    return null;
                }

                specs[i].ObjectId = copy.Value;
                specs[i].Name = tilde < 0 ? string.Empty : BridgeCodec.DecodeText(parts[i].Substring(tilde + 1));
            }

            return specs;
        }

        private static void KeepSound(uint server, uint local)
        {
            if (sClientSoundHandles.Count > 2048)
            {
                sClientSoundHandles.Clear();
            }

            sClientSoundHandles[server] = local;
        }

        // Client side of the direct-animation boundary (AnimationRecorder): the same calls on this client's copy, through
        // this game's own Animation implementation. A random clip controller is kept by the server's controller id.
        private static IAnimation sClientAnimation;
        private static readonly Dictionary<string, uint> sClientClipControllers = new Dictionary<string, uint>();
        private static long sAnimationApplied;
        private static long sAnimationFailed;

        private static IAnimation ClientAnimation()
        {
            if (sClientAnimation == null)
            {
                FieldInfo field = typeof(Animation).GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static);
                sClientAnimation = field == null ? null : field.GetValue(null) as IAnimation;
            }

            return sClientAnimation;
        }

        private static ResourceKey KeyAt(string[] f, int i)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return new ResourceKey(ulong.Parse(f[i], c), uint.Parse(f[i + 1], c), uint.Parse(f[i + 2], c));
        }

        private static ResourceKey KeyOf(string text)
        {
            string[] p = text.Split('~');
            CultureInfo c = CultureInfo.InvariantCulture;
            return new ResourceKey(ulong.Parse(p[0], c), uint.Parse(p[1], c), uint.Parse(p[2], c));
        }

        private static bool ReplayAnimation(string[] f)
        {
            if (f.Length < 4)
            {
                return false;
            }

            IAnimation anim = ClientAnimation();
            ObjectGuid obj = ReplayObject(ulong.Parse(f[2], CultureInfo.InvariantCulture));
            if (anim == null || !obj.IsValid)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            try
            {
                switch (f[3])
                {
                    case "b":
                        anim.SetBonePosition(obj, uint.Parse(f[4], c), new Vector3(float.Parse(f[5], c), float.Parse(f[6], c), float.Parse(f[7], c)), float.Parse(f[8], c));
                        break;
                    case "r":
                        anim.SetBoneTimeRotation(obj, uint.Parse(f[4], c), float.Parse(f[5], c));
                        break;
                    case "t":
                        anim.CreateTransformController(obj);
                        break;
                    case "m":
                        anim.CreateTimeController(obj);
                        break;
                    case "p":
                        anim.PlayAnimation(obj, uint.Parse(f[4], c), KeyAt(f, 5), float.Parse(f[8], c), false);
                        break;
                    case "s":
                        anim.StartPose(obj, uint.Parse(f[4], c), KeyAt(f, 5));
                        break;
                    case "z":
                        anim.StopAllAnimation(obj);
                        break;
                    case "f":
                        anim.ForceAnimation(obj, f[4] == "1");
                        break;
                    case "q":
                        {
                            string[] keyText = f[8] == "-" ? new string[0] : f[8].Split(';');
                            ResourceKey[] keys = new ResourceKey[keyText.Length];
                            for (int i = 0; i < keyText.Length; i++)
                            {
                                keys[i] = KeyOf(keyText[i]);
                            }

                            uint local = anim.StartRandomClipController(obj, uint.Parse(f[5], c), float.Parse(f[6], c), KeyOf(f[7]), keys, ParseFloats(f[9]), ParseFloats(f[10]));
                            sClientClipControllers[obj.Value.ToString(c) + ":" + f[4]] = local;
                            break;
                        }
                    case "w":
                        {
                            uint local;
                            string key = obj.Value.ToString(c) + ":" + f[5];
                            if (sClientClipControllers.TryGetValue(key, out local))
                            {
                                anim.StopRandomClipController(obj, uint.Parse(f[4], c), local);
                                sClientClipControllers.Remove(key);
                            }

                            break;
                        }
                    case "d":
                        anim.EnableDisableForceDiscontinuousOnParenting(obj, f[4] == "1");
                        break;
                    case "l":
                        anim.EnableDisableLargeAnimalEvalNode(obj, f[4] == "1");
                        break;
                    case "g":
                        anim.EnableDisableScubaDivingMode(obj, f[4] == "1", f[5] == "1");
                        break;
                    case "h":
                        anim.SetLookAtsHidden(obj, f[4] == "1");
                        break;
                    case "a":
                        if (obj.ObjectFromId<Sim>() != null)
                        {
                            return true; // a Sim's procedural motion is the motion pass's (internal documentation)
                        }

                        anim.PlayProcedurallyMotionAccumulatedAnimation(obj, uint.Parse(f[4], c), KeyAt(f, 5),
                            new Vector3(float.Parse(f[8], c), float.Parse(f[9], c), float.Parse(f[10], c)),
                            new Vector3(float.Parse(f[11], c), float.Parse(f[12], c), float.Parse(f[13], c)));
                        break;
                    default:
                        return false;
                }

                sAnimationApplied++;
                return true;
            }
            catch (Exception)
            {
                sAnimationFailed++;
                return false;
            }
        }

        private static string AnimationReport()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "installed:" + (AnimationRecorder.Installed ? "1" : "0") + ",lines:" + AnimationRecorder.Lines.ToString(c) + ",late:" + SacsRecorder.LateKept.ToString(c)
                + "/" + SacsRecorder.LateFlushed.ToString(c) + ",client:applied:" + sAnimationApplied.ToString(c) + "/failed:" + sAnimationFailed.ToString(c);
        }

        // Client side of the effects boundary (VfxRecorder): the server's effects on this client's copies, by the
        // server's effect handle. An effect's log arrives again with each new parent; for an effect this client already
        // has, only the parent applies.
        private sealed class ClientEffect
        {
            public VisualEffect Effect;
            public bool Skip;
        }

        private static readonly Dictionary<ulong, ClientEffect> sClientEffects = new Dictionary<ulong, ClientEffect>();
        private const int ClientEffectCapacity = 512;
        private static long sVfxCreated;
        private static long sVfxParented;
        private static long sVfxDisposed;
        private static long sVfxFailed;
        private static long sVfxOrphans;

        private static bool ReplayVfx(string[] f)
        {
            if (f.Length < 5)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ulong serverParent = ulong.Parse(f[2], c);
            ulong id = ulong.Parse(f[3], c);
            string op = f[4];
            ClientEffect entry;
            sClientEffects.TryGetValue(id, out entry);
            try
            {
                if (op == "c")
                {
                    if (entry != null)
                    {
                        entry.Skip = true; // a re-parent: its log is already applied here
                        return true;
                    }

                    if (sClientEffects.Count >= ClientEffectCapacity || f.Length < 6)
                    {
                        return false;
                    }

                    VisualEffect created = VisualEffect.Create(BridgeCodec.DecodeText(f[5]));
                    if (created == null)
                    {
                        sVfxFailed++;
                        return false;
                    }

                    entry = new ClientEffect();
                    entry.Effect = created;
                    sClientEffects[id] = entry;
                    sVfxCreated++;
                    return true;
                }

                if (entry == null || entry.Effect == null)
                {
                    // An effect this client let the engine end (auto-destroy) or never had (its parent is not streamed
                    // here): a later stop or dispose has nothing to do.
                    sVfxOrphans++;
                    return op != "p";
                }

                VisualEffect fx = entry.Effect;
                if (op == "p")
                {
                    entry.Skip = false;
                    GameObject parent = ReplayObject(serverParent).ObjectFromId<GameObject>();
                    StandIn standIn;
                    if (parent == null && f.Length >= 9 && sStandIns.TryGetValue(serverParent, out standIn))
                    {
                        // A server object this client never makes (a fire): its effect plays where it is.
                        Vector3 offset = new Vector3(float.Parse(f[6], c), float.Parse(f[7], c), float.Parse(f[8], c));
                        fx.SetPosAndOrient(standIn.Position + offset, standIn.Forward, Vector3.UnitY);
                        sStandInEffects++;
                        return true;
                    }

                    if (parent == null || f.Length < 9)
                    {
                        return false;
                    }

                    Vector3 trans = new Vector3(float.Parse(f[6], c), float.Parse(f[7], c), float.Parse(f[8], c));
                    sVfxParented++;
                    return fx.ParentTo(parent, (Slot)uint.Parse(f[5], c), ref trans);
                }

                if (entry.Skip)
                {
                    return true;
                }

                switch (op)
                {
                    case "s":
                        fx.Start((VisualEffect.TransitionType)int.Parse(f[5], c), false);
                        return true;
                    case "x":
                        fx.Stop((VisualEffect.TransitionType)int.Parse(f[5], c));
                        return true;
                    case "o":
                        fx.SetPosAndOrient(new Vector3(float.Parse(f[5], c), float.Parse(f[6], c), float.Parse(f[7], c)),
                            new Vector3(float.Parse(f[8], c), float.Parse(f[9], c), float.Parse(f[10], c)),
                            new Vector3(float.Parse(f[11], c), float.Parse(f[12], c), float.Parse(f[13], c)));
                        return true;
                    case "k":
                        return fx.SetEffectColorScale(float.Parse(f[5], c), float.Parse(f[6], c), float.Parse(f[7], c));
                    case "m":
                        return fx.SetEffectModelOverride(int.Parse(f[5], c), int.Parse(f[6], c));
                    case "t":
                        return fx.SetEffectTextureOverride(ulong.Parse(f[5], c), int.Parse(f[6], c), uint.Parse(f[7], c));
                    case "2":
                        return fx.SetEffectDoubleTextureOverride(ulong.Parse(f[5], c), int.Parse(f[6], c), uint.Parse(f[7], c),
                            ulong.Parse(f[8], c), int.Parse(f[9], c), uint.Parse(f[10], c));
                    case "n":
                        {
                            ThumbnailKey key;
                            if (!DecodeIcon(f[5], out key))
                            {
                                return false;
                            }

                            return fx.SetEffectThumbnailTextureOverride(key, int.Parse(f[6], c));
                        }
                    case "h":
                        return fx.SetEffectHeadlineTextureOverride(BridgeCodec.DecodeText(f[5]));
                    case "l":
                        return fx.SetEffectLifeScale(float.Parse(f[5], c));
                    case "a":
                        fx.SetAutoDestroy(f[5] == "1");
                        if (f[5] == "1")
                        {
                            sClientEffects.Remove(id); // the engine ends it
                        }

                        return true;
                    case "v":
                        fx.SetNeverHide(f[5] == "1");
                        return true;
                    case "f":
                        return fx.SetFloatParams(int.Parse(f[5], c), ParseFloats(f[6]));
                    case "i":
                        {
                            int type = int.Parse(f[5], c);
                            int[] values = ParseInts(f[6]);
                            if (type == 9 && values.Length == 2)
                            {
                                // kParamObjectId: an object id in two halves (VisualEffect.SetParentObjectId), this client's
                                ulong server = (ulong)(uint)values[0] | ((ulong)(uint)values[1] << 32);
                                ObjectGuid local = ReplayObject(server);
                                ulong mapped = local.IsValid ? local.Value : server;
                                values[0] = (int)(mapped & 0xFFFFFFFFu);
                                values[1] = (int)((mapped >> 32) & 0xFFFFFFFFu);
                            }

                            return fx.SetIntParams(type, values);
                        }
                    case "b":
                        fx.SetUseBoneParenting(f[5] == "1");
                        return true;
                    case "g":
                        {
                            GameObject target = ReplayObject(ulong.Parse(f[5], c)).ObjectFromId<GameObject>();
                            if (target == null)
                            {
                                return false;
                            }

                            return fx.SetTargetObjectAndSlotOrBone(target, (Slot)uint.Parse(f[6], c),
                                new Vector3(float.Parse(f[7], c), float.Parse(f[8], c), float.Parse(f[9], c)), f[10] == "1");
                        }
                    case "d":
                        sClientEffects.Remove(id);
                        fx.Dispose();
                        sVfxDisposed++;
                        return true;
                }

                return false;
            }
            catch (Exception)
            {
                sVfxFailed++;
                return false;
            }
        }

        private static float[] ParseFloats(string text)
        {
            if (string.IsNullOrEmpty(text) || text == "-")
            {
                return new float[0];
            }

            string[] parts = text.Split(';');
            float[] values = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                values[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            }

            return values;
        }

        private static int[] ParseInts(string text)
        {
            if (string.IsNullOrEmpty(text) || text == "-")
            {
                return new int[0];
            }

            string[] parts = text.Split(';');
            int[] values = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                values[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
            }

            return values;
        }

        private static string VfxReport()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return VfxRecorder.Report().Replace(';', ',') + ",client:created:" + sVfxCreated.ToString(c) + "/parented:" + sVfxParented.ToString(c)
                + "/disposed:" + sVfxDisposed.ToString(c) + "/failed:" + sVfxFailed.ToString(c) + "/orphans:" + sVfxOrphans.ToString(c) + "/live:" + sClientEffects.Count.ToString(c);
        }

        // Moodlets (2026-10-14): a client's copies kept their save's buffs, so the HUD's moodlets (and mood) were the
        // client's. Server, every prop poll, when a streamed Sim's set changes and every 10 s (with time left):
        //   Bf|sim|guid:origin:minutesLeft:uiMinutes,...   ("-" = none)
        // and the client gives its copy the same set and times. uiMinutes is what the HUD shows as the time left
        // (BuffInstance.UITimeoutCount) worked out for the buff's owner: a need moodlet (-1 timeout) takes it from the
        // owner's motive decay, which a puppet does not have. The client shows it through the fork's moodlet-time hook
        // (PresenterHook.DisplayTimeout), counted down by the sim minutes since it arrived.
        // The client side is the presentation module's (Presentation/Moodlets.cs).
        private static readonly Dictionary<ulong, string> sPublishedBuffs = new Dictionary<ulong, string>();

        private static void PublishBuffs(Sim sim, bool resend)
        {
            if (sim.BuffManager == null)
            {
                return;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            List<string> set = new List<string>();
            List<string> timed = new List<string>();
            foreach (BuffInstance buff in sim.BuffManager.List)
            {
                string key = ((ulong)buff.Guid).ToString(c) + ":" + ((ulong)buff.BuffOrigin).ToString(c);
                set.Add(key);
                timed.Add(key + ":" + buff.TimeoutCount.ToString("F0", c) + ":" + UiMinutes(sim, buff).ToString("F1", c));
            }

            set.Sort(StringComparer.Ordinal);
            timed.Sort(StringComparer.Ordinal);
            string membership = set.Count == 0 ? "-" : string.Join(",", set.ToArray());
            ulong id = sim.ObjectId.Value;
            string last;
            if (!resend && sPublishedBuffs.TryGetValue(id, out last) && last == membership)
            {
                return;
            }

            sPublishedBuffs[id] = membership;
            SacsRecorder.RingLine("Bf|" + id.ToString(c) + "|" + (timed.Count == 0 ? "-" : string.Join(",", timed.ToArray())));
        }

        private static FieldInfo sInfiniteTimeoutBuffs;
        // Server: BuffInstance.UITimeoutCount for the buff's owner rather than the selected Sim.
        private static float UiMinutes(Sim sim, BuffInstance buff)
        {
            float timeout = buff.TimeoutCount;
            if (timeout != -1f || buff.mBuff == null)
            {
                return timeout;
            }

            try
            {
                CommodityKind commodity = buff.mBuff.SolveCommodity;
                if (commodity == CommodityKind.None || sim.Motives == null)
                {
                    return -1f;
                }

                if (sInfiniteTimeoutBuffs == null)
                {
                    sInfiniteTimeoutBuffs = typeof(BuffInstance).GetField("sBuffsWithSolveCommodityThatHaveInfiniteTimeoutLength", BindingFlags.NonPublic | BindingFlags.Static);
                }

                BuffNames[] infinite = sInfiniteTimeoutBuffs == null ? null : sInfiniteTimeoutBuffs.GetValue(null) as BuffNames[];
                if (infinite != null && Array.IndexOf(infinite, buff.mBuff.BuffGuid) >= 0)
                {
                    return -1f;
                }

                return sim.Motives.CalculateTimeLeft(buff.Guid, commodity);
            }
            catch (Exception)
            {
                return -1f;
            }
        }

        // part A, inventories (InventorySet): the server watches the inventories of the streamed Sims and of the
        // objects within stream.world-radius of them (found again at each resend, about every 2 s; one that is no longer
        // near is dropped) and, each prop poll, sends a watched inventory's set when it changed, and its first set when
        // the watch starts. A few watched sets are sent again at each resend, in turn, for a client that was not
        // streaming that object when it changed. A poll and not the engine's own funnel (Inventory.EventCallback, which
        // AddInternal and RemoveInternal call): Inventory is [Persistable] and that field is not marked DoNotPersist, so a
        // mod delegate in it could go into the server's saves. The Iv line can come one poll after the slot lines of
        // the same change (a book taken out, then carried); the client's carry and its take-out then land in the other
        // order, with the same end state. stream.inventories 0 turns it off.
        private sealed class InventoryWatch
        {
            internal ulong Owner;
            // The count and ids folded, so an unchanged set costs no string.
            internal long Signature;
            // The set last sent; null before the first, "+" while it is too large to send.
            internal string Sent;
            internal int SeenPass;
        }

        private const int InventoryWatchCapacity = 96;
        private const int InventoryResendPerPass = 8;
        private static readonly Dictionary<ulong, InventoryWatch> sInventoryWatches = new Dictionary<ulong, InventoryWatch>();
        private static readonly List<ulong> sInventoryOrder = new List<ulong>();
        private static int sInventoryPass;
        private static int sInventoryResendAt;
        private static long sInventoryLines;
        private static long sInventoryResent;
        private static long sInventoryTooLarge;
        private static long sInventoryFull;
        private static long sInventoryErrors;
        // Cost (DateTime.UtcNow, about 1 ms resolution, so read the means over many polls): polls, sets read, time.
        private static long sInventoryPolls;
        private static long sInventoryReads;
        private static long sInventoryPollTicks;
        private static long sInventoryPollMaxTicks;
        private static long sInventoryFinds;
        private static long sInventoryFindTicks;
        private static long sInventoryFindMaxTicks;

        private static void PublishInventories(Dictionary<ulong, bool> targets, bool resend)
        {
            if (Tunables.Get("stream.inventories", 1f) <= 0f)
            {
                return;
            }

            long started = DateTime.UtcNow.Ticks;
            try
            {
                if (resend)
                {
                    FindInventories(targets);
                    long found = DateTime.UtcNow.Ticks - started;
                    sInventoryFinds++;
                    sInventoryFindTicks += found;
                    sInventoryFindMaxTicks = Math.Max(sInventoryFindMaxTicks, found);
                }

                PublishWatchedInventories(resend);
            }
            finally
            {
                long spent = DateTime.UtcNow.Ticks - started;
                sInventoryPolls++;
                sInventoryPollTicks += spent;
                sInventoryPollMaxTicks = Math.Max(sInventoryPollMaxTicks, spent);
            }
        }

        private static void PublishWatchedInventories(bool resend)
        {
            sInventoryReads += sInventoryWatches.Count;
            List<ulong> gone = new List<ulong>();
            foreach (InventoryWatch watch in sInventoryWatches.Values)
            {
                try
                {
                    if (!PublishInventory(watch, false))
                    {
                        gone.Add(watch.Owner);
                    }
                }
                catch (Exception)
                {
                    sInventoryErrors++;
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                ForgetInventory(gone[i]);
            }

            if (!resend || sInventoryOrder.Count == 0)
            {
                return;
            }

            for (int i = 0; i < InventoryResendPerPass && i < sInventoryOrder.Count; i++)
            {
                if (sInventoryResendAt >= sInventoryOrder.Count)
                {
                    sInventoryResendAt = 0;
                }

                InventoryWatch watch;
                if (sInventoryWatches.TryGetValue(sInventoryOrder[sInventoryResendAt++], out watch))
                {
                    try
                    {
                        PublishInventory(watch, true);
                    }
                    catch (Exception)
                    {
                        sInventoryErrors++;
                    }
                }
            }
        }

        // The streamed Sims' own inventories first, then every object (not a Sim) with an inventory near one of them.
        private static void FindInventories(Dictionary<ulong, bool> targets)
        {
            sInventoryPass++;
            float radius = Tunables.Get("stream.world-radius", 25f);
            List<Sim> sims = new List<Sim>();
            foreach (ulong id in targets.Keys)
            {
                Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                if (sim != null)
                {
                    sims.Add(sim);
                    WatchInventory(sim);
                }
            }

            for (int s = 0; s < sims.Count; s++)
            {
                try
                {
                    GameObject[] near = Sims3.Gameplay.Queries.GetObjects<GameObject>(sims[s].Position, radius);
                    for (int i = 0; near != null && i < near.Length; i++)
                    {
                        // Other Sims' inventories are theirs to show, not this stream's.
                        if (!(near[i] is Sim))
                        {
                            WatchInventory(near[i]);
                        }
                    }
                }
                catch (Exception)
                {
                    sInventoryErrors++;
                }
            }

            List<ulong> away = new List<ulong>();
            foreach (InventoryWatch watch in sInventoryWatches.Values)
            {
                if (watch.SeenPass != sInventoryPass)
                {
                    away.Add(watch.Owner);
                }
            }

            for (int i = 0; i < away.Count; i++)
            {
                ForgetInventory(away[i]);
            }
        }

        private static void WatchInventory(GameObject owner)
        {
            if (owner == null || owner.Inventory == null)
            {
                return;
            }

            ulong id = owner.ObjectId.Value;
            InventoryWatch watch;
            if (!sInventoryWatches.TryGetValue(id, out watch))
            {
                if (sInventoryWatches.Count >= InventoryWatchCapacity)
                {
                    sInventoryFull++;
                    return;
                }

                watch = new InventoryWatch();
                watch.Owner = id;
                sInventoryWatches[id] = watch;
                sInventoryOrder.Add(id);
            }

            watch.SeenPass = sInventoryPass;
        }

        private static void ForgetInventory(ulong owner)
        {
            sInventoryWatches.Remove(owner);
            sInventoryOrder.Remove(owner);
        }

        // False when the owner or its inventory is gone.
        private static bool PublishInventory(InventoryWatch watch, bool resend)
        {
            GameObject owner = new ObjectGuid(watch.Owner).ObjectFromId<GameObject>();
            Inventory inventory = owner == null ? null : owner.Inventory;
            if (inventory == null)
            {
                return false;
            }

            List<IGameObject> items = inventory.FindAll<IGameObject>(false);
            long sum = 0L;
            long mix = 0L;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null)
                {
                    ulong item = items[i].ObjectId.Value;
                    sum = unchecked(sum + (long)item);
                    mix ^= unchecked((long)(item * 0x9E3779B97F4A7C15uL));
                }
            }

            long signature = sum ^ mix ^ items.Count;
            if (!resend && watch.Sent != null && signature == watch.Signature)
            {
                return true;
            }

            watch.Signature = signature;
            if (items.Count > InventorySet.MaxItems)
            {
                if (watch.Sent != "+")
                {
                    sInventoryTooLarge++;
                    watch.Sent = "+";
                }

                return true;
            }

            List<ulong> ids = new List<ulong>(items.Count);
            List<string> types = new List<string>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null)
                {
                    ids.Add(items[i].ObjectId.Value);
                    types.Add(items[i].GetType().Name);
                }
            }

            string typeList;
            string membership = InventorySet.Membership(ids, types, out typeList);
            if (!resend && membership == watch.Sent)
            {
                return true;
            }

            watch.Sent = membership;
            SacsRecorder.RingLine(InventorySet.Line(watch.Owner, membership, typeList));
            if (resend)
            {
                sInventoryResent++;
            }
            else
            {
                sInventoryLines++;
            }

            return true;
        }

        // DIAG_STATE inventories=: watched/lines/resent/tooLarge/full/errors/polls:N/reads:<sets a poll>/ms:<mean>,<max>
        // /findMs:<mean>,<max> (the search for owners, at each resend; included in ms).
        private static string InventoryReport()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return sInventoryWatches.Count.ToString(c) + "/" + sInventoryLines.ToString(c) + "/" + sInventoryResent.ToString(c) + "/"
                + sInventoryTooLarge.ToString(c) + "/" + sInventoryFull.ToString(c) + "/" + sInventoryErrors.ToString(c)
                + "/polls:" + sInventoryPolls.ToString(c) + "/reads:" + Mean(sInventoryReads, sInventoryPolls, 1.0).ToString("0.#", c)
                + "/ms:" + Mean(sInventoryPollTicks, sInventoryPolls, 10000.0).ToString("0.###", c) + "," + (sInventoryPollMaxTicks / 10000.0).ToString("0.#", c)
                + "/findMs:" + Mean(sInventoryFindTicks, sInventoryFinds, 10000.0).ToString("0.###", c) + "," + (sInventoryFindMaxTicks / 10000.0).ToString("0.#", c);
        }

        private static double Mean(long total, long count, double unit)
        {
            return count == 0 ? 0.0 : total / unit / count;
        }

        // part A, object use (ObjectUse): who uses an object, for a client's own menu tests (InUse, a part's
        // CanBeUsedBy). The server watches the objects the streamed Sims use: each prop poll their posture's container
        // and their current interaction's target, and at each resend (about every 2 s) any object within
        // stream.world-radius of them that has a user (GameObject.ActorsUsingMe, or a part's ContainedSim; a Sim parent
        // is the slot lines' to show). Each poll it sends a watched object's users when they changed, and a few again at
        // each resend, in turn. An object that has nobody left is sent once with none and then dropped at the next search,
        // as is one no longer near. A poll and not the engine's own funnel: AddToUseList and RemoveFromUseList are
        // virtual with overrides (Computer, BunkBedBunk, ServingContainer, ...) and the part callback
        // (OnPartAvailabilityChangedCallback) is per object, so neither is one place. stream.use 0 turns it off.
        private sealed class UseWatch
        {
            internal ulong Object;
            // The line last sent; null before the first.
            internal string Sent;
            internal int SeenPass;
        }

        private const int UseWatchCapacity = 64;
        private const int UseResendPerPass = 8;
        private static readonly Dictionary<ulong, UseWatch> sUseWatches = new Dictionary<ulong, UseWatch>();
        private static readonly List<ulong> sUseOrder = new List<ulong>();
        private static int sUsePass;
        private static int sUseResendAt;
        private static long sUseLines;
        private static long sUseResent;
        private static long sUseFull;
        private static long sUseErrors;
        private static long sUsePolls;
        private static long sUsePollTicks;
        private static long sUsePollMaxTicks;
        private static string sUseLast = "-"; // object type:users,parts of the last line sent

        private static void PublishUses(Dictionary<ulong, bool> targets, bool resend)
        {
            if (Tunables.Get("stream.use", 1f) <= 0f)
            {
                return;
            }

            long started = DateTime.UtcNow.Ticks;
            try
            {
                foreach (ulong id in targets.Keys)
                {
                    try
                    {
                        Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                        if (sim == null)
                        {
                            continue;
                        }

                        WatchUse(sim.Posture == null ? null : sim.Posture.Container as GameObject, false);
                        InteractionInstance current = sim.CurrentInteraction;
                        WatchUse(current == null ? null : current.Target as GameObject, false);
                    }
                    catch (Exception)
                    {
                        sUseErrors++;
                    }
                }

                if (resend)
                {
                    FindUses(targets);
                }

                List<ulong> gone = new List<ulong>();
                foreach (UseWatch watch in sUseWatches.Values)
                {
                    try
                    {
                        if (!PublishUse(watch, false))
                        {
                            gone.Add(watch.Object);
                        }
                    }
                    catch (Exception)
                    {
                        sUseErrors++;
                    }
                }

                for (int i = 0; i < gone.Count; i++)
                {
                    ForgetUse(gone[i]);
                }

                for (int i = 0; resend && i < UseResendPerPass && i < sUseOrder.Count; i++)
                {
                    if (sUseResendAt >= sUseOrder.Count)
                    {
                        sUseResendAt = 0;
                    }

                    UseWatch watch;
                    if (sUseWatches.TryGetValue(sUseOrder[sUseResendAt++], out watch))
                    {
                        try
                        {
                            PublishUse(watch, true);
                        }
                        catch (Exception)
                        {
                            sUseErrors++;
                        }
                    }
                }
            }
            finally
            {
                long spent = DateTime.UtcNow.Ticks - started;
                sUsePolls++;
                sUsePollTicks += spent;
                sUsePollMaxTicks = Math.Max(sUsePollMaxTicks, spent);
            }
        }

        // At each resend: objects near the streamed Sims that someone uses come under watch; a watched object stays
        // while it is near and either used or last sent with users (its "none" has not gone out yet).
        private static void FindUses(Dictionary<ulong, bool> targets)
        {
            sUsePass++;
            float radius = Tunables.Get("stream.world-radius", 25f);
            Dictionary<ulong, bool> near = new Dictionary<ulong, bool>();
            foreach (ulong id in targets.Keys)
            {
                try
                {
                    Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                    GameObject[] found = sim == null ? null : Sims3.Gameplay.Queries.GetObjects<GameObject>(sim.Position, radius);
                    for (int i = 0; found != null && i < found.Length; i++)
                    {
                        if (found[i] == null || found[i] is Sim)
                        {
                            continue;
                        }

                        near[found[i].ObjectId.Value] = true;
                        if (HasUsers(found[i]))
                        {
                            WatchUse(found[i], true);
                        }
                    }
                }
                catch (Exception)
                {
                    sUseErrors++;
                }
            }

            List<ulong> away = new List<ulong>();
            foreach (UseWatch watch in sUseWatches.Values)
            {
                if (!near.ContainsKey(watch.Object) || (watch.SeenPass != sUsePass && watch.Sent != null && watch.Sent.EndsWith("|-|-")))
                {
                    away.Add(watch.Object);
                }
            }

            for (int i = 0; i < away.Count; i++)
            {
                ForgetUse(away[i]);
            }
        }

        private static bool HasUsers(GameObject obj)
        {
            if (obj.UseCount > 0)
            {
                return true;
            }

            PartComponent parts = obj.PartComponent;
            if (parts != null && parts.PartDataList != null)
            {
                foreach (PartData part in parts.PartDataList.Values)
                {
                    if (part != null && part.ContainedSim != null)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // seen: found used by the search (keeps the watch past this pass); a Sim's container or target is watched
        // without that, so it is dropped at the next search once nobody uses it.
        private static void WatchUse(GameObject obj, bool seen)
        {
            if (obj == null || obj is Sim)
            {
                return;
            }

            ulong id = obj.ObjectId.Value;
            UseWatch watch;
            if (!sUseWatches.TryGetValue(id, out watch))
            {
                if (sUseWatches.Count >= UseWatchCapacity)
                {
                    sUseFull++;
                    return;
                }

                watch = new UseWatch();
                watch.Object = id;
                watch.SeenPass = sUsePass - 1;
                sUseWatches[id] = watch;
                sUseOrder.Add(id);
            }

            if (seen)
            {
                watch.SeenPass = sUsePass;
            }
        }

        private static void ForgetUse(ulong obj)
        {
            sUseWatches.Remove(obj);
            sUseOrder.Remove(obj);
        }

        // False when the object is gone.
        private static bool PublishUse(UseWatch watch, bool resend)
        {
            GameObject obj = new ObjectGuid(watch.Object).ObjectFromId<GameObject>();
            if (obj == null)
            {
                return false;
            }

            List<ulong> users = new List<ulong>();
            List<Sim> actors = obj.ActorsUsingMe;
            for (int i = 0; actors != null && i < actors.Count; i++)
            {
                if (actors[i] != null)
                {
                    users.Add(actors[i].ObjectId.Value);
                }
            }

            List<uint> areas = new List<uint>();
            List<ulong> partSims = new List<ulong>();
            PartComponent parts = obj.PartComponent;
            if (parts != null && parts.PartDataList != null)
            {
                foreach (KeyValuePair<PartArea, PartData> part in parts.PartDataList)
                {
                    Sim sim = part.Value == null ? null : part.Value.ContainedSim;
                    if (sim != null)
                    {
                        areas.Add((uint)(int)part.Key);
                        partSims.Add(sim.ObjectId.Value);
                    }
                }
            }

            // A watch that has never sent and has nobody sends nothing: the client's copy has no mirrored users either.
            string line = ObjectUse.Line(watch.Object, users, areas, partSims);
            if (watch.Sent == null && users.Count == 0 && partSims.Count == 0)
            {
                watch.Sent = line;
                return true;
            }

            if (!resend && line == watch.Sent)
            {
                return true;
            }

            watch.Sent = line;
            SacsRecorder.RingLine(line);
            sUseLast = obj.GetType().Name + ":" + users.Count.ToString(CultureInfo.InvariantCulture) + "," + partSims.Count.ToString(CultureInfo.InvariantCulture);
            if (resend)
            {
                sUseResent++;
            }
            else
            {
                sUseLines++;
            }

            return true;
        }

        // DIAG_STATE uses=: watched/lines/resent/full/errors/polls:N/ms:<mean>,<max>/last:<type>:<users>,<parts>
        private static string UseReport()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return sUseWatches.Count.ToString(c) + "/" + sUseLines.ToString(c) + "/" + sUseResent.ToString(c) + "/" + sUseFull.ToString(c)
                + "/" + sUseErrors.ToString(c) + "/polls:" + sUsePolls.ToString(c)
                + "/ms:" + Mean(sUsePollTicks, sUsePolls, 10000.0).ToString("0.###", c) + "," + (sUsePollMaxTicks / 10000.0).ToString("0.#", c)
                + "/last:" + sUseLast;
        }

        // Relationships (2026-10-14): the HUD's relationship item reads the client's own: its bar (liking) and its line
        // ("Pablo thinks Jennifer is being flirty", the conversation's short-term context). Server, every prop poll, for
        // each pair of streamed Sims with a relationship, on change and every 10 s:
        //   Rl|simA|simB|liking|stc|commodity|commodityValue|perceivedSim|ltr|bits|stateStartedTicks|highestLiking|lastGreetedTicks|lastTalkedTicks
        // (2026-10-22) added the long-term fields (RelationshipState.cs): the pie menu reads the long-term state,
        // which the engine derives from liking and the interaction bits, and when the pair last greeted and talked. The
        // poll's diff is the one seam: it reads the
        // Relationship itself, so every path that changes it (socials, cheats, CopyLtr, story progression) is sent.
        private static readonly Dictionary<string, string> sPublishedRelationships = new Dictionary<string, string>();
        private static FieldInfo sStcField;
        private static FieldInfo sStcCommodityField;
        private static FieldInfo sStcValueField;
        private static FieldInfo sStcPerceivedField;
        private static FieldInfo sLtrLikingField;
        private static FieldInfo sLtrStateField;
        private static FieldInfo sLtrBitsField;
        private static FieldInfo sLtrStartedField;
        private static FieldInfo sLtrHighestField;
        private static long sRelationshipsSet;
        private static long sRelationshipStatesSet;

        private static void ResolveLtrFields()
        {
            if (sLtrLikingField != null)
            {
                return;
            }

            BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            sLtrStateField = typeof(LongTermRelationship).GetField("mCurrentLtr", any);
            sLtrBitsField = typeof(LongTermRelationship).GetField("mInteraction", any);
            sLtrStartedField = typeof(LongTermRelationship).GetField("mWhenStateStarted", any);
            sLtrHighestField = typeof(LongTermRelationship).GetField("mHighestLiking", any);
            sLtrLikingField = typeof(LongTermRelationship).GetField("mLiking", any);
        }

        private static void ResolveStcFields()
        {
            if (sStcField != null)
            {
                return;
            }

            BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            sStcCommodityField = typeof(ShortTermContext).GetField("mCurrentCommodityType", any);
            sStcValueField = typeof(ShortTermContext).GetField("mCurrentCommodityValue", any);
            sStcPerceivedField = typeof(ShortTermContext).GetField("mPerceivedSim", any);
            sStcField = typeof(ShortTermContext).GetField("mCurrentStc", any);
        }

        private static void PublishRelationships(Dictionary<ulong, bool> targets, bool resend)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            List<Sim> sims = new List<Sim>();
            foreach (ulong id in targets.Keys)
            {
                Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                if (sim != null && sim.SimDescription != null)
                {
                    sims.Add(sim);
                }
            }

            for (int i = 0; i < sims.Count; i++)
            {
                for (int j = i + 1; j < sims.Count; j++)
                {
                    try
                    {
                        Relationship r = Relationship.Get(sims[i], sims[j], false);
                        if (r == null || r.LTR == null || r.STC == null)
                        {
                            continue;
                        }

                        SimDescription perceived = r.STC.PerceivedSim;
                        RelationshipState s = new RelationshipState();
                        s.SimA = sims[i].ObjectId.Value;
                        s.SimB = sims[j].ObjectId.Value;
                        s.Liking = r.LTR.Liking;
                        s.Stc = (int)r.STC.CurrentSTC;
                        s.Commodity = (int)r.STC.CurrentCommodity;
                        s.CommodityValue = r.STC.CurrentCommodityValue;
                        s.PerceivedSim = perceived != null && perceived.CreatedSim != null ? perceived.CreatedSim.ObjectId.Value : 0uL;
                        s.HasLtr = true;
                        s.Ltr = (int)r.LTR.CurrentLTR;
                        s.Bits = (uint)r.LTR.LTRInteractionBits;
                        s.StateStartedTicks = r.LTR.WhenStateStarted.Ticks;
                        s.HighestLiking = r.LTR.HighestLiking;
                        s.LastGreetedTicks = r.WhenLastGreeted.Ticks;
                        s.LastTalkedTicks = r.WhenLastTalked.Ticks;
                        InformationLearnedAboutSim infoA = r.InformationAbout(sims[j]);
                        InformationLearnedAboutSim infoB = r.InformationAbout(sims[i]);
                        if (infoA != null && infoA.Traits != null)
                        {
                            foreach (TraitNames tn in infoA.Traits)
                            {
                                s.TraitsA.Add((ulong)tn);
                            }
                        }
                        if (infoB != null && infoB.Traits != null)
                        {
                            foreach (TraitNames tn in infoB.Traits)
                            {
                                s.TraitsB.Add((ulong)tn);
                            }
                        }
                        s.HasTraits = true;
                        string line = s.Line();
                        string key = s.SimA.ToString(c) + "|" + s.SimB.ToString(c);
                        string last;
                        if (!resend && sPublishedRelationships.TryGetValue(key, out last) && last == line)
                        {
                            continue;
                        }

                        sPublishedRelationships[key] = line;
                        SacsRecorder.RingLine(line);
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }
                }
            }
        }

        private static bool ReplayRelationship(string[] f)
        {
            RelationshipState s;
            if (!RelationshipState.Parse(f, out s))
            {
                return false;
            }

            Sim a = ReplayObject(s.SimA).ObjectFromId<Sim>();
            Sim b = ReplayObject(s.SimB).ObjectFromId<Sim>();
            if (a == null || b == null)
            {
                return false;
            }

            try
            {
                Relationship r = Relationship.Get(a, b, true);
                if (r == null || r.LTR == null || r.STC == null)
                {
                    return false;
                }

                ResolveLtrFields();
                bool ltrFields = sLtrLikingField != null && sLtrStateField != null && sLtrBitsField != null && sLtrStartedField != null && sLtrHighestField != null;
                if (s.HasLtr && ltrFields)
                {
                    // the server's long-term state as the engine's CopyLtr (LongTermRelationship.cs:764) copies it,
                    // without UpdateLTR: no re-derivation, no ChangeState effects (buffs, partner, charisma, a random BFF)
                    // on a game that only shows the pair. With the bits and start time here, a later local UpdateLTR
                    // derives the server's state again.
                    bool changed = Math.Abs(r.LTR.Liking - s.Liking) > 0.05f || (int)r.LTR.CurrentLTR != s.Ltr
                        || (uint)r.LTR.LTRInteractionBits != s.Bits || r.LTR.WhenStateStarted.Ticks != s.StateStartedTicks
                        || r.WhenLastGreeted.Ticks != s.LastGreetedTicks || r.WhenLastTalked.Ticks != s.LastTalkedTicks;
                    if (changed)
                    {
                        sLtrLikingField.SetValue(r.LTR, Math.Max(-100f, Math.Min(100f, s.Liking)));
                        sLtrHighestField.SetValue(r.LTR, s.HighestLiking);
                        sLtrBitsField.SetValue(r.LTR, Enum.ToObject(sLtrBitsField.FieldType, s.Bits));
                        sLtrStateField.SetValue(r.LTR, Enum.ToObject(sLtrStateField.FieldType, s.Ltr));
                        sLtrStartedField.SetValue(r.LTR, new DateAndTime(s.StateStartedTicks));
                        // The menu's greeting and new-conversation tests (Sim.NeedsToBeGreeted, GetAllInteractionsForPieMenu).
                        r.WhenLastGreeted = new DateAndTime(s.LastGreetedTicks);
                        r.WhenLastTalked = new DateAndTime(s.LastTalkedTicks);
                        sRelationshipStatesSet++;
                        if (a.IsSelectable || b.IsSelectable)
                        {
                            r.UpdateSocializingUI(); // only a pair with this client's own Sim has a HUD to refresh
                        }
                    }
                }
                else if (Math.Abs(r.LTR.Liking - s.Liking) > 0.05f)
                {
                    r.LTR.SetLiking(s.Liking);
                }

                ResolveStcFields();
                if (sStcField != null && sStcCommodityField != null && sStcValueField != null && sStcPerceivedField != null)
                {
                    sStcField.SetValue(r.STC, Enum.ToObject(sStcField.FieldType, s.Stc));
                    sStcCommodityField.SetValue(r.STC, Enum.ToObject(sStcCommodityField.FieldType, s.Commodity));
                    sStcValueField.SetValue(r.STC, s.CommodityValue);
                    Sim perceivedSim = s.PerceivedSim == 0uL ? null : ReplayObject(s.PerceivedSim).ObjectFromId<Sim>();
                    sStcPerceivedField.SetValue(r.STC, perceivedSim == null ? null : perceivedSim.SimDescription);
                }

                if (s.HasTraits)
                {
                    ResolveInfoLearnedFields();
                    if (sInfoTraitsField != null)
                    {
                        InformationLearnedAboutSim infoA = r.InformationAbout(b);
                        InformationLearnedAboutSim infoB = r.InformationAbout(a);
                        ApplyTraitsKnown(infoA, s.TraitsA);
                        ApplyTraitsKnown(infoB, s.TraitsB);
                    }
                }

                sRelationshipsSet++;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static FieldInfo sInfoTraitsField;

        private static void ResolveInfoLearnedFields()
        {
            if (sInfoTraitsField != null)
            {
                return;
            }

            BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            sInfoTraitsField = typeof(InformationLearnedAboutSim).GetField("TraitsKnown", any);
        }

        private static void ApplyTraitsKnown(InformationLearnedAboutSim info, List<ulong> traits)
        {
            if (info == null || sInfoTraitsField == null)
            {
                return;
            }

            List<TraitNames> list = sInfoTraitsField.GetValue(info) as List<TraitNames>;
            if (list == null)
            {
                return;
            }

            list.Clear();
            if (traits != null)
            {
                for (int i = 0; i < traits.Count; i++)
                {
                    list.Add((TraitNames)traits[i]);
                }
            }
        }

        // Published conversations, keyed by their whole membership (ConversationPublisher).
        private static readonly ConversationPublisher sConversationPublisher = new ConversationPublisher();
        private static long sConversationsSet;

        // one pass reads every target Sim's conversation; the publisher sends the end of each membership no
        // longer talking before any start or change, so a Sim that left A-B for A-C, or a third member who left A-B-C, is
        // ended before the new membership starts.
        private static void PublishConversations(Dictionary<ulong, bool> targets, bool resend)
        {
            List<ConversationState> active = new List<ConversationState>();
            Dictionary<ulong, bool> unreadable = new Dictionary<ulong, bool>();
            ReadTargetConversations(targets.Keys, active, unreadable);
            List<string> lines = sConversationPublisher.Pass(active, targets.Keys, unreadable.Keys, resend);
            for (int i = 0; i < lines.Count; i++)
            {
                SacsRecorder.RingLine(lines[i]);
            }
        }

        // n1: the one read of the targets' conversations, for the publisher and the late-join snapshot
        // (BoundaryState.Lines), so both say the same of every target (ConversationState.Talking). Each conversation of two
        // or more goes into active once. A target this game cannot read goes into unreadable and is never said to be
        // absent: no Sim, SimDescription or SocialComponent, or a conversation of two or more whose read threw or named
        // no Sim. A conversation's members read fine say the rest of its targets; a failed one makes them unreadable too.
        internal static void ReadTargetConversations(ICollection<ulong> targets, List<ConversationState> active,
            Dictionary<ulong, bool> unreadable)
        {
            Dictionary<Conversation, bool> readOk = new Dictionary<Conversation, bool>();
            foreach (ulong id in targets)
            {
                try
                {
                    Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                    if (sim == null || sim.SimDescription == null || sim.SocialComponent == null)
                    {
                        unreadable[id] = true;
                        continue;
                    }

                    Conversation conv = sim.SocialComponent.Conversation;
                    if (conv == null)
                    {
                        continue;
                    }

                    bool ok;
                    if (readOk.TryGetValue(conv, out ok))
                    {
                        if (!ok)
                        {
                            unreadable[id] = true;
                        }

                        continue;
                    }

                    readOk[conv] = false;
                    ConversationState cs = ReadConversation(conv);
                    if (cs != null)
                    {
                        active.Add(cs);
                    }
                    else if (conv.Members != null && conv.Members.Count >= 2)
                    {
                        unreadable[id] = true;
                        continue;
                    }

                    readOk[conv] = true;
                }
                catch (Exception)
                {
                    unreadable[id] = true;
                    SwallowedErrors.Note();
                }
            }
        }

        // The server's conversation as a Cv state: the two lowest-listed members as the pair (lower id first), the rest
        // as extra members, the turns and the active topics. Null when it has fewer than two members.
        private static ConversationState ReadConversation(Conversation conv)
        {
            ICollection<Sim> members = conv.Members;
            if (members == null || members.Count < 2)
            {
                return null;
            }

            List<Sim> memberList = new List<Sim>(members);
            Sim a = memberList[0];
            Sim b = memberList[1];
            if (a == null || b == null)
            {
                return null;
            }

            ConversationState cs = new ConversationState();
            cs.SimA = Math.Min(a.ObjectId.Value, b.ObjectId.Value);
            cs.SimB = Math.Max(a.ObjectId.Value, b.ObjectId.Value);
            cs.InConversation = true;
            cs.WhoseTurn = conv.WhoseTurn != null ? conv.WhoseTurn.ObjectId.Value : cs.SimA;
            cs.WhoTalkedLast = conv.WhoTalkedLast != null ? conv.WhoTalkedLast.ObjectId.Value : 0uL;
            for (int m = 2; m < memberList.Count; m++)
            {
                if (memberList[m] != null)
                {
                    cs.ExtraMembers.Add(memberList[m].ObjectId.Value);
                }
            }

            if (conv.ActiveTopics != null)
            {
                foreach (ActiveTopic at in conv.ActiveTopics)
                {
                    if (at == null || string.IsNullOrEmpty(at.Type))
                    {
                        continue;
                    }

                    ulong paramId = at.Parameter != null && at.Parameter.CreatedSim != null ? at.Parameter.CreatedSim.ObjectId.Value : 0uL;
                    ulong activeId = at.Active != null && at.Active.CreatedSim != null ? at.Active.CreatedSim.ObjectId.Value : 0uL;
                    if (!cs.AddTopic(at.Type, paramId, activeId))
                    {
                        SwallowedErrors.Note();
                    }
                }
            }

            return cs;
        }

        private static ConstructorInfo sConvCtor;
        private static FieldInfo sConvMembersField;
        private static FieldInfo sConvWhoseTurnField;
        private static FieldInfo sConvWhoTalkedLastField;
        private static FieldInfo sConvActiveTopicsField;

        private static void ResolveConversationFields()
        {
            if (sConvCtor != null)
            {
                return;
            }

            BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            sConvCtor = typeof(Conversation).GetConstructor(any, null, Type.EmptyTypes, null);
            sConvMembersField = typeof(Conversation).GetField("mMembers", any);
            sConvWhoseTurnField = typeof(Conversation).GetField("mWhoseTurn", any);
            sConvWhoTalkedLastField = typeof(Conversation).GetField("mWhoTalkedLast", any);
            sConvActiveTopicsField = typeof(Conversation).GetField("mActiveTopics", any);
        }

        private static bool ReplayConversation(string[] f)
        {
            ConversationState s;
            if (!ConversationState.Parse(f, out s))
            {
                return false;
            }

            // The line's members as this game's copies (the pair first); a member this game does not have is left out.
            List<Sim> want = new List<Sim>();
            List<ulong> wantIds = new List<ulong>();
            List<ulong> serverIds = s.Members();
            for (int i = 0; i < serverIds.Count; i++)
            {
                Sim member = ReplayObject(serverIds[i]).ObjectFromId<Sim>();
                if (member != null && member.SocialComponent != null && !want.Contains(member))
                {
                    want.Add(member);
                    wantIds.Add(member.ObjectId.Value);
                }
            }

            Sim a = ReplayObject(s.SimA).ObjectFromId<Sim>();
            Sim b = ReplayObject(s.SimB).ObjectFromId<Sim>();
            try
            {
                if (s.IsAbsence)
                {
                    // the server's Sim holds no conversation, so this copy holds none either: one its save
                    // loaded (the engine's own) or one the stream built that no end line will ever reach.
                    DetachAbsent(a);
                    sConversationsSet++;
                    return true;
                }

                if (!s.InConversation)
                {
                    // Only a conversation that still holds the other half of the ended pair goes: a Sim that already
                    // moved on (A-B ended, A-C started) keeps its new one. Each member on its own: one this game no
                    // longer has does not keep the others in the ended conversation.
                    ulong aId = a != null ? a.ObjectId.Value : 0uL;
                    ulong bId = b != null ? b.ObjectId.Value : 0uL;
                    for (int i = 0; i < want.Count; i++)
                    {
                        EndSimConversation(want[i], aId, bId);
                    }

                    sConversationsSet++;
                    return true;
                }

                if (a == null || b == null)
                {
                    return false;
                }

                ResolveConversationFields();
                if (sConvCtor == null || sConvMembersField == null || sConvWhoseTurnField == null || sConvActiveTopicsField == null)
                {
                    return false;
                }

                // Every conversation a member of this line holds now: one of the stream's own with exactly these members
                // is kept (a turn or topic change); from every other, each Sim not in this line that still holds it is
                // detached, so nobody keeps a conversation that dropped it (review wf_842dd179-926 finding 1). The
                // engine's own conversations (a SocialGroupSituation) are never rewritten here.
                Conversation conv = null;
                List<Conversation> held = new List<Conversation>();
                for (int i = 0; i < want.Count; i++)
                {
                    Conversation old = want[i].SocialComponent.Conversation;
                    if (old != null && !held.Contains(old))
                    {
                        held.Add(old);
                    }
                }

                for (int h = 0; h < held.Count; h++)
                {
                    List<Sim> oldMembers = new List<Sim>(held[h].Members);
                    List<ulong> oldIds = new List<ulong>();
                    for (int i = 0; i < oldMembers.Count; i++)
                    {
                        if (oldMembers[i] != null)
                        {
                            oldIds.Add(oldMembers[i].ObjectId.Value);
                        }
                    }

                    if (conv == null && held[h].SocialGroupSituation == null && ConversationState.SameMembers(wantIds, oldIds))
                    {
                        conv = held[h];
                        continue;
                    }

                    List<ulong> gone = ConversationState.Leavers(wantIds, oldIds);
                    for (int i = 0; i < oldMembers.Count; i++)
                    {
                        Sim leaver = oldMembers[i];
                        if (leaver != null && gone.Contains(leaver.ObjectId.Value) && leaver.SocialComponent != null
                            && leaver.SocialComponent.Conversation == held[h])
                        {
                            DetachConversation(leaver);
                        }
                    }
                }

                if (conv == null)
                {
                    conv = (Conversation)sConvCtor.Invoke(null);
                }

                List<Sim> members = sConvMembersField.GetValue(conv) as List<Sim>;
                if (members != null)
                {
                    members.Clear();
                    members.AddRange(want);
                }

                Sim whoseTurn = s.WhoseTurn != 0uL ? ReplayObject(s.WhoseTurn).ObjectFromId<Sim>() : a;
                sConvWhoseTurnField.SetValue(conv, whoseTurn);

                if (sConvWhoTalkedLastField != null)
                {
                    Sim whoTalked = s.WhoTalkedLast != 0uL ? ReplayObject(s.WhoTalkedLast).ObjectFromId<Sim>() : null;
                    sConvWhoTalkedLastField.SetValue(conv, whoTalked);
                }

                List<ActiveTopic> topics = sConvActiveTopicsField.GetValue(conv) as List<ActiveTopic>;
                if (topics != null)
                {
                    topics.Clear();
                    if (s.Topics != null)
                    {
                        for (int i = 0; i < s.Topics.Count; i++)
                        {
                            string entry = s.Topics[i];
                            if (string.IsNullOrEmpty(entry))
                            {
                                continue;
                            }

                            string[] parts = entry.Split(':');
                            string topicType = parts[0];
                            SimDescription paramDesc = null;
                            SimDescription activeDesc = null;
                            ulong paramId;
                            ulong activeId;
                            if (parts.Length > 1 && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out paramId) && paramId != 0uL)
                            {
                                Sim pSim = ReplayObject(paramId).ObjectFromId<Sim>();
                                if (pSim != null)
                                {
                                    paramDesc = pSim.SimDescription;
                                }
                            }

                            if (parts.Length > 2 && ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out activeId) && activeId != 0uL)
                            {
                                Sim actSim = ReplayObject(activeId).ObjectFromId<Sim>();
                                if (actSim != null)
                                {
                                    activeDesc = actSim.SimDescription;
                                }
                            }

                            ActiveTopic at;
                            if (paramDesc != null || activeDesc != null)
                            {
                                at = new ActiveTopic(topicType, paramDesc, activeDesc);
                            }
                            else
                            {
                                at = new ActiveTopic(topicType);
                            }
                            topics.Add(at);
                        }
                    }
                }

                for (int i = 0; i < want.Count; i++)
                {
                    AttachConversation(want[i], conv);
                }

                sConversationsSet++;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void AttachConversation(Sim sim, Conversation conv)
        {
            if (sim != null && sim.SocialComponent != null)
            {
                sim.SocialComponent.Conversation = conv;
                if (sim.IsSelectable)
                {
                    sim.SocialComponent.UpdateSocializingUI();
                }
            }
        }

        // Ends sim's conversation when it is the ended pair's (this game's ids a and b): it holds a or b besides sim itself.
        private static void EndSimConversation(Sim sim, ulong a, ulong b)
        {
            Conversation conv = sim != null && sim.SocialComponent != null ? sim.SocialComponent.Conversation : null;
            if (conv == null || conv.Members == null)
            {
                return;
            }

            List<ulong> held = new List<ulong>();
            foreach (Sim member in conv.Members)
            {
                if (member != null)
                {
                    held.Add(member.ObjectId.Value);
                }
            }

            if (ConversationState.HoldsOtherHalf(sim.ObjectId.Value, held, a, b))
            {
                DetachConversation(sim);
            }
        }

        private static long sAbsenceDetached;
        private static string sAbsenceLast = "-";

        // An absence line's detach: whatever sim holds, counted, with who and what it held (absenceLast) for the reports.
        private static void DetachAbsent(Sim sim)
        {
            Conversation conv = sim != null && sim.SocialComponent != null ? sim.SocialComponent.Conversation : null;
            if (conv == null)
            {
                return;
            }

            List<string> names = new List<string>();
            Dictionary<ulong, Sim> holders = new Dictionary<ulong, Sim>();
            if (conv.Members != null)
            {
                foreach (Sim member in conv.Members)
                {
                    names.Add(member != null ? member.FirstName : "null");
                    if (member != null && member.SocialComponent != null && member.SocialComponent.Conversation == conv)
                    {
                        holders[member.ObjectId.Value] = member;
                    }
                }
            }

            names.Sort(StringComparer.Ordinal);
            holders[sim.ObjectId.Value] = sim;
            DetachConversation(sim);
            sAbsenceDetached++;
            string left = string.Empty;
            List<ulong> alone = ConversationState.LeftAlone(sim.ObjectId.Value, holders.Keys);
            for (int i = 0; i < alone.Count; i++)
            {
                DetachConversation(holders[alone[i]]);
                sAbsenceDetached++;
                left += (left.Length == 0 ? "+left:" : "+") + holders[alone[i]].FirstName;
            }

            sAbsenceLast = sim.FirstName + "=" + string.Join("+", names.ToArray()) + (conv.SocialGroupSituation != null ? "(engine)" : "(stream)") + left;
        }

        // The stream's own detach: the field only, as AttachConversation sets it (SocialComponent.SetConversation would run
        // the socializing posture's exit on the copy).
        private static void DetachConversation(Sim sim)
        {
            sim.SocialComponent.Conversation = null;
            if (sim.IsSelectable)
            {
                sim.SocialComponent.UpdateSocializingUI();
            }
        }
        private static string ExtrasReport()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "relationships:" + sRelationshipsSet.ToString(c) + "/states:" + sRelationshipStatesSet.ToString(c) + "/convs:" + sConversationsSet.ToString(c) + "/leaveIntentsSent:" + sLeaveIntentsSent.ToString(c)
                + "/absenceDetached:" + sAbsenceDetached.ToString(c) + "/absenceLast:" + sAbsenceLast
                + ",placements:" + sPlacementsApplied.ToString(c) + "/failed:" + sPlacementsFailed.ToString(c)
                + ",simulator:" + (SimulatorRecorder.Installed ? "on" : "off") + "/creates:" + SimulatorRecorder.Creates.ToString(c) + "/kept:" + SimulatorRecorder.Count.ToString(c)
                + "/scriptlessCopies:" + sScriptlessCreated.ToString(c) + ",route:" + RouteRecorder.Report().Replace(',', '/')
                + ",events:" + EventQueueRecorder.Report().Replace(',', '/') + "/notes:" + SacsRecorder.EventNotes.ToString(c)
                + ",catchup:" + StateCatchUp.Report().Replace(',', '/')
                + ",world:standIns:" + sStandIns.Count.ToString(c) + "/made:" + sStandInsMade.ToString(c) + "/effects:" + sStandInEffects.ToString(c)
                + "/spawnsShown:" + sSpawnsShown.ToString(c);
        }

        // Client: make this client's copy of the Sim hold what the server's holds.
        private static long sHandFixes;

        private static bool ReconcileHands(string[] f)
        {
            if (f.Length < 7)
            {
                return false;
            }

            ObjectGuid simId = ReplayObject(ulong.Parse(f[2], CultureInfo.InvariantCulture));
            if (!simId.IsValid)
            {
                return false;
            }

            for (int i = 0; i < sHandSlots.Length; i++)
            {
                ulong serverHeld = ulong.Parse(f[3 + i], CultureInfo.InvariantCulture);
                ObjectGuid expected = serverHeld == 0uL ? ObjectGuid.InvalidObjectGuid : ReplayObject(serverHeld);
                ObjectGuid current = Slots.GetContainedObject(simId, sHandSlots[i]);
                bool same = current.IsValid && expected.IsValid && current.Value == expected.Value;
                if (same || (!current.IsValid && !expected.IsValid))
                {
                    continue;
                }

                if (current.IsValid)
                {
                    Slots.DetachFromSlot(current);
                    string placed;
                    if (sClientPlacements.TryGetValue(current.Value, out placed))
                    {
                        // the server's is on a table or counter: put it there (hiding it made a plate flicker)
                        string[] p = placed.Split('|');
                        ApplyPlacement(current, new ObjectGuid(ulong.Parse(p[0], CultureInfo.InvariantCulture)), uint.Parse(p[1], CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        World.ObjectSetHiddenFlags(current, uint.MaxValue);
                    }

                    sHandFixes++;
                }

                if (expected.IsValid)
                {
                    Slots.DetachFromSlot(expected);
                    Slots.AttachToSlot(expected, simId, sHandSlots[i], true, false);
                    World.ObjectSetHiddenFlags(expected, 0u);
                    sHandFixes++;
                }
            }

            return true;
        }

        private static void AddHeldProp(GameObject prop)
        {
            if (prop != null && !(prop is Sim) && !SacsRecorder.Props.ContainsKey(prop.ObjectId.Value) && SacsRecorder.Props.Count < 256)
            {
                SacsRecorder.Props[prop.ObjectId.Value] = true;
            }
        }

        // object identity (docs/engine/object-identity.md).
        // Server: what a client needs to make its own copy of a streamed object.
        private static string DescribeStreamObject(ulong id)
        {
            GameObject obj = new ObjectGuid(id).ObjectFromId<GameObject>();
            if (obj == null)
            {
                // No script object (a prop from GlobalFunctions.CreateProp: the wooden spoon, a utensil): described by the
                // prototype key the object-creation boundary saw it made from (SimulatorRecorder).
                ResourceKey made;
                if (Sims3.SimIFace.Objects.IsValid(new ObjectGuid(id)) && SimulatorRecorder.TryGetKey(id, out made))
                {
                    CultureInfo k = CultureInfo.InvariantCulture;
                    return ScriptlessType + "|" + made.InstanceId.ToString(k) + "|" + made.TypeId.ToString(k) + "|" + made.GroupId.ToString(k)
                        + "|prop|0|0|0|0|0|1||0";
                }

                return null;
            }

            if (obj is Sim || obj is Lot || obj is Terrain)
            {
                return "";
            }

            ResourceKey key = obj.GetResourceKey();
            if (key == ResourceKey.kInvalidResourceKey)
            {
                return "";
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            // kind: a carryable prop may be created on a client; fixed furniture is only ever matched to
            // the client's own (by id, or by type and placement: save copies place it the same way).
            Vector3 position = obj.Position;
            Vector3 forward = obj.ForwardVector;
            // spawn: made by this game since the session began (SimulatorRecorder; a fire, an ash pile, a puddle): a client has
            // no such object of its own and shows the server's.
            ResourceKey created;
            bool carryable = obj is Sims3.Gameplay.ActorSystems.ICarryable;
            bool runtime = SimulatorRecorder.TryGetKey(id, out created);
            // A carryable this game made and left in the world with no parent (a newspaper on the lawn, put down by a Sim
            // no client streams, or made in place): the prop poll watches it, so its visibility (H) and placement reach
            // the clients. A prop's copy is made hidden until a line shows it, and without this nothing ever did
            // (two-paper-refused, 2026-10-21: the server's paper hidden on both clients, flags 3).
            if (carryable && runtime && obj.Parent == null && obj.InWorld)
            {
                AddHeldProp(obj);
            }

            return obj.GetType().FullName + "|" + key.InstanceId.ToString(c) + "|" + key.TypeId.ToString(c) + "|" + key.GroupId.ToString(c)
                + "|" + (carryable ? "prop" : runtime ? "spawn" : "fixed")
                + "|" + Float(position.x) + "|" + Float(position.y) + "|" + Float(position.z)
                + "|" + Float(forward.x) + "|" + Float(forward.y) + "|" + Float(forward.z);
        }

        // Client: server objects never made here (a script that would run the server's own simulation), whatever the O
        // line's kind (a fire loaded from the save has no spawn mark): a stand-in records where they are.
        private sealed class StandIn
        {
            public Vector3 Position;
            public Vector3 Forward;
        }

        private static readonly Dictionary<ulong, StandIn> sStandIns = new Dictionary<ulong, StandIn>();
        private static long sStandInsMade;
        private static long sStandInEffects;
        private static long sSpawnsShown;

        private static bool IsServerOnlyType(string typeFullName)
        {
            // Fire, FireTile and EmergencyFireTile are Sims3.Gameplay.Objects.Fire; FireSource is the fire situation's.
            return typeFullName == "Sims3.Gameplay.Objects.Fire" || typeFullName == "Sims3.Gameplay.Objects.FireSource";
        }

        // O lines for an object with no script object: made from its prototype key (Simulator.CreateObject).
        private const string ScriptlessType = "@scriptless";
        private static long sScriptlessCreated;
        // Server: an announced object that no longer exists (eaten food, a used-up prop) -> Od line.
        private static void ForgetVanishedObjects()
        {
            List<ulong> gone = new List<ulong>();
            foreach (ulong id in SacsRecorder.AnnouncedObjects.Keys)
            {
                ObjectGuid guid = new ObjectGuid(id);
                if (guid.ObjectFromId<GameObject>() == null && !Sims3.SimIFace.Objects.IsValid(guid))
                {
                    gone.Add(id);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                SacsRecorder.AnnouncedObjects.Remove(gone[i]);
                BoundaryState.ForgetObject(gone[i]);
                SacsRecorder.RingLine("Od|" + gone[i].ToString(CultureInfo.InvariantCulture));
            }
        }

        // Client: server object id -> the copy this client made of it.
        private static readonly Dictionary<ulong, ObjectGuid> sObjectAliases = new Dictionary<ulong, ObjectGuid>();
        private static long sObjectsShared;
        private static long sObjectsCreated;
        private static long sObjectsFailed;
        private static long sObjectsDropped;

        // seq|O|id|typeFullName|keyInstance|keyType|keyGroup|prop or fixed|x|y|z|fx|fy|fz
        // In order: an alias already made; this client's object with the same id and type (shared); its
        // object of the same type placed within a metre of the server's (matched: furniture from a
        // different save copy); otherwise a hidden copy made from the key (created). A prop copy waits out
        // of place for the stream's attach and visibility lines. A furniture copy is placed where the
        // server's stands: a copy left at the origin dragged the Sims animating against it there
        // (cold-test-041), and without one, props set down on it stayed in the Sim's hand (042).
        private static long sObjectsMatched;
        private static long sItemsMatched; // inventory items paired by type (MatchItemForModules)
        private static readonly Dictionary<ulong, bool> sMatchedLocal = new Dictionary<ulong, bool>();

        private static bool ResolveStreamObject(string[] f)
        {
            if (f.Length < 11)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ulong id = ulong.Parse(f[2], c);
            if (sObjectAliases.ContainsKey(id))
            {
                return true;
            }

            if (f[3] == ScriptlessType)
            {
                try
                {
                    ResourceKey made = new ResourceKey(ulong.Parse(f[4], c), uint.Parse(f[5], c), uint.Parse(f[6], c));
                    ObjectGuid copyId = Simulator.CreateObject(made, null,
                        new Simulator.ObjectInitParameters(0uL, Vector3.OutOfWorld, 0, Vector3.UnitZ, (HiddenFlags)0));
                    if (!copyId.IsValid)
                    {
                        sObjectsFailed++;
                        return false;
                    }

                    sObjectAliases[id] = copyId;
                    sObjectsCreated++;
                    sScriptlessCreated++;
                    return true;
                }
                catch (Exception)
                {
                    sObjectsFailed++;
                    return false;
                }
            }

            GameObject own = new ObjectGuid(id).ObjectFromId<GameObject>();
            if (own != null && own.GetType().FullName == f[3])
            {
                sObjectsShared++; // the same object under the same id (furniture from the save)
                return true;
            }

            Vector3 serverPosition = new Vector3(float.Parse(f[8], c), float.Parse(f[9], c), float.Parse(f[10], c));
            if (IsServerOnlyType(f[3]))
            {
                // Never made here (its script would run the server's simulation on this client: a fire arms its own
                // burst-into-flame and spreads); a stand-in where it is, which its effects play at (ReplayVfx).
                StandIn standIn = new StandIn();
                standIn.Position = serverPosition;
                standIn.Forward = f.Length > 13 ? new Vector3(float.Parse(f[11], c), float.Parse(f[12], c), float.Parse(f[13], c)) : Vector3.UnitZ;
                sStandIns[id] = standIn;
                sStandInsMade++;
                return true;
            }
            GameObject placed = null;
            float best = 1f;
            foreach (GameObject candidate in Sims3.Gameplay.Queries.GetObjects<GameObject>(serverPosition, 1f))
            {
                // Not one that already stands for a server object: a copy made here is no matched object, and two
                // server books in two seated Sims' hands matched the one copy made for the first, so only one showed
                // (client 2, 2026-10-21: Jennifer's and Pablo's books both aliased to one local copy).
                if (candidate == null || candidate is Sim || candidate.GetType().FullName != f[3] || sMatchedLocal.ContainsKey(candidate.ObjectId.Value)
                    || IsAliasTarget(candidate.ObjectId))
                {
                    continue;
                }

                float d = (candidate.Position - serverPosition).Length();
                if (d <= best)
                {
                    best = d;
                    placed = candidate;
                }
            }

            if (placed != null)
            {
                sObjectAliases[id] = placed.ObjectId;
                sMatchedLocal[placed.ObjectId.Value] = true;
                sObjectsMatched++;
                return true;
            }

            try
            {
                ResourceKey key = new ResourceKey(ulong.Parse(f[4], c), uint.Parse(f[5], c), uint.Parse(f[6], c));
                GameObject copy = GlobalFunctions.CreateObjectOutOfWorld(key) as GameObject;
                if (copy == null)
                {
                    sObjectsFailed++;
                    return false;
                }

                if (f[7] != "prop" && f.Length > 13)
                {
                    copy.SetPosition(serverPosition);
                    copy.SetForward(new Vector3(float.Parse(f[11], c), float.Parse(f[12], c), float.Parse(f[13], c)));
                }

                // In the world, hidden: the stream's attach and visibility lines decide when a prop shows;
                // a furniture copy stays hidden (the client's own room is what the player sees). A spawn shows where the
                // server's is (the client has no such object of its own: an ash pile, a puddle).
                copy.AddToWorld();
                if (f[7] == "spawn")
                {
                    sSpawnsShown++;
                }
                else
                {
                    World.ObjectSetHiddenFlags(copy.ObjectId, uint.MaxValue);
                }
                sObjectAliases[id] = copy.ObjectId;
                sObjectsCreated++;
                return true;
            }
            catch (Exception)
            {
                sObjectsFailed++;
                return false;
            }
        }

        // seq|Od|id: remove the copy this client made (never an object it had of its own).
        // Client: drop everything the presentation built for the last session (a warm re-attach, or a reloaded
        // presentation module): the copies it created (never the client's own objects it matched), its effects, its
        // replay drivers and walks' held lines, and the maps. The Sims are reset by the next history's StopAllAnimation
        // lines, and the history (O lines, drivers, boundary state) builds everything again.
        private static long sPresentationResets;
        private static string sLastPresentationReset = "-";

        private static string sPresentationModuleStart = "-";

        internal static ObjectGuid ReplayObjectForModules(ulong serverId)
        {
            return ReplayObject(serverId);
        }

        // An inventory item the server made at runtime, paired with this client's own object of that type
        // (Presentation/Inventories.cs, InventorySet.Pair): an alias like a placement match, so replay, picks
        // (WireObjectId) and refusals name it by the server's id, and never destroyed when the server's goes.
        // False when either side is already mapped.
        internal static bool MatchItemForModules(ulong serverId, ObjectGuid local)
        {
            if (serverId == 0uL || !local.IsValid || sObjectAliases.ContainsKey(serverId) || sMatchedLocal.ContainsKey(local.Value)
                || FindSim(serverId) != null || new ObjectGuid(serverId).ObjectFromId<GameObject>() != null || IsAliasTarget(local))
            {
                return false;
            }

            sObjectAliases[serverId] = local;
            sMatchedLocal[local.Value] = true;
            sItemsMatched++;
            return true;
        }

        // True when the local object already stands for some server object (a copy made here or a matched object):
        // one local object never takes a second server id.
        private static bool IsAliasTarget(ObjectGuid local)
        {
            foreach (ObjectGuid aliased in sObjectAliases.Values)
            {
                if (aliased == local)
                {
                    return true;
                }
            }

            return false;
        }

        internal static Type PresenterHookForModules()
        {
            return PresenterOn() ? sPresenterHookType : null;
        }

        internal static void PresenterApplyForModules(ulong localSim, string posture)
        {
            if (PresenterOn())
            {
                PresenterApply(localSim, posture, 0uL, 0);
            }
        }

        internal static string ResetPresentation()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            PresentationModules.ResetModule();
            int copies = 0;
            int effects = 0;
            int drivers = 0;
            // Lines still waiting go with the reset (the next history rebuilds what they set): recorded, so a loss here
            // shows in replay-skips instead of nowhere.
            foreach (List<string> waiting in sReplayPending.Values)
            {
                for (int i = 0; i < waiting.Count; i++)
                {
                    RecordReplaySkip(SkipSiteResetDropped, waiting[i]);
                }
            }

            foreach (List<string> waiting in sRouteHeldLines.Values)
            {
                for (int i = 0; i < waiting.Count; i++)
                {
                    RecordReplaySkip(SkipSiteResetDropped, waiting[i]);
                }
            }

            for (int i = 0; i < sRouteReleasedLines.Count; i++)
            {
                RecordReplaySkip(SkipSiteResetDropped, sRouteReleasedLines[i]);
            }

            try
            {
                List<ulong> created = new List<ulong>();
                foreach (KeyValuePair<ulong, ObjectGuid> alias in sObjectAliases)
                {
                    if (!sMatchedLocal.ContainsKey(alias.Value.Value))
                    {
                        created.Add(alias.Key);
                    }
                }

                for (int i = 0; i < created.Count; i++)
                {
                    if (DropStreamObject(new string[] { "0", "Od", created[i].ToString(c) }))
                    {
                        copies++;
                    }
                }

                sObjectAliases.Clear();
                sMatchedLocal.Clear();
                foreach (ClientEffect effect in sClientEffects.Values)
                {
                    try
                    {
                        if (effect.Effect != null)
                        {
                            effect.Effect.Stop();
                            effect.Effect.Dispose();
                            effects++;
                        }
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }
                }

                sClientEffects.Clear();
                ISacsProxy proxy = SacsRecorder.Current;
                List<KeyValuePair<uint, uint>> live = new List<KeyValuePair<uint, uint>>(sReplayDrivers);
                for (int i = 0; i < live.Count; i++)
                {
                    try
                    {
                        ReleaseReplayDriver(proxy, live[i].Key, live[i].Value);
                        drivers++;
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }
                }

                sReplayDrivers.Clear();
                sReplayBorrowed.Clear();
                sReplayAcquiredOn.Clear();
                sReplayAcquireLines.Clear();
                sReplaySetup.Clear();
                sReplayStates.Clear();
                ClientBoundaryRoute.Receipts.Clear();
                SacsRecorder.Authority.Clear();
                sReplayPending.Clear();
                sBridgeParked.Clear();
                sReplayDriverObjects.Clear();
                sStreamAnimatedUtc.Clear();
                sJoinResetUtc.Clear();
                sRouteHeldLines.Clear();
                sRouteReleasedLines.Clear();
                sClientPlacements.Clear();
                sClientSoundHandles.Clear();
                sClientClipControllers.Clear();
                sStandIns.Clear();
            }
            catch (Exception ex)
            {
                sReplayErrors++;
                sReplayLastError = "ResetPresentation:" + ex.GetType().Name;
            }

            sPresentationResets++;
            return "copies:" + copies.ToString(c) + "/effects:" + effects.ToString(c) + "/drivers:" + drivers.ToString(c);
        }

        private static bool DropStreamObject(string[] f)
        {
            ulong id = ulong.Parse(f[2], CultureInfo.InvariantCulture);
            sStandIns.Remove(id); // its effects end with their own stop / dispose lines
            ObjectGuid copyId;
            if (!sObjectAliases.TryGetValue(id, out copyId))
            {
                return true;
            }

            sObjectAliases.Remove(id);
            if (sMatchedLocal.Remove(copyId.Value))
            {
                return true; // the client's own object, matched by placement: never destroyed
            }

            try
            {
                GameObject copy = copyId.ObjectFromId<GameObject>();
                if (copy != null)
                {
                    copy.UnParent();
                    copy.Destroy();
                }
                else if (Sims3.SimIFace.Objects.IsValid(copyId))
                {
                    Slots.DetachFromSlot(copyId);
                    Simulator.DestroyObject(copyId); // a script-less copy (SimulatorRecorder)
                }

                sObjectsDropped++;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsAuthorityOwned(ulong localObjectId)
        {
            for (int i = 0; i < sAuthorityOwned.Length; i++)
            {
                if (sAuthorityOwned[i] == localObjectId)
                {
                    return true;
                }
            }

            return false;
        }

        private static void HandleSacsReplay(BridgeCommand command)
        {
            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SACS_REPLAY is client-role only."));
                return;
            }

            if (SacsRecorder.Current == null || command.Parts.Length < 3)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SACS_REPLAY needs the SACS proxy and a hex-encoded line batch."));
                return;
            }

            // Experiment C, client ring path: ring lines published before this command go first.
            DrainClientRing();

            string[] batchLines = BridgeCodec.DecodeText(command.Parts[2]).Split('\n');
            if (SimLinkShadow.ClientEnabled)
            {
                try
                {
                    SimLinkShadow.NoteBridgeLines(batchLines);
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
            }

            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", ApplyReplayBatch(batchLines)));
        }

        // Server ring path (internal documentation): instead of answering the controller's SACS_RECORDER dump
        // every 250 ms, the mod runs the same dump itself on every simulator update and publishes each page
        // (the dump reply's text: "seq=N;more=M" then the lines) as one record on lane 6 of the SimLink ring. The
        // observers (adopt target drivers, routes, prop visibility, positions) keep their 250 ms cadence (their resend
        // counters count polls); the recorder's lines go out as soon as the next tick. The cursor moves only when a page
        // is in the ring, so a full ring retries and a fallback to dumps loses nothing the recorder ring still holds.
        // Targets are filtered when the page is built (the dump filtered at read time over the whole range).
        private static readonly TimeSpan PushObserveInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan PushHeartbeat = TimeSpan.FromSeconds(1);
        private const int PushPageChars = 60000;
        private const int PushPagesPerTick = 16;
        private static bool sPushEnabled;
        private static bool sPushTaskScheduled;
        private static int sPushEvery = 1;
        private static long sPushUpdates;
        private static long sPushCursor;
        private static Dictionary<ulong, bool> sPushTargets = new Dictionary<ulong, bool>();
        private static DateTime sPushNextObserve = DateTime.MinValue;
        private static DateTime sPushLastPage = DateTime.MinValue;
        private static long sPushPages;
        private static long sPushRuns;
        private static long sPushLines;
        private static long sPushDrops;
        private static long sPushBehind;
        private static long sPushErrors;
        private static string sPushLastError = "-";

        private static Dictionary<ulong, bool> ParsePushTargets(string field)
        {
            Dictionary<ulong, bool> targets = new Dictionary<ulong, bool>();
            ulong[] ids = SimScopeWire.Parse(field);
            for (int i = 0; ids != null && i < ids.Length; i++)
            {
                targets[ids[i]] = true;
            }

            return targets;
        }

        private static string DescribePush()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "push=" + (sPushEnabled ? "on" : "off") + ",cursor=" + sPushCursor.ToString(c) + ",sequence=" + SacsRecorder.Sequence.ToString(c)
                + ",targets=" + sPushTargets.Count.ToString(c) + ",pages=" + sPushPages.ToString(c) + ",lines=" + sPushLines.ToString(c)
                + ",drops=" + sPushDrops.ToString(c) + ",behind=" + sPushBehind.ToString(c) + ",errors=" + sPushErrors.ToString(c) + ",lastError=" + sPushLastError
                + ",runs=" + sPushRuns.ToString(c) + ",every=" + sPushEvery.ToString(c) + ",simTick=" + SafeTick().ToString(c)
                + ",sentByKind=" + LineKindCounts.Format(sSentByKind);
        }

        // Server: every line handed to the controller (push pages and dumps) by kind; a client's receivedByKind
        // (replay-skips) is the other end.
        private static readonly Dictionary<string, long> sSentByKind = new Dictionary<string, long>();

        private static void ScheduleServerPushTask()
        {
            if (sPushTaskScheduled)
            {
                return;
            }

            sPushTaskScheduled = true;
            Simulator.AddObject((object)new EveryUpdateTask(EveryUpdateTask.ServerPush));
        }

        // One simulator update of the push; false ends the task (push off).
        private static bool ServerPushUpdate()
        {
            if (!sPushEnabled)
            {
                sPushTaskScheduled = false;
                return false;
            }

            if (++sPushUpdates % sPushEvery != 0)
            {
                return true;
            }

            sPushRuns++;
            try
            {
                PushPages();
            }
            catch (Exception ex)
            {
                sPushErrors++;
                sPushLastError = ex.GetType().Name + ":" + (ex.Message ?? "").Replace('|', '/').Replace(';', ',');
            }

            return true;
        }

        // A task the simulator runs on every update until it says stop. A OneShotFunctionTask with a delay that
        // schedules the next one when it runs gets every second update at best: its stopwatch starts on its first
        // update and fires on the next (push-096: lines reached the controller one or two ticks after they were
        // recorded; the client ring read the same way). Runs while the game is paused, like the bridge pump.
        private sealed class EveryUpdateTask : Sims3.SimIFace.Task
        {
            internal const int ServerPush = 0;
            internal const int ClientRing = 1;
            private readonly int mKind;

            internal EveryUpdateTask(int kind)
            {
                mKind = kind;
            }

            public override void Simulate()
            {
                bool keep;
                try
                {
                    keep = mKind == ServerPush ? ServerPushUpdate() : ClientRingUpdate();
                }
                catch (Exception)
                {
                    keep = false;
                    if (mKind == ServerPush)
                    {
                        sPushTaskScheduled = false;
                    }
                    else
                    {
                        sRingTaskScheduled = false;
                    }
                }

                if (!keep)
                {
                    Simulator.DestroyObject(ObjectId);
                }
            }
        }

        private static void PushPages()
        {
            if (!sPushEnabled || !SacsRecorder.RingEnabled)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now >= sPushNextObserve)
            {
                sPushNextObserve = now + PushObserveInterval;
                AdoptTargetDrivers(sPushTargets, false);
                PublishRoutes(sPushTargets);
                PollPropVisibility(sPushTargets);
                PublishPositions(sPushTargets);
            }

            if (SacsRecorder.Sequence - sPushCursor >= SacsRecorder.RingCapacityLines)
            {
                sPushBehind++; // the recorder ring has wrapped past the cursor: those lines are gone, as with a slow dump
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            for (int page = 0; page < PushPagesPerTick; page++)
            {
                bool heartbeat = now - sPushLastPage >= PushHeartbeat;
                if (sPushCursor >= SacsRecorder.Sequence && !heartbeat)
                {
                    return;
                }

                long last;
                List<string> lines = SacsRecorder.RingSince(sPushCursor, PushPageChars, sPushTargets, out last);
                if (lines.Count == 0 && !heartbeat)
                {
                    sPushCursor = Math.Max(sPushCursor, last); // nothing for the targets: no page needed
                    if (sPushCursor >= SacsRecorder.Sequence)
                    {
                        return;
                    }

                    continue;
                }

                string text = "seq=" + last.ToString(c) + ";more=" + (last < SacsRecorder.Sequence ? "1" : "0")
                    + (lines.Count > 0 ? "\n" + string.Join("\n", lines.ToArray()) : string.Empty);
                if (!SimLinkShadow.PublishPage((int)last, text))
                {
                    sPushDrops++; // the controller isn't draining: keep the cursor, retry next tick
                    return;
                }

                sPushCursor = Math.Max(sPushCursor, last);
                sPushLastPage = now;
                sPushPages++;
                sPushLines += lines.Count;
                LineKindCounts.AddLines(sSentByKind, lines);
                if (sPushCursor >= SacsRecorder.Sequence)
                {
                    return;
                }
            }
        }

        // Experiment C, client ring path: SimAnimation lines arriving through the SimLink sidecar's inbound ring are
        // applied here, on the game thread, on every simulator update and before every bridge command. Each read is one
        // batch exactly as SACS_REPLAY would carry it (the controller splits events the same way and marks each batch's
        // end), so the per-batch logic (pending drivers, route releases, re-resolve, own-machine repairs) sees the same
        // batches. A failure turns the ring path off (the client controller notices the ring stop moving and falls back
        // to the bridge).
        private const int RingBatchesPerDrain = 64;
        private static bool sRingTaskScheduled;

        private static void ScheduleRingTask()
        {
            if (sRingTaskScheduled)
            {
                return;
            }

            sRingTaskScheduled = true;
            Simulator.AddObject((object)new EveryUpdateTask(EveryUpdateTask.ClientRing));
        }

        // One simulator update of the client ring path; false ends the task (ring path off).
        private static bool ClientRingUpdate()
        {
            if (!SimLinkShadow.RingEnabled)
            {
                sRingTaskScheduled = false;
                return false;
            }

            SimLinkShadow.RingRuns++;
            DrainClientRing();
            return true;
        }

        private static void DrainClientRing()
        {
            if (!SimLinkShadow.RingEnabled || sCurrentRole != "client" || SacsRecorder.Current == null)
            {
                return;
            }

            try
            {
                for (int reads = 0; reads < RingBatchesPerDrain; reads++)
                {
                    string[] lines = SimLinkShadow.ReadInboundLines();
                    if (lines == null || lines.Length == 0)
                    {
                        return;
                    }

                    ApplyReplayBatch(lines);
                }
            }
            catch (Exception ex)
            {
                SimLinkShadow.RingEnabled = false;
                SimLinkShadow.RingErrors++;
                SimLinkShadow.RingLastError = ex.GetType().Name + ":" + (ex.Message ?? "").Replace('|', '/').Replace(';', ',');
            }
        }

        // The body of SACS_REPLAY: applies one batch of the server's recorded lines and returns the report.
        private static string ApplyReplayBatch(string[] batchLines)
        {
            LineKindCounts.AddLines(sReplayReceivedByKind, batchLines); // as they arrive, before pending or held lines join
            ISacsProxy proxy = SacsRecorder.Current;
            ClearCosmeticAllowanceOnce();
            PresenterOn();

            CultureInfo c = CultureInfo.InvariantCulture;
            int applied = 0;
            int skipped = 0;
            // Drivers whose host could not be resolved earlier (its alias or spawn had not arrived yet)
            // go first, once the host resolves, so their history is not lost.
            List<string> work = new List<string>();
            List<uint> ready = new List<uint>();
            foreach (KeyValuePair<uint, List<string>> pending in sReplayPending)
            {
                string[] a = pending.Value[0].Split('|');
                if (a.Length > 3 && ReplayObject(ulong.Parse(a[3], c)).IsValid)
                {
                    ready.Add(pending.Key);
                    work.AddRange(pending.Value);
                }
            }

            for (int i = 0; i < ready.Count; i++)
            {
                sReplayPending.Remove(ready[i]);
            }

            int releasedEnd = work.Count + sRouteReleasedLines.Count;
            int releasedStart = work.Count;
            if (sRouteReleasedLines.Count > 0)
            {
                sRouteLastRelease = string.Join(((char)10).ToString(), sRouteReleasedLines.ToArray());
            }

            work.AddRange(sRouteReleasedLines); // a client route ended: what the Sim did meanwhile, in order
            sRouteReleasedLines.Clear();
            int appliedAtRelease = 0;
            int skippedAtRelease = 0;
            long failuresAtRelease = 0L;
            work.AddRange(batchLines);
            Dictionary<uint, bool> reresolved = new Dictionary<uint, bool>();
            Dictionary<ulong, bool> stopped = new Dictionary<ulong, bool>();
            SacsRecorder.Replaying = true;
            try
            {
            for (int wi = 0; wi < work.Count; wi++)
            {
                if (releasedEnd > releasedStart && wi == releasedStart)
                {
                    appliedAtRelease = applied;
                    skippedAtRelease = skipped;
                    failuresAtRelease = sReplayRequestFailureTotal;
                }

                if (releasedEnd > releasedStart && wi == releasedEnd)
                {
                    CountReleaseOutcome(applied - appliedAtRelease, skipped - skippedAtRelease, sReplayRequestFailureTotal - failuresAtRelease);
                    releasedEnd = releasedStart; // counted
                }

                string line = work[wi];
                string[] f = line.Split('|');
                if (f.Length < 2 || (f.Length < 3 && f[1] != "Ne" && f[1] != "Rt" && f[1] != "Vp"))
                {
                    continue;
                }

                try
                {
                    string kind = f[1];
                    // A presentation module's handler for this kind (PresentationModules) comes before the built-in one.
                    PresentationLineHandler moduleHandler = PresentationModules.HandlerFor(kind);
                    if (moduleHandler != null)
                    {
                        bool handled;
                        try
                        {
                            handled = moduleHandler(f);
                        }
                        catch (Exception)
                        {
                            handled = false;
                        }

                        if (handled)
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(7900, line);
                        }

                        continue;
                    }

                    // object identity (O) and server-side removal (Od).
                    if (kind == "O" || kind == "Od")
                    {
                        if (kind == "O" ? ResolveStreamObject(f) : DropStreamObject(f))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(6729, line);
                        }

                        continue;
                    }

                    // item 4: props. Attach / detach / visibility name objects, not drivers.
                    if (kind == "Qu")
                    {
                        if (ReplayQueue(f))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(6744, line);
                        }

                        continue;
                    }

                    if (kind == "Ou")
                    {
                        if (ReplayOutfit(f))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(6758, line);
                        }

                        continue;
                    }

                    if (kind == "Ne" || kind == "Rt" || kind == "Vp" || kind == "Rs" || kind == "Rte" || kind == "Rx" || kind == "Rh" || kind == "Ru" || kind == "Pz")
                    {
                        skipped++; RecordReplaySkip(6772, line); // walks and positions are the presentation module's (none loaded)
                        continue;
                    }

                    if (kind == "Po")
                    {
                        // A posture change while this client's copy still walks waits with the animation lines: the
                        // server's Sim sat down after its walk, so the copy does too (applied mid-walk it broke the walk).
                        ObjectGuid posturedSim = ReplayObject(ulong.Parse(f[2], c));
                        if (posturedSim.IsValid)
                        {
                            MarkStreamPositioned(posturedSim.Value); // the stream still describes this Sim
                        }

                        if (posturedSim.IsValid && IsClientRouting(posturedSim.Value))
                        {
                            HoldForRoute(posturedSim.Value, line);
                            continue;
                        }

                        if (ReplayStreamPosture(f))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(6809, line);
                        }

                        continue;
                    }

                    if (kind == "Hd")
                    {
                        if (ReconcileHands(f))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(6837, line);
                        }

                        continue;
                    }

                    if (kind == "Au")
                    {
                        if (ReplayAudio(f))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(7112, line);
                        }

                        continue;
                    }

                    if (kind == "Lk")
                    {
                        // A head look-at of the server's Sim (LookAtRecorder), on this client's copy when it is a puppet.
                        if (LookAtRecorder.Replay(f, new Converter<ulong, ObjectGuid>(ReplayObject), new Predicate<ulong>(MethodSwap.IsPuppet)))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(7114, line);
                        }

                        continue;
                    }

                    if (kind == "Wl")
                    {
                        // A lot-keyed world call (floor features: puddles, burn marks, leaves), BoundaryReplay.ReplayLot.
                        if (BoundaryReplay.ReplayLot(BoundaryReplay.World, f, new Converter<ulong, ObjectGuid>(ReplayObject)))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(7113, line);
                        }

                        continue;
                    }

                    if (kind == "Wd" || kind == "Ob")
                    {
                        string replayContext = SacsRecorder.LocalContext;
                        SacsRecorder.LocalContext = "server placement (Ob)";
                        bool replayed = BoundaryReplay.Replay(kind == "Wd" ? BoundaryReplay.World : BoundaryReplay.Objects, f, new Converter<ulong, ObjectGuid>(ReplayObject),
                            new Predicate<ulong>(IsClientWalking));
                        SacsRecorder.LocalContext = replayContext;
                        if (replayed)
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(7110, line);
                        }

                        continue;
                    }

                    if (kind == "An")
                    {
                        if (ReplayAnimation(f))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(7109, line);
                        }

                        continue;
                    }

                    if (kind == "Vx")
                    {
                        if (ReplayVfx(f))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(7108, line);
                        }

                        continue;
                    }

                    if (kind == "Bf")
                    {
                        skipped++; RecordReplaySkip(7106, line); // moodlets are the presentation module's (none loaded)
                        continue;
                    }

                    if (kind == "Iv")
                    {
                        skipped++; RecordReplaySkip(7113, line); // inventories are the presentation module's (none loaded)
                        continue;
                    }

                    if (kind == "Rf")
                    {
                        skipped++; RecordReplaySkip(7114, line); // menu refusals are the presentation module's (none loaded)
                        continue;
                    }

                    if (kind == "Us")
                    {
                        skipped++; RecordReplaySkip(7115, line); // object use is the presentation module's (none loaded)
                        continue;
                    }

                    if (kind == "Pl" || kind == "Rl" || kind == "Cv")
                    {
                        bool done = kind == "Pl" ? ReplayPlacement(f) : (kind == "Rl" ? ReplayRelationship(f) : ReplayConversation(f));
                        if (done)
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(kind == "Pl" ? 7103 : (kind == "Rl" ? 7107 : 7108), line);
                        }

                        continue;
                    }

                    if (kind == "T" || kind == "Tx" || kind == "B" || kind == "U" || kind == "H")
                    {
                        if (ReplayProp(f, kind))
                        {
                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(6851, line);
                            sReplayPropsUnresolved++;
                        }

                        continue;
                    }

                    // An object's rig, or its clips at a priority made interruptible (SacsRecorder): objects, not drivers.
                    if (kind == "Rg" || kind == "Ci")
                    {
                        ObjectGuid animated = ReplayObject(ulong.Parse(f[2], c));
                        if (animated.IsValid && f.Length > 3)
                        {
                            if (kind == "Rg")
                            {
                                string[] rig = f[3].Split('~');
                                proxy.SetRig(animated, new ResourceKey(ulong.Parse(rig[0], c), uint.Parse(rig[1], c), uint.Parse(rig[2], c)));
                            }
                            else
                            {
                                proxy.SetClipInterruptible(animated, (AnimationPriority)int.Parse(f[3], c), f.Length > 4 && f[4] == "1");
                            }

                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(6877, line);
                        }

                        continue;
                    }

                    // StopAllAnimation names an object, not a driver: handle it before parsing a handle.
                    if (kind == "Z")
                    {
                        ObjectGuid target = ReplayObject(ulong.Parse(f[2], c));
                        if (target.IsValid && IsClientRouting(target.Value))
                        {
                            HoldForRoute(target.Value, line);
                            continue;
                        }

                        if (target.IsValid)
                        {
                            proxy.StopAllAnimation(target);
                            stopped[target.Value] = true;
                            if (f[0] == "0")
                            {
                                // The late-join reset: the copy also drops what its own queue holds. A client runs no
                                // interactions, but the save it loaded can bring some back (Jennifer cleaning up a serving
                                // dish on the client while the server's Jennifer used her phone).
                                CancelOwnInteractions(target);
                                NoteJoinReset(target.Value);
                            }

                            applied++;
                        }
                        else
                        {
                            skipped++; RecordReplaySkip(6876, line);
                        }

                        continue;
                    }

                    uint serverDriver = uint.Parse(f[2], c);
                    NoteDriverObjects(kind, f, serverDriver);
                    ulong routingSim = RoutingSimForDriver(serverDriver);
                    if (routingSim != 0uL)
                    {
                        HoldForRoute(routingSim, line);
                        continue;
                    }

                    uint driver;
                    bool known = sReplayDrivers.TryGetValue(serverDriver, out driver);
                    if (kind == "A")
                    {
                        // seq|A|driver|host|type|group|instance|priority
                        if (ulong.Parse(f[6], c) == SacsRecorder.FacialIdleMachine)
                        {
                            // A facial idle from an older server: this client plays its own (SacsRecorder.FacialIdleMachine);
                            // the driver stays unmapped, so its later lines are skipped too.
                            skipped++; RecordReplaySkip(6900, line);
                            continue;
                        }

                        ObjectGuid host = ReplayObject(ulong.Parse(f[3], c));
                        if (!host.IsValid)
                        {
                            // Keep it (and its later lines) until the host resolves.
                            if (sReplayPending.Count < ReplayPendingDrivers || sReplayPending.ContainsKey(serverDriver))
                            {
                                List<string> held = new List<string>();
                                held.Add(line);
                                sReplayPending[serverDriver] = held;
                            }

                            skipped++; RecordReplaySkip(6915, line);
                            continue;
                        }

                        ResourceKey key = new ResourceKey(ulong.Parse(f[6], c), uint.Parse(f[4], c), uint.Parse(f[5], c));
                        int priority = int.Parse(f[7], c);
                        if (known)
                        {
                            ReleaseReplayDriver(proxy, serverDriver, driver); // a resync of a driver this client already plays
                        }

                        Sim hostSim = host.ObjectFromId<Sim>();
                        bool borrowed;
                        uint acquired = ReplayDriverBootstrap.Resolve(FindOwnDriver(host, key.InstanceId),
                            hostSim != null && StreamOwnsIdle(hostSim) && key.InstanceId == ResourceUtils.HashString64("idle"),
                            delegate { WarmMachine(host, key, priority); return 0u; },
                            delegate { return proxy.AcquireDriver(false, host, key, priority); }, out borrowed);
                        if (borrowed && acquired != 0u)
                        {
                            SacsSeedCopy.NoteMapped(serverDriver, acquired, key.InstanceId, true);
                            sReplayDrivers[serverDriver] = acquired;
                            sReplayBorrowed[serverDriver] = true;
                            sReplayAcquireLines[serverDriver] = line;
                            SacsRecorder.Authority.Follow(acquired); // item 4: the server's parameter values win on it
                            sReplayBorrowCount++;
                            applied++;
                            continue;
                        }

                        if ((borrowed && acquired == 0u) || acquired >= AcquireErrorFloor)
                        {
                            // stream gaps: without yielding, AcquireDriver answers with an error code when
                            // the machine is not loaded on this client yet (StateMachineClient.Acquire retries
                            // those with yield). Load it on a simulator task, keep the driver's lines, retry
                            // on a later batch. Mapping the code as a handle made every later request on the
                            // driver fail (kProxyInvalid).
                            string code = "0x" + acquired.ToString("X8", c);
                            long seen;
                            sReplayAcquireFailures.TryGetValue(code, out seen);
                            sReplayAcquireFailures[code] = seen + 1;
                            if (sReplayPending.Count < ReplayPendingDrivers || sReplayPending.ContainsKey(serverDriver))
                            {
                                List<string> held = new List<string>();
                                held.Add(line);
                                sReplayPending[serverDriver] = held;
                            }

                            WarmMachine(host, key, priority);
                            skipped++; RecordReplaySkip(6957, line);
                            continue;
                        }

                        sReplayDrivers[serverDriver] = acquired;
                        sRepairEntries.NoteLine(acquired, kind); // a fresh driver: a handle reused since a repair is not its entry
                        sReplayAcquireLines[serverDriver] = line;
                        if (host.ObjectFromId<Sim>() != null)
                        {
                            sReplayAcquiredOn[serverDriver] = new KeyValuePair<ulong, ulong>(host.Value, key.InstanceId);
                        }

                        SacsRecorder.Authority.Follow(acquired);
                        SacsSeedCopy.NoteMapped(serverDriver, acquired, key.InstanceId, false);
                        // While a motion watch runs: the replay driver's SACS events noted like the server's (a driver the
                        // mod acquires has no StateMachineClient, so no queue of its own).
                        EventQueueRecorder.Attach(proxy, acquired);
                        applied++;
                        continue;
                    }

                    if (!known)
                    {
                        if (kind == "AS")
                        {
                            SacsSeedCopy.NoteEarly(serverDriver); // coverage: seeds for a driver not mapped yet
                        }

                        List<string> pendingLines;
                        if (sReplayPending.TryGetValue(serverDriver, out pendingLines))
                        {
                            if (kind == "X" || pendingLines.Count >= ReplayPendingCap)
                            {
                                sReplayPending.Remove(serverDriver); // released, or too long to keep
                            }
                            else
                            {
                                pendingLines.Add(line);
                            }
                        }

                        skipped++; RecordReplaySkip(6982, line);
                        continue;
                    }

                    switch (kind)
                    {
                        case "S":
                            ObjectGuid actor = ReplayObject(ulong.Parse(f[4], c));
                            if (!actor.IsValid)
                            {
                                skipped++; RecordReplaySkip(6992, line);
                                continue;
                            }

                            if (IsAuthorityOwned(actor.Value))
                            {
                                // This client still runs its own Sim natively (until input forwarding,
                                // item 3): drop the driver, its later lines are skipped as unmapped.
                                ReleaseReplayDriver(proxy, serverDriver, driver);
                                skipped++; RecordReplaySkip(7001, line);
                                continue;
                            }

                            proxy.SetActor(driver, uint.Parse(f[3], c), actor);
                            break;
                        case "P":
                            proxy.SetParam(driver, uint.Parse(f[3], c), uint.Parse(f[4], c));
                            break;
                        case "Ps":
                            proxy.SetParam(driver, uint.Parse(f[3], c), f.Length > 4 ? f[4] : "");
                            break;
                        case "Pn":
                            proxy.SetParam(driver, f[3], f[4], f.Length > 5 ? f[5] : "");
                            break;
                        case "Ph":
                            proxy.SetParam(driver, f[3], f[4], uint.Parse(f[5], c));
                            break;
                        case "E":
                            sRepairEntries.NoteLine(driver, kind);
                            proxy.EnterState(driver, uint.Parse(f[3], c), uint.Parse(f[4], c));
                            sReplayStates.Set(serverDriver, uint.Parse(f[3], c), uint.Parse(f[4], c));
                            NoteStreamAnimated(serverDriver);
                            break;
                        case "R":
                            // seq|R|driver|actor|state|flags|actorObject[|wakeup|timeout|bridgeDriver|bridgeActor]. The bridge is a
                            // server driver: this client's own for it, or none when it plays no such driver. A history line
                            // (seq 0) is a path to the current state, not a hand-off under way: it waits on no bridge.
                            Sims3.SimIFace.SACS.DriverRequestFlags requestFlags = (Sims3.SimIFace.SACS.DriverRequestFlags)uint.Parse(f[5], c);
                            float wakeup = 0f;
                            uint timeout = 0u;
                            uint bridge = 0u;
                            uint bridgeActor = 0u;
                            if (f.Length > 10)
                            {
                                uint serverBridge;
                                float.TryParse(f[7], NumberStyles.Float, c, out wakeup);
                                uint.TryParse(f[8], NumberStyles.None, c, out timeout);
                                if (uint.TryParse(f[9], NumberStyles.None, c, out serverBridge) && serverBridge != 0u)
                                {
                                    if (sReplayDrivers.TryGetValue(serverBridge, out bridge))
                                    {
                                        uint.TryParse(f[10], NumberStyles.None, c, out bridgeActor);
                                        sReplayBridges++;
                                    }
                                    else if (f[0] == "0" || sReplayPending.ContainsKey(serverBridge))
                                    {
                                        // The machine it hands the Sim to is not here yet: wait for that machine's own request.
                                        List<string> parked;
                                        if (!sBridgeParked.TryGetValue(serverBridge, out parked))
                                        {
                                            if (sBridgeParked.Count >= 64)
                                            {
                                                sBridgeParked.Clear();
                                            }

                                            parked = new List<string>();
                                            sBridgeParked[serverBridge] = parked;
                                        }

                                        parked.Add(line);
                                        sReplayBridgesParked++;
                                        continue;
                                    }
                                }
                            }

                            // B2+(a): the first request on a driver a repair just entered, for that state, before the
                            // SACS pump took the entry (RepairEntryGate): taken as applied, not sent (the success path below).
                            bool repairDup = false;
                            if (sRepairEntries.Count > 0)
                            {
                                if (sRepairDupDrop.Value > 0f)
                                {
                                    repairDup = sRepairEntries.TakeRequest(driver, uint.Parse(f[4], c), (uint)requestFlags, bridge != 0u, RepairEntryTicks());
                                }
                                else
                                {
                                    sRepairEntries.NoteLine(driver, kind);
                                }
                            }

                            Sims3.SimIFace.SACS.ResultCodes result;
                            if (repairDup)
                            {
                                result = Sims3.SimIFace.SACS.ResultCodes.kSuccess;
                                idleRepairDupDroppedCount++;
                                if (f.Length > 6)
                                {
                                    MotionWatch.Note(ReplayObject(ulong.Parse(f[6], c)).Value, "R dropped: repair entry not yet pumped (state " + f[4] + ")");
                                }
                            }
                            else
                            {
                                result = proxy.RequestState(false, driver, uint.Parse(f[3], c), uint.Parse(f[4], c), requestFlags, wakeup, timeout, bridge, bridgeActor);
                            }

                            if (result == Sims3.SimIFace.SACS.ResultCodes.kSuccess)
                            {
                                StateCatchUp.Requested(serverDriver, driver, uint.Parse(f[3], c), uint.Parse(f[4], c), (uint)requestFlags, wakeup, timeout, bridge != 0u);
                                sReplayStates.Set(serverDriver, uint.Parse(f[3], c), uint.Parse(f[4], c));
                                NoteStreamAnimated(serverDriver);
                            }
                            else
                            {
                                StateCatchUp.Forget(serverDriver);
                            }

                            List<string> handOffs;
                            if (sBridgeParked.TryGetValue(serverDriver, out handOffs))
                            {
                                sBridgeParked.Remove(serverDriver);
                                work.InsertRange(wi + 1, handOffs); // the posture machine's hand-off to this one, now
                            }

                            if (result != Sims3.SimIFace.SACS.ResultCodes.kSuccess)
                            {
                                long seen;
                                sReplayResults.TryGetValue(result.ToString(), out seen);
                                sReplayResults[result.ToString()] = seen + 1;
                                RecordReplayFailure(result.ToString() + "|client=" + driver.ToString(c) + "|" + line);
                                sReplayRequestFailureTotal++;
                                // The client handle went stale (a borrowed posture driver the client replaced,
                                // or a driver the engine dropped): resolve the driver again from its acquire
                                // line and retry this request, once per batch.
                                string acquireLine;
                                if (result == Sims3.SimIFace.SACS.ResultCodes.kProxyInvalid && !reresolved.ContainsKey(serverDriver)
                                    && sReplayAcquireLines.TryGetValue(serverDriver, out acquireLine))
                                {
                                    reresolved[serverDriver] = true;
                                    sReplayReresolved++;
                                    sReplayBorrowed.Remove(serverDriver);
                                    SacsRecorder.Authority.Unfollow(driver);
                                    sReplayDrivers.Remove(serverDriver);
                                    work.Add(acquireLine);
                                    // Seeds, actors, parameters: what the acquire line alone does not give the new driver.
                                    List<string> setup = sReplaySetup.Lines(serverDriver);
                                    work.AddRange(setup);
                                    sReplaySetupResent += setup.Count;
                                    work.Add(line);
                                }
                            }
                            break;
                        case "I":
                            sRepairEntries.NoteLine(driver, kind);
                            proxy.Interrupt(driver, uint.Parse(f[3], c));
                            break;
                        case "Se":
                            // seq|Se|driver|state: the server's machine entered a state (StateCatchUp, sacs.catchup on).
                            StateCatchUp.ServerEntered(serverDriver, uint.Parse(f[3], c), sReplayDrivers);
                            break;
                        case "Pa":
                            proxy.Pause(driver, uint.Parse(f[3], c));
                            break;
                        case "Co":
                            proxy.Continue(driver, uint.Parse(f[3], c));
                            break;
                        case "AS":
                            // seq|AS|driver|contextSeed|spareSeed ( tier 1, sacs.seed-copy): the server driver's seeds,
                            // sent before its first draw; a borrowed driver keeps its own.
                            SacsSeedCopy.Apply(serverDriver, driver, sReplayBorrowed.ContainsKey(serverDriver), f);
                            break;
                        case "AW":
                            // seq|AW|driver|spare|g:d:b:e|... ( option C, sacs.walker-copy): the server driver's walker
                            // counters, borrowed driver or not (SimLink's, not the game's). Not kept for a re-resolve
                            // (ReplaySetupCache leaves it out): SacsSeedCopy.Apply carries the old copy's counts instead.
                            SacsWalkerCopy.Apply(serverDriver, driver, f);
                            break;
                        case "X":
                            sRepairEntries.NoteLine(driver, kind);
                            ReleaseReplayDriver(proxy, serverDriver, driver);
                            break;
                        default:
                            skipped++; RecordReplaySkip(7054, line);
                            continue;
                    }

                    sReplaySetup.Keep(serverDriver, kind, f, line);
                    SacsSeedCopy.NoteLine(serverDriver);
                    applied++;
                }
                catch (Exception ex)
                {
                    sReplayErrors++;
                    sReplayLastError = ex.GetType().Name + ":" + (ex.Message ?? "").Replace('|', '/').Replace('\n', ' ');
                }
            }
            }
            finally
            {
                SacsRecorder.Replaying = false;
            }

            if (releasedEnd > releasedStart)
            {
                CountReleaseOutcome(applied - appliedAtRelease, skipped - skippedAtRelease, sReplayRequestFailureTotal - failuresAtRelease);
            }

            PresentationModules.Tick(); // the module's per-batch work (Walks: walk expiry, the position mirror)
            SettlePlacements();

            // Outside Replaying: these are this client's own machines, as they were when the Sim was created.
            string repairContext = SacsRecorder.LocalContext;
            SacsRecorder.LocalContext = RepairLabel.BatchEnd;
            try
            {
                foreach (ulong id in stopped.Keys)
                {
                    RepairOwnMachines(new ObjectGuid(id));
                }
            }
            finally
            {
                SacsRecorder.LocalContext = repairContext;
            }

            if (sReplayAcquiredOn.Count > 0 && DateTime.UtcNow >= sNextHandoverCheck)
            {
                sNextHandoverCheck = DateTime.UtcNow.AddMilliseconds(250);
                HandOverToOwnDrivers(0uL); // an own driver the game started after the replay acquired one
            }

            sReplayApplied += applied;
            sReplaySkipped += skipped;
            // End of managed replay application, NOT necessarily the visible
            // frame boundary. Available without a native sidecar at ~1 ms.
            long applyUtcTicks = DateTime.UtcNow.Ticks;
            return (
                "applied=" + applied.ToString(c) + ";skipped=" + skipped.ToString(c) + ";drivers=" + sReplayDrivers.Count.ToString(c)
                + ";pending=" + sReplayPending.Count.ToString(c) + ";requestFailures=" + DescribeCounts(sReplayResults) + ";acquireFailures=" + DescribeCounts(sReplayAcquireFailures) + ";borrowed=" + sReplayBorrowCount.ToString(c) + ";reresolved=" + sReplayReresolved.ToString(c) + "/setup:" + sReplaySetupResent.ToString(c) + ";" + SacsRecorder.Authority.Describe() + ";bridges=" + sReplayBridges.ToString(c) + "/parked:" + sReplayBridgesParked.ToString(c) + ";propsUnresolved=" + sReplayPropsUnresolved.ToString(c)
                + ";objects=shared:" + sObjectsShared.ToString(c) + ",matched:" + sObjectsMatched.ToString(c) + ",items:" + sItemsMatched.ToString(c) + ",created:" + sObjectsCreated.ToString(c)
                + ",failed:" + sObjectsFailed.ToString(c) + ",dropped:" + sObjectsDropped.ToString(c) + ";handFixes=" + sHandFixes.ToString(c) + ";snapshotMirror=skips:" + sStreamMirrorSkips.ToString(c) + ";extras=" + ExtrasReport() + ";vfx=" + VfxReport() + ";animation=" + AnimationReport() + ";world=installed:" + (WorldRecorder.Installed ? "1" : "0")
                + ",lines:" + WorldRecorder.Lines.ToString(c) + ",client:" + BoundaryReplay.Report(BoundaryReplay.World)
                + ";" + RustRoadPlace.DescribeStatus()
                + ";audio=installed:" + (AudioRecorder.Installed ? "1" : "0") + ",lines:" + AudioRecorder.Lines.ToString(c) + ",client:played:"
                + sSoundsPlayed.ToString(c) + "/failed:" + sSoundsFailed.ToString(c) + "/arrays:" + sSoundArrays.ToString(c)
                + "/points:" + sPointSounds.ToString(c) + "/stingsPlayed:" + sStingsPlayed.ToString(c) + ",pointLines:" + AudioRecorder.PointLines.ToString(c)
                + ",pointsOwn:" + AudioRecorder.PointsOwn.ToString(c) + ",pointsSent:" + SacsRecorder.PointSoundsSent.ToString(c)
                + ",stings:" + AudioRecorder.Stings.ToString(c) + "/stingsSent:" + sStingsSent.ToString(c) + "/stingsUnowned:" + AudioRecorder.StingsUnowned.ToString(c)
                + ";lookat=" + LookAtRecorder.Report()
                + ";objects2=installed:" + (ObjectsRecorder.Installed ? "1" : "0") + ",lines:" + ObjectsRecorder.Lines.ToString(c) + ",client:"
                + BoundaryReplay.Report(BoundaryReplay.Objects) + ";postures=standUps:" + sPostureStandUps.ToString(c) + ";presenter=" + PresenterReport() + ";censorChanges=" + sCensorChanges.ToString(c) + ";ownRepairs=" + sOwnMachineRepairs.ToString(c) + ";idleRepairDupDropped=" + idleRepairDupDroppedCount.ToString(c) + ";idleRepairStream=" + sIdleRepairStream.ToString(c) + ";idleRepairDefault=" + sIdleRepairDefault.ToString(c) + ";handovers=" + sReplayHandovers.ToString(c) + ";ownInteractionsCancelled=" + sOwnInteractionsCancelled.ToString(c)
                + ";joinResets=" + sJoinResets.ToString(c)
                + ";presentationResets=" + sPresentationResets.ToString(c) + "/last:" + sLastPresentationReset
                + ";presentation=" + PresentationModules.Report() + "/start:" + sPresentationModuleStart.Replace(';', ',')
                + ";streamAnimated=stamps:" + sStreamAnimatedStamps.ToString(c) + ",objects:" + sStreamAnimatedUtc.Count.ToString(c)
                + ";routeLines=held:" + sRouteLinesHeld.ToString(c) + ",linesReleased:" + sRouteLinesReleased.ToString(c) + ",linesDropped:" + sRouteLinesDropped.ToString(c)
                + ",released:applied:" + sRouteReleaseApplied.ToString(c) + "/skipped:" + sRouteReleaseSkipped.ToString(c) + "/failed:" + sRouteReleaseFailed.ToString(c) + ";outfits=changed:" + sOutfitChanges.ToString(c) + ",missing:" + sOutfitMissing.ToString(c)
                + ";totalApplied=" + sReplayApplied.ToString(c) + ";totalSkipped=" + sReplaySkipped.ToString(c)
                + ";applyUtcTicks=" + applyUtcTicks.ToString(c)
                + ";errors=" + sReplayErrors.ToString(c) + ";lastError=" + sReplayLastError);
        }

        // The recorder's last state for this object: "<machineInstance>:<stateHash>", "-" when unknown.
        private static string SacsField(ulong objectId)
        {
            SacsRecorder.ActorState state;
            if (!SacsRecorder.Actors.TryGetValue(objectId, out state))
            {
                return "-";
            }

            return state.Machine.ToString("x16", CultureInfo.InvariantCulture) + ":" + state.State.ToString("x8", CultureInfo.InvariantCulture);
        }

        private static string PresentationField(string value)
        {
            if (value == null || value.Length == 0)
            {
                return "-";
            }

            return value.Replace('|', '_').Replace('\n', '_').Replace('\r', '_');
        }

        private static string PresentationLine(Sim sim, ulong wireId, ulong lotId)
        {
            Vector3 position = sim.Position;
            string postureType = "-";
            string containerId = "0";
            string containerType = "-";
            Posture posture = sim.Posture;
            if (posture != null)
            {
                postureType = posture.GetType().Name;
                if (postureType.EndsWith("PuppetPosture", StringComparison.Ordinal))
                {
                    // presenter: the server's posture this copy stands for (its Name is the HUD's display name)
                    FieldInfo serverName = posture.GetType().GetField("ServerName");
                    postureType = serverName != null ? (string)serverName.GetValue(posture) : posture.Name;
                }
                IGameObject container = posture.Container;
                if (container != null)
                {
                    containerId = container.ObjectId.Value.ToString(CultureInfo.InvariantCulture);
                    containerType = container.GetType().Name;
                }
            }

            // Animation: the state machines driving the Sim (posture idle/sit, and the running interaction's).
            // Native active-clip and SACS debug queries are empty in release builds ( probe).
            string postureMachine = (posture != null && posture.CurrentStateMachine != null) ? posture.CurrentStateMachine.Name : null;
            if (postureMachine == null && posture != null && posture.GetType().Name.EndsWith("PuppetPosture", StringComparison.Ordinal))
            {
                postureMachine = ReplayedPostureMachine(sim.ObjectId.Value);
            }
            InteractionInstance current = sim.CurrentInteraction;
            string interactionMachine = (current != null && current.mCurrentStateMachine != null) ? current.mCurrentStateMachine.Name : null;

            GameObject right = sim.GetObjectInRightHand();
            GameObject left = sim.GetObjectInLeftHand();
            CultureInfo c = CultureInfo.InvariantCulture;
            // a game may hide a Sim, or keep it at low LOD, on a lot it does not simulate (a player's Sim on
            // another lot); the hidden flags (hex, 0 = drawn) and the autonomy LOD let presentation_compare judge "shown".
            return wireId.ToString(c) + "|" + sim.SimDescription.SimDescriptionId.ToString(c) + "|" + lotId.ToString(c) + "|"
                + position.x.ToString("0.###", c) + "|" + position.y.ToString("0.###", c) + "|" + position.z.ToString("0.###", c) + "|"
                + PresentationField(postureType) + "|" + containerId + "|" + PresentationField(containerType) + "|"
                + PresentationField(CurrentInteractionKey(sim)) + "|" + PresentationField(postureMachine) + "|" + PresentationField(interactionMachine) + "|"
                + PresentationField(right != null ? right.GetType().Name : null) + "|" + PresentationField(left != null ? left.GetType().Name : null) + "|"
                + SacsField(sim.ObjectId.Value) + "|" + sim.CurrentOutfitCategory.ToString() + ":" + sim.CurrentOutfitIndex.ToString(c) + "|"
                + World.ObjectGetHiddenFlags(sim.ObjectId).ToString("x", c) + "|"
                + (sim.Autonomy == null ? "-" : (sim.Autonomy.GetCachedHighLODValue ? "high" : "low"));
        }

        // Presenter: a puppet's posture has no StateMachineClient of its own; the replay drives the server's posture
        // machine through a driver acquired (or borrowed) here. The probe reports that machine's name only while the
        // server's driver (Po machineDriver) maps to a client driver that is alive (the check StateMachineClient.IsValid
        // makes), so anim_posture on a client means "a live replayed copy of the server's posture machine animates it".
        private static readonly Dictionary<ulong, uint> sPostureMachineDrivers = new Dictionary<ulong, uint>();
        private static readonly Dictionary<ulong, string> sPostureMachineNames = new Dictionary<ulong, string>();

        private static string ReplayedPostureMachine(ulong simId)
        {
            uint serverDriver;
            uint clientDriver;
            string name;
            if (!sPostureMachineDrivers.TryGetValue(simId, out serverDriver) || serverDriver == 0u
                || !sReplayDrivers.TryGetValue(serverDriver, out clientDriver)
                || !sPostureMachineNames.TryGetValue(simId, out name))
            {
                return null;
            }

            try
            {
                ISacsProxy proxy = SacsRecorder.Current;
                return proxy != null && proxy.IsEventQueueValid(clientDriver) ? name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The driver handle of a StateMachineClient, as the SacsRecorder's lines name it (0 when unknown).
        private static uint DriverHandleOf(StateMachineClient client)
        {
            AdoptClient(null); // binds the reflection fields
            if (client == null || sSmcDriver == null || sSmcDriverHandle == null)
            {
                return 0u;
            }

            return (uint)sSmcDriverHandle.GetValue(sSmcDriver.GetValue(client));
        }

        private static Sim FindSimByDescription(ulong simDescriptionId)
        {
            foreach (Sim candidate in LotManager.Actors)
            {
                if (candidate != null && candidate.SimDescription != null && candidate.SimDescription.SimDescriptionId == simDescriptionId)
                {
                    return candidate;
                }
            }

            return null;
        }

        // The id a snapshot reports for a Sim: the server's ObjectId when this client holds an alias
        // for the Sim's description, else its own.
        private static ulong WireSimObjectId(Sim sim)
        {
            ulong serverObjectId;
            if (sSimAliasByDescription.Count > 0
                && sim.SimDescription != null
                && sSimAliasByDescription.TryGetValue(sim.SimDescription.SimDescriptionId, out serverObjectId))
            {
                return serverObjectId;
            }

            return sim.ObjectId.Value;
        }

        // An interaction target named by the server: an aliased id is a Sim, found by description.
        private static GameObject FindTargetObject(ulong objectId)
        {
            if (sSimAliases.Count > 0 && sSimAliases.ContainsKey(objectId))
            {
                return FindSim(objectId);
            }

            return new ObjectGuid(objectId).ObjectFromId<GameObject>();
        }

        // W2 (docs/server-authoritative-plan.md): SET_AUTHORITY|corr|mode|owned -- the client authority
        // gate in the forked gameplay assembly (SimsOnline.Hooks.AuthorityHook). mode = off | observe |
        // enforce; owned = the Sims this client owns (SimScopeWire id list). In enforce mode a Sim the
        // client does not own only queues interactions inserted while a server command runs (the
        // dispatch window above). Stock gameplay: hook=absent, no-op.
        private static Type sAuthorityHookType;
        private static bool sAuthorityHookResolved;
        private static MethodInfo sAuthorityBegin;
        private static MethodInfo sAuthorityEnd;

        // SACS_RECORDER authority: "authority;Mode=2;Checked=..;Blocked=..;..." from AuthorityHook's public static int and
        // long fields (not its constants), read when asked; "authority;hook=absent" on stock gameplay.
        private static string AuthorityCounters()
        {
            Type hook = AuthorityHook();
            if (hook == null)
            {
                return "authority;hook=absent";
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            List<string> parts = new List<string>();
            parts.Add("authority");
            try
            {
                foreach (FieldInfo field in hook.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.IsLiteral)
                    {
                        continue;
                    }

                    if (field.FieldType == typeof(long))
                    {
                        parts.Add(field.Name + "=" + ((long)field.GetValue(null)).ToString(c));
                    }
                    else if (field.FieldType == typeof(int))
                    {
                        parts.Add(field.Name + "=" + ((int)field.GetValue(null)).ToString(c));
                    }
                }
            }
            catch (Exception ex)
            {
                parts.Add("error=" + ex.GetType().Name);
            }

            return string.Join(";", parts.ToArray());
        }

        private static Type AuthorityHook()
        {
            if (!sAuthorityHookResolved)
            {
                sAuthorityHookType = FindGameplayType("SimsOnline.Hooks.AuthorityHook");
                if (sAuthorityHookType != null)
                {
                    sAuthorityBegin = sAuthorityHookType.GetMethod("BeginServerApply");
                    sAuthorityEnd = sAuthorityHookType.GetMethod("EndServerApply");
                }
                sAuthorityHookResolved = true;
            }

            return sAuthorityHookType;
        }

        private static void BeginServerApply()
        {
            try
            {
                if (AuthorityHook() != null)
                {
                    sAuthorityBegin.Invoke(null, null);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static void EndServerApply()
        {
            try
            {
                if (AuthorityHook() != null)
                {
                    sAuthorityEnd.Invoke(null, null);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static void HandleSetAuthority(BridgeCommand command)
        {
            if (command.Parts.Length < 4)
            {
                throw new InvalidOperationException("SET_AUTHORITY requires mode and owned fields.");
            }

            string modeText = command.Parts[2];
            int mode = modeText == "off" ? 0 : modeText == "observe" ? 1 : modeText == "enforce" ? 2 : -1;
            ulong[] owned = SimScopeWire.Parse(command.Parts[3]);
            if (mode < 0 || owned == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SET_AUTHORITY mode must be off|observe|enforce and owned a SimScopeWire id list."));
                return;
            }

            sAuthorityOwned = owned;
            Type hook = AuthorityHook();
            if (hook == null)
            {
                SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "hook=absent; mode=" + modeText + "; owned=" + SimScopeWire.Format(owned)));
                return;
            }

            hook.GetMethod("SetOwnedSims").Invoke(null, new object[] { owned });
            hook.GetMethod("SetMode").Invoke(null, new object[] { mode });
            string state = (string)hook.GetMethod("Describe").Invoke(null, null);
            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "hook=present; " + state));
        }

        // W2 part B (internal documentation): mirror the visible state of Sims a client does
        // not own. PROBE_SIM_STATE|corr|<ids> (SimScopeWire list, at most SimStateWire.MaxSimsPerProbe
        // used) replies AUTONOMY_PROBE ok with SimStateWire entries joined by ';' (missing Sims left out).
        // APPLY_SIM_STATE|corr|<entry> (client role only) sets the six motives and reconciles the buff set
        // to the server's: removes buffs the server does not have, adds the ones it has (with origin).
        private static CommodityKind[] sMirroredMotives;

        private static CommodityKind[] MirroredMotives()
        {
            if (sMirroredMotives == null)
            {
                CommodityKind[] kinds = new CommodityKind[SimStateWire.MotiveNames.Length];
                for (int i = 0; i < kinds.Length; i++)
                {
                    kinds[i] = (CommodityKind)Enum.Parse(typeof(CommodityKind), SimStateWire.MotiveNames[i], true);
                }

                sMirroredMotives = kinds;
            }

            return sMirroredMotives;
        }

        private static void HandleProbeSimState(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("PROBE_SIM_STATE requires a Sim id list.");
            }

            ulong[] ids = SimScopeWire.Parse(command.Parts[2]);
            if (ids == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "PROBE_SIM_STATE ids must be decimal, comma-separated."));
                return;
            }

            CommodityKind[] kinds = MirroredMotives();
            StringBuilder text = new StringBuilder();
            int written = 0;
            for (int i = 0; i < ids.Length && written < SimStateWire.MaxSimsPerProbe; i++)
            {
                Sim sim = FindSim(ids[i]);
                if (sim == null || sim.Motives == null)
                {
                    continue;
                }

                float[] motives = new float[kinds.Length];
                for (int k = 0; k < kinds.Length; k++)
                {
                    motives[k] = sim.Motives.HasMotive(kinds[k]) ? sim.Motives.GetValue(kinds[k]) : 0f;
                }

                List<ulong> guids = new List<ulong>();
                List<ulong> origins = new List<ulong>();
                if (sim.BuffManager != null)
                {
                    foreach (BuffInstance buff in sim.BuffManager.List)
                    {
                        guids.Add((ulong)buff.Guid);
                        origins.Add((ulong)buff.BuffOrigin);
                    }
                }

                if (written > 0)
                {
                    text.Append(';');
                }

                text.Append(SimStateWire.FormatEntry(ids[i], motives, guids.ToArray(), origins.ToArray()));
                written++;
            }

            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", text.Length == 0 ? "-" : text.ToString()));
        }

        private static void HandleApplySimState(BridgeCommand command)
        {
            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_SIM_STATE is client-role only."));
                return;
            }

            ulong simId;
            float[] motives;
            ulong[] guids;
            ulong[] origins;
            if (command.Parts.Length < 3 || !SimStateWire.TryParseEntry(command.Parts[2], out simId, out motives, out guids, out origins))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_SIM_STATE requires one SimStateWire entry."));
                return;
            }

            Sim sim = FindSim(simId);
            if (sim == null || sim.Motives == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + simId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            CommodityKind[] kinds = MirroredMotives();
            int set = 0;
            for (int k = 0; k < kinds.Length && k < motives.Length; k++)
            {
                if (sim.Motives.HasMotive(kinds[k]))
                {
                    sim.Motives.SetValue(kinds[k], motives[k]);
                    set++;
                }
            }

            int added = 0;
            int removed = 0;
            if (sim.BuffManager != null)
            {
                Dictionary<ulong, ulong> target = new Dictionary<ulong, ulong>();
                for (int i = 0; i < guids.Length; i++)
                {
                    target[guids[i]] = origins[i];
                }

                List<ulong> current = new List<ulong>();
                foreach (BuffInstance buff in sim.BuffManager.List)
                {
                    current.Add((ulong)buff.Guid);
                }

                for (int i = 0; i < current.Count; i++)
                {
                    if (!target.ContainsKey(current[i]))
                    {
                        sim.BuffManager.RemoveElement(current[i]);
                        removed++;
                    }
                }

                foreach (KeyValuePair<ulong, ulong> entry in target)
                {
                    if (!sim.BuffManager.HasElement(entry.Key) && sim.BuffManager.AddElement(entry.Key, (Origin)entry.Value))
                    {
                        added++;
                    }
                }
            }

            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                "sim=" + simId.ToString(CultureInfo.InvariantCulture) + "; motivesSet=" + set.ToString(CultureInfo.InvariantCulture)
                + "; buffsAdded=" + added.ToString(CultureInfo.InvariantCulture) + "; buffsRemoved=" + removed.ToString(CultureInfo.InvariantCulture)));
        }

        // The forked gameplay assembly keeps the stock identity (Sims3GameplaySystems), so a hook
        // type is found by name there; the side-by-side gameplay_02_fork copy is never consulted.
        private static Type FindGameplayType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == "Sims3GameplaySystems")
                {
                    return assembly.GetType(fullName, false);
                }
            }

            return null;
        }

        // per-value try/catch wrapper for live-object inspection — a diagnostic must
        // never crash the pump because one getter threw mid-simulation.
        private delegate string DiagValue();

        private static string SafeDiag(DiagValue value)
        {
            try
            {
                string result = value();
                return result == null ? "null" : result;
            }
            catch (Exception ex)
            {
                return "!" + ex.GetType().Name;
            }
        }

        // read a (possibly private) instance field via reflection, walking the base
        // type chain (GetField does not surface private fields of base classes).
        private static string ReadFieldAsString(object target, string fieldName)
        {
            try
            {
                if (target == null)
                {
                    return "null";
                }

                for (Type type = target.GetType(); type != null; type = type.BaseType)
                {
                    System.Reflection.FieldInfo field = type.GetField(
                        fieldName,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (field != null)
                    {
                        object value = field.GetValue(target);
                        return value == null ? "null" : value.ToString();
                    }
                }

                return "!no-field";
            }
            catch (Exception ex)
            {
                return "!" + ex.GetType().Name;
            }
        }

        // (DIAG_QUEUE): full interaction-queue dump with exit reasons. Read-only,
        // accepted in any role. First customer: "why did this interaction die"
        // becomes observable instead of inferred from a one-line InteractionFailed event.
        private static void HandleDiagQueue(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("DIAG_QUEUE requires a Sim object id.");
            }

            ulong simObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            Sim sim = FindSim(simObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.DiagQueueReport(command.CorrelationId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=not-found"));
                return;
            }

            StringBuilder report = new StringBuilder();
            report.Append("sim=").Append(simObjectId.ToString(CultureInfo.InvariantCulture));
            report.Append(";name=").Append(SafeDiag(delegate { return sim.FullName; }));
            // Sim.mExitReason is the actor-level exit-reason flags ReactionInteractions and
            // cancellations write into (the suspect channel).
            report.Append(";simExitReason=").Append(ReadFieldAsString(sim, "mExitReason"));
            // a SocializingPosture here with nothing running/transitioning is the
            // orphaned-posture freeze signature (see RepairOrphanedSocializingPosture).
            report.Append(";posture=").Append(SafeDiag(delegate { return sim.Posture == null ? "null" : sim.Posture.GetType().Name; }));

            InteractionQueue queue = sim.InteractionQueue;
            if (queue == null)
            {
                report.Append(";queue=null");
            }
            else
            {
                report.Append(";count=").Append(SafeDiag(delegate { return queue.Count.ToString(CultureInfo.InvariantCulture); }));

                InteractionInstance running = null;
                try
                {
                    running = queue.RunningInteraction;
                }
                catch (Exception)
                {
                    // DIAG_QUEUE: an unreadable running interaction reads as none.
                }

                InteractionInstance transition = null;
                try
                {
                    transition = queue.TransitionInteraction;
                }
                catch (Exception)
                {
                    // DIAG_QUEUE: an unreadable transition reads as none.
                }

                if (transition != null)
                {
                    report.Append("\ntransition:");
                    AppendInteractionDetail(report, transition, running);
                }

                int index = 0;
                foreach (InteractionInstance instance in queue.InteractionList)
                {
                    if (index >= 32)
                    {
                        report.Append("\n[truncated at 32]");
                        break;
                    }

                    report.Append("\n[").Append(index.ToString(CultureInfo.InvariantCulture)).Append("]");
                    AppendInteractionDetail(report, instance, running);
                    index++;
                }

                if (index == 0)
                {
                    report.Append("\n(queue empty)");
                }
            }

            SendChunkedSnapshot(command.CorrelationId, BridgeCodec.DiagQueueReport(command.CorrelationId, "ok", report.ToString()));
        }

        private static void AppendInteractionDetail(StringBuilder report, InteractionInstance instance, InteractionInstance running)
        {
            if (instance == null)
            {
                report.Append(" null");
                return;
            }

            report.Append(" type=").Append(SafeDiag(delegate { return instance.GetType().FullName; }));
            report.Append(";def=").Append(SafeDiag(delegate { return instance.InteractionDefinition == null ? "-" : instance.InteractionDefinition.GetType().FullName; }));
            report.Append(";id=").Append(SafeDiag(delegate { return instance.Id.ToString(); }));
            report.Append(";priority=").Append(SafeDiag(delegate { InteractionPriority priority = instance.GetPriority(); return priority.Level.ToString() + "/" + Float(priority.Value); }));
            report.Append(";state=").Append(ReadFieldAsString(instance, "mInteractionState"));
            report.Append(";cancelled=").Append(SafeDiag(delegate { return instance.Cancelled.ToString(); }));
            report.Append(";cancelReason=").Append(SafeDiag(delegate { return instance.CancelledReason.ToString(); }));
            report.Append(";mustRun=").Append(SafeDiag(delegate { return instance.MustRun.ToString(); }));
            report.Append(";autonomous=").Append(SafeDiag(delegate { return instance.Autonomous.ToString(); }));
            report.Append(";target=").Append(SafeDiag(delegate { return instance.Target == null ? "-" : instance.Target.GetType().Name; }));
            if (object.ReferenceEquals(instance, running))
            {
                report.Append(";RUNNING");
            }
        }

        // (DIAG_RESOLVE): try every known Sim/object resolution path and report which
        // succeed — settles the Bebe Hart class of "Sim not found" failures (off-lot Sims
        // unresolvable via ObjectFromId). Read-only, any role.
        private static void HandleDiagResolve(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("DIAG_RESOLVE requires an object id.");
            }

            ulong objectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            StringBuilder report = new StringBuilder();
            report.Append("object=").Append(objectId.ToString(CultureInfo.InvariantCulture));

            Sim lotManagerHit = null;
            int actorsEnumerated = 0;
            try
            {
                foreach (Sim candidate in LotManager.Actors)
                {
                    actorsEnumerated++;
                    if (candidate != null && candidate.ObjectId.Value == objectId)
                    {
                        lotManagerHit = candidate;
                    }
                }

                report.Append(";lotManagerActors=").Append(lotManagerHit != null ? "HIT" : "miss");
                report.Append(";actorsEnumerated=").Append(actorsEnumerated.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                report.Append(";lotManagerActors=!").Append(ex.GetType().Name);
            }

            ObjectGuid guid = new ObjectGuid(objectId);
            Sim proxySim = null;
            try
            {
                proxySim = guid.ObjectFromId<Sim>();
                report.Append(";objectFromIdSim=").Append(proxySim != null ? "HIT" : "miss");
            }
            catch (Exception ex)
            {
                report.Append(";objectFromIdSim=!").Append(ex.GetType().Name);
            }

            try
            {
                Sims3.Gameplay.Abstracts.GameObject proxyObject = guid.ObjectFromId<Sims3.Gameplay.Abstracts.GameObject>();
                report.Append(";objectFromIdGameObject=").Append(proxyObject != null ? "HIT (" + proxyObject.GetType().Name + ")" : "miss");
            }
            catch (Exception ex)
            {
                report.Append(";objectFromIdGameObject=!").Append(ex.GetType().Name);
            }

            Sim resolved = lotManagerHit != null ? lotManagerHit : proxySim;
            if (resolved != null)
            {
                report.Append(";name=").Append(SafeDiag(delegate { return resolved.FullName; }));
                report.Append(";pos=").Append(SafeDiag(delegate { return Vector(resolved.Position); }));
                report.Append(";onLot=").Append(SafeDiag(delegate { return (resolved.LotCurrent != null).ToString(); }));
            }

            SendLine(BridgeCodec.DiagResolveReport(command.CorrelationId, "ok", report.ToString()));
        }

        // (DIAG_WALK): bounded reflection field walk of a live object — the engine's
        // own ScriptError.GetObjectFieldData proves recursive field reflection is supported by
        // this runtime. FIELDS ONLY during recursion (property getters can run arbitrary code
        // mid-simulation); explicit path segments may use a property getter because the
        // operator asked for it by name. Depth/node bounded so the pump tick never stalls.
        private static void HandleDiagWalk(BridgeCommand command)
        {
            if (command.Parts.Length < 5)
            {
                throw new InvalidOperationException("DIAG_WALK requires object id, hex path (empty ok), and depth.");
            }

            ulong objectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            // "-" = walk from the root (REPL-friendly alias for an empty hex path).
            string path = command.Parts[3] == "-" || command.Parts[3].Length == 0
                ? ""
                : BridgeCodec.DecodeText(command.Parts[3]);
            int depth = int.Parse(command.Parts[4], CultureInfo.InvariantCulture);
            if (depth < 1)
            {
                depth = 1;
            }

            if (depth > 6)
            {
                depth = 6;
            }

            int maxNodes = 400;
            if (command.Parts.Length >= 6)
            {
                maxNodes = int.Parse(command.Parts[5], CultureInfo.InvariantCulture);
                if (maxNodes < 1)
                {
                    maxNodes = 1;
                }

                if (maxNodes > 2000)
                {
                    maxNodes = 2000;
                }
            }

            object root = FindSim(objectId);
            if (root == null)
            {
                try
                {
                    root = new ObjectGuid(objectId).ObjectFromId<Sims3.Gameplay.Abstracts.GameObject>();
                }
                catch (Exception)
                {
                    // DIAG_WALK: an id that does not resolve is reported as not-resolvable below.
                }
            }

            if (root == null)
            {
                SendLine(BridgeCodec.DiagWalkReport(command.CorrelationId, "err", "object=" + objectId.ToString(CultureInfo.InvariantCulture) + ";error=not-resolvable"));
                return;
            }

            if (path.Length > 0)
            {
                string[] segments = path.Split('.');
                for (int i = 0; i < segments.Length; i++)
                {
                    object next = GetMemberValue(root, segments[i]);
                    if (next == null)
                    {
                        SendLine(BridgeCodec.DiagWalkReport(command.CorrelationId, "err", "error=path-null-or-missing;segment=" + segments[i]));
                        return;
                    }

                    root = next;
                }
            }

            StringBuilder report = new StringBuilder();
            report.Append("root=").Append(root.GetType().FullName).Append(";path=").Append(path.Length > 0 ? path : "(root)");
            int nodes = 0;
            List<object> visited = new List<object>();
            WalkFields(report, "", root, depth, ref nodes, maxNodes, visited);
            if (nodes >= maxNodes)
            {
                report.Append("\n[truncated at ").Append(maxNodes.ToString(CultureInfo.InvariantCulture)).Append(" nodes]");
            }

            SendChunkedSnapshot(command.CorrelationId, BridgeCodec.DiagWalkReport(command.CorrelationId, "ok", report.ToString()));
        }

        // ---------------------------------------------------------------------------------
        // (T5): scratch-probe hot-injection. Inbound blob reassembly + Assembly.Load.
        // All of this runs on the simulator pump (ProcessCommandLine is driven by
        // BridgeAlarmTick / OneShotFunctionTask on Simulator), so loaded probe code executes
        // on the game thread with no Thread.Start. NO assembly unload in .NET 2 — every load
        // is resident; uploaders MUST stamp a unique assembly name per upload.
        // ---------------------------------------------------------------------------------
        private static string sBlobId;
        private static int sBlobCount;
        private static int sBlobReceived;
        private static StringBuilder sBlobHex;
        private static byte[] sLastBlobBytes;
        private static string sLastBlobId;
        private static readonly RustPrivateNavigation.BlobStore sNavBlobs = new RustPrivateNavigation.BlobStore();

        internal static void NavigationReply(string correlation, string body)
        {
            SendLine(BridgeCodec.AutonomyProbe(correlation, "ok", body));
        }

        // part 2: DIAG_RERUN caches the LAST resolved probe Run() so it can be re-invoked
        // without a fresh Assembly.Load + GetType + GetMethod. The cheap inner step of the
        // observe -> mutate -> re-observe loop; set by LoadAndRun, consumed by HandleDiagRerun.
        private static System.Reflection.MethodInfo sLastRunMethod;

        // cached reflection for internal TS3 methods used by travel-merge.
        private static System.Reflection.MethodInfo sHouseholdMergeTravelDataMethod;
        private static System.Reflection.MethodInfo sBinImportSimMethod;
        private static string sLastRunTypeName;
        private static string sLastRunLabel;
        private static string sLastRunAsmName;

        // T5a: one wire command -> one handler. The dispatch table maps command.Name to
        // a BridgeHandler; it is the seam the hot-reload design ( part 2) swaps to replace
        // the hot-swappable handler set live. Lazily built in ProcessCommandLine (retryable, not
        // readonly/Lazy) so a future null after a swap rebuilds rather than fossilizing.
        private delegate void BridgeHandler(BridgeCommand command);
        private static Dictionary<string, BridgeHandler> sDispatch;

        // Phase 2a inbound: controller writes payload into a ctl bulk slot, then sends
        // BULK_REF|corr|probe-upload|slot|totalLength|crc|hexBlobId on the control ring.
        // Falls back to DIAG_BLOB chunk upload when v11 bulk is unavailable.
        private static void HandleBulkRef(BridgeCommand command)
        {
            if (command.Parts.Length < 6)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "BULK_REF needs kind|slot|totalLength|crc"));
                return;
            }

            string kind = command.Parts[2];
            if (kind != "probe-upload")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "unsupported BULK_REF kind: " + kind));
                return;
            }

            int slotIndex;
            int totalLength;
            uint crc32;
            try
            {
                slotIndex = int.Parse(command.Parts[3], CultureInfo.InvariantCulture);
                totalLength = int.Parse(command.Parts[4], CultureInfo.InvariantCulture);
                crc32 = uint.Parse(command.Parts[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            catch
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "BULK_REF parse failed"));
                return;
            }

            string blobId = command.CorrelationId;
            if (command.Parts.Length >= 7 && command.Parts[6] != null && command.Parts[6].Length > 0)
            {
                try
                {
                    blobId = BridgeCodec.DecodeText(command.Parts[6]);
                }
                catch
                {
                    SendLine(BridgeCodec.Error(command.CorrelationId, "BULK_REF blobId decode failed"));
                    return;
                }
            }

            byte[] payload;
            if (!BridgeBulkBuffer.TryConsumeCtlBulk(slotIndex, totalLength, crc32, out payload) || payload == null)
            {
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "err", "ctl bulk read failed slot=" + slotIndex.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            sLastBlobBytes = payload;
            sLastBlobId = blobId;
            long checksum = 0;
            for (int i = 0; i < payload.Length; i++)
            {
                checksum = (checksum + payload[i]) & 0xFFFFFFFF;
            }

            string report = "blobId=" + blobId + ";bytes=" + payload.Length.ToString(CultureInfo.InvariantCulture)
                + ";checksum=" + checksum.ToString(CultureInfo.InvariantCulture) + ";state=complete;transport=bulk";
            SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "ok", report));
        }

        // DIAG_BLOB|corrId|blobId|index|count|hexPayload — inbound chunk reassembly (net-new;
        // outbound SNAPSHOT_CHUNK existed, inbound did not). Each chunk is an independent
        // command with its own corrId and its own reply, so the controller's single-reader
        // correlation is satisfied per frame.
        private static void HandleDiagBlob(BridgeCommand command)
        {
            if (command.Parts.Length < 6)
            {
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "err", "DIAG_BLOB needs blobId|index|count|hexPayload"));
                return;
            }

            string blobId = command.Parts[2];
            if (blobId.StartsWith("nav-", StringComparison.Ordinal))
            {
                int navIndex, navCount;
                byte[] complete;
                bool accepted = int.TryParse(command.Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out navIndex)
                    && int.TryParse(command.Parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out navCount)
                    && sNavBlobs.Add(blobId, navIndex, navCount, command.Parts[5], out complete);
                // Never touch sBlob* or sLastBlob*: a probe upload may be in flight concurrently.
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, accepted ? "ok" : "err",
                    accepted ? "navigation chunk accepted" : "navigation chunk refused"));
                return;
            }
            int index = int.Parse(command.Parts[3], CultureInfo.InvariantCulture);
            int count = int.Parse(command.Parts[4], CultureInfo.InvariantCulture);
            string hexPayload = command.Parts[5];

            if (index == 0)
            {
                sBlobId = blobId;
                sBlobCount = count;
                sBlobReceived = 0;
                sBlobHex = new StringBuilder();
            }
            else if (sBlobHex == null || sBlobId != blobId)
            {
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "err", "no upload in progress for blobId=" + blobId + " (send index 0 first)"));
                return;
            }
            else if (index != sBlobReceived)
            {
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "err", "out-of-order chunk: got index=" + index.ToString(CultureInfo.InvariantCulture) + " expected " + sBlobReceived.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            sBlobHex.Append(hexPayload);
            sBlobReceived++;

            if (sBlobReceived >= sBlobCount)
            {
                try
                {
                    sLastBlobBytes = BridgeCodec.DecodeHexBytes(sBlobHex.ToString());
                    sLastBlobId = blobId;
                    long checksum = 0;
                    for (int i = 0; i < sLastBlobBytes.Length; i++)
                    {
                        checksum = (checksum + sLastBlobBytes[i]) & 0xFFFFFFFF;
                    }
                    string report = "blobId=" + blobId + ";chunks=" + sBlobCount.ToString(CultureInfo.InvariantCulture)
                        + ";bytes=" + sLastBlobBytes.Length.ToString(CultureInfo.InvariantCulture)
                        + ";checksum=" + checksum.ToString(CultureInfo.InvariantCulture) + ";state=complete";
                    sBlobHex = null;
                    SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "ok", report));
                }
                catch (Exception ex)
                {
                    sBlobHex = null;
                    SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "err", "decode failed: " + ex.GetType().Name + ": " + ex.Message));
                }
            }
            else
            {
                SendLine(BridgeCodec.DiagBlobReport(command.CorrelationId, "ok", "blobId=" + blobId + ";received=" + sBlobReceived.ToString(CultureInfo.InvariantCulture) + "/" + sBlobCount.ToString(CultureInfo.InvariantCulture) + ";state=partial"));
            }
        }

        // DIAG_LOADTEST|corrId — phase-1 gate: Assembly.Load(byte[]) the embedded test
        // assembly and invoke its public static string Run(). Green here = the mono boundary
        // is open and phase-2 (uploaded probes) is worth building. Self-contained: needs no
        // prior DIAG_BLOB upload, so it can run the instant the mod boots.
        private static void HandleDiagLoadTest(BridgeCommand command)
        {
            byte[] bytes;
            try
            {
                bytes = BridgeCodec.DecodeHexBytes(EmbeddedLoadTestAssembly.Hex);
            }
            catch (Exception ex)
            {
                SendLine(BridgeCodec.DiagLoadTestReport(command.CorrelationId, "err", "embedded hex decode failed: " + ex.GetType().Name + ": " + ex.Message));
                return;
            }

            string report = LoadAndRun(bytes, "SimsOnline.HotProbe.EmbeddedLoadTest", "embedded");
            string kind = report.StartsWith("!") ? "err" : "ok";
            SendLine(BridgeCodec.DiagLoadTestReport(command.CorrelationId, kind, report));
        }

        // DIAG_RUN|corrId|typeName — phase-2: load the last uploaded blob and invoke
        // typeName.Run() (public static string). The probe convention is intentionally a bare
        // static method, not a shared interface, so probe .cs files need zero extra references.
        private static void HandleDiagRun(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, "err", "DIAG_RUN needs a type name"));
                return;
            }

            string typeName = BridgeCodec.DecodeText(command.Parts[2]);
            if (sLastBlobBytes == null)
            {
                SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, "err", "no blob uploaded (DIAG_BLOB first)"));
                return;
            }

            string report = LoadAndRun(sLastBlobBytes, typeName, sLastBlobId == null ? "uploaded" : sLastBlobId);
            string kind = report.StartsWith("!") ? "err" : "ok";
            SendChunkedSnapshot(command.CorrelationId, BridgeCodec.DiagRunReport(command.CorrelationId, kind, report));
        }

        // DIAG_RERUN|corrId — part 2: re-invoke the LAST resolved probe Run() with no
        // re-Assembly.Load / GetType / GetMethod. The cheap inner step of the observe -> mutate ->
        // re-observe loop (mutate live state via APPLY_*, then re-read with a single DIAG_RERUN).
        // Errors if no probe has been loaded this session (DIAG_RUN must come first).
        private static void HandleDiagRerun(BridgeCommand command)
        {
            if (sLastRunMethod == null)
            {
                SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, "err", "no probe loaded yet (DIAG_RUN first)"));
                return;
            }

            string report = InvokeRun(sLastRunMethod, sLastRunLabel, sLastRunAsmName, sLastRunTypeName);
            string kind = report.StartsWith("!") ? "err" : "ok";
            SendChunkedSnapshot(command.CorrelationId, BridgeCodec.DiagRunReport(command.CorrelationId, kind, report));
        }

        // DIAG_HANDLERTEST|corrId — / T5b FEASIBILITY SPIKE. Loads the last uploaded blob
        // (a SpikeHandlerSet built with engine-only refs, no shell reference) and invokes
        // RunCtx(object), passing a System.Converter<object[],object> wrapping HandlerBridge.Service.
        // Proves the single load-bearing unknown for the whole shell/handler split: does an
        // mscorlib-generic carrier survive Assembly.Load(byte[]) in TS3 mono and cast cleanly inside
        // the separately-loaded handler, with engine types (Sim) round-tripping back boxed.
        // GREEN: "cast=ok;invoke=ok;role=server;findSim=Sim:<name>". RED (cast=FAILED / !RunCtx threw)
        // => carrier does not unify; fall back to a boxed System.Delegate + DynamicInvoke (same
        // no-shared-types family). Reuses the proven DIAG_BLOB upload path (sLastBlobBytes).
        private static void HandleDiagHandlerTest(BridgeCommand command)
        {
            if (sLastBlobBytes == null)
            {
                SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, "err", "no blob uploaded (DIAG_BLOB the SpikeHandlerSet first)"));
                return;
            }

            System.Reflection.Assembly assembly;
            try
            {
                assembly = System.Reflection.Assembly.Load(sLastBlobBytes);
            }
            catch (Exception ex)
            {
                SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, "err", "!Assembly.Load threw: " + ex.GetType().FullName + ": " + ex.Message));
                return;
            }

            Type type = assembly.GetType("SimsOnline.Handlers.SpikeHandlerSet");
            if (type == null)
            {
                SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, "err", "!type not found: SimsOnline.Handlers.SpikeHandlerSet [asm=" + assembly.FullName + "]"));
                return;
            }

            System.Reflection.MethodInfo runCtx = type.GetMethod(
                "RunCtx",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (runCtx == null)
            {
                SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, "err", "!no public static RunCtx(object) on SpikeHandlerSet"));
                return;
            }

            // The carrier: an mscorlib generic delegate over the shell-side service switch. Boxed to
            // object through Invoke; the handler casts it back to Converter<object[],object>.
            System.Converter<object[], object> svc = new System.Converter<object[], object>(HandlerBridge.Service);

            object result;
            try
            {
                result = runCtx.Invoke(null, new object[] { svc });
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException != null ? ex.InnerException : ex;
                SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, "err", "!RunCtx threw: " + inner.GetType().FullName + ": " + inner.Message));
                return;
            }

            string report = result == null ? "null" : result.ToString();
            string kind = report.StartsWith("!") || report.IndexOf("FAILED") >= 0 ? "err" : "ok";
            SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, kind, "asm=" + assembly.FullName + ";runCtx=" + report));
        }

        // / T5b: the cross-boundary service. Realizes "IBridgeContext" as a verb switch
        // reached through a System.Converter<object[],object> carrier rather than a shared interface
        // (interfaces are the unproven cross-assembly-identity trap). Only mscorlib + engine types
        // cross the boundary; BridgeCommand (shell-internal) never does. Phase B-1 surface; T5c ports
        // the full ~30-verb set. HAZARD: stringly-typed — always pass (ulong)id never bare int.
        // T5c: canonical verb names for the carrier. Shell-side only — the handler can't
        // reference this (no shared types across Assembly.Load), so it passes string literals; these
        // constants make the switch self-documenting and are the contract HandlerSet must match. A
        // handler-side typo falls through to the `default` case below with a clear "unknown verb".
        internal static class HandlerVerbs
        {
            public const string GetRole = "GetRole";
            public const string GetSpeed = "GetSpeed";
            public const string FindSim = "FindSim";
            public const string FindHousehold = "FindHousehold";
            public const string EncodeText = "EncodeText";
            public const string DecodeText = "DecodeText";
            public const string EnqueuePendingPositionApply = "EnqueuePendingPositionApply";
            public const string RepairOrphanedSocializingPosture = "RepairOrphanedSocializingPosture";
            public const string RouteHeld = "RouteHeld";
            public const string SendOk = "SendOk";
            public const string SendError = "SendError";
            public const string SendReply = "SendReply";
        }

        internal static class HandlerBridge
        {
            // T5c: typed + arity-checked arg extraction. The carrier is stringly-typed, so without
            // this a wrong arg count is an IndexOutOfRange and a wrong type an opaque InvalidCast.
            // Arg<T> turns both into a precise message naming the verb, arg index, expected vs actual
            // type — the failure surfaces through ProcessCommandLine's outer try/catch as a clean
            // error reply instead of a mystery. (C#2 generics: `is T` / `(T)v` unbox boxed value
            // types fine on .NET 2.)
            private static T Arg<T>(object[] a, string verb, int i)
            {
                if (i >= a.Length)
                {
                    throw new ArgumentException("service verb '" + verb + "' missing arg at index " + i.ToString(CultureInfo.InvariantCulture) +
                        " (call had " + a.Length.ToString(CultureInfo.InvariantCulture) + " element(s) incl. verb)");
                }
                object v = a[i];
                if (!(v is T))
                {
                    throw new ArgumentException("service verb '" + verb + "' arg " + i.ToString(CultureInfo.InvariantCulture) +
                        " expected " + typeof(T).Name + ", got " + (v == null ? "null" : v.GetType().Name));
                }
                return (T)v;
            }

            public static object Service(object[] a)
            {
                if (a == null || a.Length < 1 || !(a[0] is string))
                {
                    throw new ArgumentException("service call requires a string verb as arg 0");
                }
                string verb = (string)a[0];
                switch (verb)
                {
                    // authority / state (read-only to handler)
                    case HandlerVerbs.GetRole:
                        return sCurrentRole;
                    case HandlerVerbs.GetSpeed:
                        return sCurrentSpeed;
                    // resolution (engine types — shared by ref DLL, castable handler-side)
                    case HandlerVerbs.FindSim:
                        return FindSim(Arg<ulong>(a, verb, 1));
                    case HandlerVerbs.FindHousehold:
                        return FindHousehold(Arg<ulong>(a, verb, 1));
                    // codec free-text (hex wire encoding stays shell-side; BridgeCodec is internal)
                    case HandlerVerbs.EncodeText:
                        return BridgeCodec.EncodeText(Arg<string>(a, verb, 1));
                    case HandlerVerbs.DecodeText:
                        return BridgeCodec.DecodeText(Arg<string>(a, verb, 1));
                    // deferred state write (handler can't reach the shell's pending-apply queue)
                    case HandlerVerbs.EnqueuePendingPositionApply:
                        EnqueuePendingPositionApply(Arg<ulong>(a, verb, 1), Arg<Vector3>(a, verb, 2));
                        return null;
                    case HandlerVerbs.RepairOrphanedSocializingPosture:
                        return RepairOrphanedSocializingPosture(Arg<Sim>(a, verb, 1));
                    // a client route walks this Sim; the position mirror leaves it alone
                    case HandlerVerbs.RouteHeld:
                        return IsRouteHeld(Arg<ulong>(a, verb, 1), Arg<Vector3>(a, verb, 2));
                    // replies: built shell-side so the handler never names BridgeCodec (internal)
                    case HandlerVerbs.SendOk:
                        SendLine(BridgeCodec.Ok(Arg<string>(a, verb, 1)));
                        return null;
                    case HandlerVerbs.SendError:
                        SendLine(BridgeCodec.Error(Arg<string>(a, verb, 1), Arg<string>(a, verb, 2)));
                        return null;
                    case HandlerVerbs.SendReply:
                        SendHandlerReply(Arg<string>(a, verb, 1), Arg<string>(a, verb, 2), Arg<string>(a, verb, 3), Arg<string>(a, verb, 4));
                        return null;
                    default:
                        throw new NotSupportedException("unknown service verb: " + verb);
                }
            }
        }

        // T5b: kind-switched reply builder so the handler never names BridgeCodec (shell-internal).
        // probeKind selects the wire report shape; corrId/kind/report are the usual triplet.
        private static void SendHandlerReply(string probeKind, string corrId, string kind, string report)
        {
            switch (probeKind)
            {
                case "SimStatusProbe":
                    SendLine(BridgeCodec.SimStatusProbe(corrId, kind, report));
                    break;
                case "SimMotiveProbe":
                    SendLine(BridgeCodec.SimMotiveProbe(corrId, kind, report));
                    break;
                case "SimInteractionQueueProbe":
                    SendLine(BridgeCodec.SimInteractionQueueProbe(corrId, kind, report));
                    break;
                case "HouseholdCashProbe":
                    SendLine(BridgeCodec.HouseholdCashProbe(corrId, kind, report));
                    break;
                case "SimSkillProbe":
                    SendLine(BridgeCodec.SimSkillProbe(corrId, kind, report));
                    break;
                case "SimAutonomyProbe":
                    SendLine(BridgeCodec.SimAutonomyProbe(corrId, kind, report));
                    break;
                default:
                    SendLine(BridgeCodec.Error(corrId, "unknown reply kind: " + probeKind));
                    break;
            }
        }

        // T5b: the loaded swappable handler set. Resolved by reflection (by NAME — no shared type):
        // HandlerSet.Names() -> string[]; HandlerSet.Dispatch(name, parts, ctx) -> string. Loaded
        // LAZILY on first hot-command use (NOT at boot) so Assembly.Load + any diagnostic frames
        // never run during the CONNECT/HELLO/SESSION_READY handshake.
        private static System.Reflection.MethodInfo sHandlerDispatch;
        private static System.Converter<object[], object> sHandlerSvc;
        private static bool sHandlerLoadAttempted;
        private static string sHandlerLoadError;
        // T5d: how many handler assemblies have been loaded this session (1 = embedded cold-boot,
        // +1 per RELOAD_HANDLERS). .NET 2 has NO unload, so every reload stays resident; warn past a
        // soft threshold so a long hot-reload session knows to restart TS3 before it bloats.
        private static int sHandlerResidentCount;
        private const int kHandlerResidentWarnAt = 20;

        // Core load: Assembly.Load(bytes) -> find the entry type -> resolve Dispatch -> on success,
        // SWAP the cached dispatch + carrier and register any NEW swappable names the set declares.
        // Shared by the lazy embedded cold-boot load (EnsureHandlerLoaded) and the live RELOAD_HANDLERS
        // path. Runs on the pump (single-threaded); command processing is synchronous within
        // BridgeAlarmTick, so reassigning sHandlerDispatch here is atomic w.r.t. dispatch — no other
        // command can be mid-Invoke. Returns true on success; on failure leaves the prior handler (if
        // any) intact and sets `error`. entryTypeName lets a reloaded build override the type name.
        private static bool LoadHandlerAssembly(byte[] bytes, string entryTypeName, out string error)
        {
            error = null;
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.Load(bytes);
                Type type = asm.GetType(entryTypeName);
                if (type == null)
                {
                    error = "type not found " + entryTypeName;
                    return false;
                }

                System.Reflection.MethodInfo dispatchM = type.GetMethod(
                    "Dispatch", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (dispatchM == null)
                {
                    error = "Dispatch() not found on " + entryTypeName;
                    return false;
                }

                // Resolve Names() too (optional) so a reload can ADD swappable commands. A name that
                // collides with a shell-permanent command is skipped (never shadow the shell).
                System.Reflection.MethodInfo namesM = type.GetMethod(
                    "Names", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                // Swap is the last step, after every resolve succeeded — a half-loaded set never
                // becomes live. sHandlerSvc points at the shell's own Service, so it is build-stable;
                // reassign anyway for clarity.
                sHandlerSvc = new System.Converter<object[], object>(HandlerBridge.Service);
                sHandlerDispatch = dispatchM;
                sHandlerResidentCount++;

                if (namesM != null && sDispatch != null)
                {
                    string[] names = (string[])namesM.Invoke(null, null);
                    if (names != null)
                    {
                        for (int i = 0; i < names.Length; i++)
                        {
                            string name = names[i];
                            if (name == null || name.Length == 0)
                            {
                                continue;
                            }
                            BridgeHandler existing;
                            if (sDispatch.TryGetValue(name, out existing) && existing != DispatchToHandler)
                            {
                                continue;   // shell-permanent command — never let a handler shadow it
                            }
                            sDispatch[name] = DispatchToHandler;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // Lazy-load the embedded handler assembly on the pump, on FIRST hot-command use. Attempted at
        // most once (sHandlerLoadAttempted); a failure is recorded in sHandlerLoadError and reported
        // against the triggering command's corrId — never as an unsolicited frame. Returns true when
        // the handler dispatch is ready. NO sends on the success path (the handler sends its own
        // reply); this keeps the handshake stream clean.
        private static bool EnsureHandlerLoaded()
        {
            if (sHandlerDispatch != null)
            {
                return true;
            }
            if (sHandlerLoadAttempted)
            {
                return false;   // already failed once; sHandlerLoadError holds the reason
            }
            sHandlerLoadAttempted = true;

            byte[] bytes;
            try
            {
                bytes = BridgeCodec.DecodeHexBytes(EmbeddedHandlerAssembly.Hex);
            }
            catch (Exception ex)
            {
                sHandlerLoadError = "embedded hex decode failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            string error;
            if (!LoadHandlerAssembly(bytes, EmbeddedHandlerAssembly.EntryTypeName, out error))
            {
                sHandlerLoadError = error;
                return false;
            }
            return true;
        }

        // Shell-side BridgeHandler for every swappable command (registered by name in BuildDispatch).
        // Lazy-loads the handler assembly on first use, then forwards the command into it via the
        // reflection-resolved Dispatch + the Converter carrier. All errors carry the command's corrId.
        private static void DispatchToHandler(BridgeCommand command)
        {
            if (!EnsureHandlerLoaded())
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "handler-load-failed: " + (sHandlerLoadError != null ? sHandlerLoadError : "unknown")));
                return;
            }

            try
            {
                sHandlerDispatch.Invoke(null, new object[] { command.Name, command.Parts, sHandlerSvc });
                // The handler sent its own wire reply via the SendReply verb. The returned dispatch
                // breadcrumb ("ok"/"err:...") is informational only; the err reply already went out.
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException != null ? ex.InnerException : ex;
                SendLine(BridgeCodec.Error(command.CorrelationId, "handler threw: " + inner.GetType().FullName + ": " + inner.Message));
            }
        }

        // RELOAD_HANDLERS|corrId[|hexEntryTypeName] — / T5d, the live hot-swap payoff. Loads
        // the LAST DIAG_BLOB-uploaded bytes as a NEW handler assembly and atomically swaps the cached
        // dispatch, so editing a Handlers/*.cs and reloading changes live behavior with NO TS3 restart.
        // Runs on the pump (synchronous, single-threaded) so the swap can't race an in-flight dispatch.
        // Dev-gated by posture (same as the rest of the DIAG_* / hot surface). NO unload in .NET 2 —
        // each reload is a fresh uniquely-named resident assembly; we warn past a soft count threshold.
        private static void HandleReloadHandlers(BridgeCommand command)
        {
            if (sLastBlobBytes == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "RELOAD_HANDLERS: no blob uploaded (DIAG_BLOB the handler DLL first)"));
                return;
            }

            // Optional hex-encoded entry type name (default: the standard HandlerSet). Lets a renamed
            // entry type be reloaded without a shell change.
            string entryTypeName = "SimsOnline.Handlers.HandlerSet";
            if (command.Parts.Length >= 3 && command.Parts[2].Length > 0)
            {
                entryTypeName = BridgeCodec.DecodeText(command.Parts[2]);
            }

            string error;
            if (!LoadHandlerAssembly(sLastBlobBytes, entryTypeName, out error))
            {
                // Prior handler (if any) is left intact — a failed reload does not break live dispatch.
                SendLine(BridgeCodec.Error(command.CorrelationId, "RELOAD_HANDLERS failed: " + error));
                return;
            }

            // Success: clear the one-shot lazy-load latch so EnsureHandlerLoaded reflects the new state,
            // and report the new resident count (+ a soft warning when it climbs).
            sHandlerLoadAttempted = true;
            sHandlerLoadError = null;
            string report = "reloaded ok; entryType=" + entryTypeName + "; bytes=" +
                sLastBlobBytes.Length.ToString(CultureInfo.InvariantCulture) + "; resident=" +
                sHandlerResidentCount.ToString(CultureInfo.InvariantCulture);
            if (sHandlerResidentCount >= kHandlerResidentWarnAt)
            {
                report += "; WARNING: " + sHandlerResidentCount.ToString(CultureInfo.InvariantCulture) +
                    " handler assemblies resident (no .NET2 unload) — restart TS3 to reclaim";
            }
            SendLine(BridgeCodec.DiagRunReport(command.CorrelationId, "ok", report));
        }

        // Shared load+invoke. Returns the probe's Run() output prefixed with assembly info, or
        // a "!"-prefixed diagnostic (the caller maps "!" to err). Everything is try/caught: a
        // probe must never crash the pump silently — but note a hard native crash inside probe
        // code WILL take the TS3 session down (documented, dev-only, acceptable).
        // Probes by content: the same bytes (bridgectl stamps a probe with its source's hash and reuses the build) are
        // loaded once and rerun. Every Assembly.Load stays in the domain for good (.NET 2 cannot unload), and the server's
        // interpreter crashed twice after long runs of probes (2026-10-14/15, AV at TS3W RVA 0xa5101f); DIAG_STATE's
        // probeLoads/probeReuses say whether a crash tracks the count.
        private static readonly Dictionary<string, System.Reflection.Assembly> sProbeAssemblies = new Dictionary<string, System.Reflection.Assembly>();
        private static long sProbeLoads;
        private static long sProbeReuses;

        private static string ProbeKey(byte[] bytes)
        {
            ulong hash = 14695981039346656037UL; // FNV-1a 64
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }

            return bytes.Length.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static string LoadAndRun(byte[] bytes, string typeName, string label)
        {
            System.Reflection.Assembly assembly;
            string key = ProbeKey(bytes);
            if (sProbeAssemblies.TryGetValue(key, out assembly))
            {
                sProbeReuses++;
            }
            else
            {
                try
                {
                    assembly = System.Reflection.Assembly.Load(bytes);
                }
                catch (Exception ex)
                {
                    return "!Assembly.Load threw: " + ex.GetType().FullName + ": " + ex.Message;
                }

                sProbeAssemblies[key] = assembly;
                sProbeLoads++;
            }

            string asmName;
            try
            {
                asmName = assembly.FullName;
            }
            catch (Exception ex)
            {
                asmName = "(FullName threw " + ex.GetType().Name + ")";
            }

            Type type;
            try
            {
                type = assembly.GetType(typeName);
            }
            catch (Exception ex)
            {
                return "!GetType(" + typeName + ") threw: " + ex.GetType().Name + ": " + ex.Message + " [asm=" + asmName + "]";
            }
            if (type == null)
            {
                return "!type not found: " + typeName + " [asm=" + asmName + "]";
            }

            System.Reflection.MethodInfo run = type.GetMethod(
                "Run",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (run == null)
            {
                return "!no public static Run() on " + typeName + " [asm=" + asmName + "]";
            }

            // part 2: cache the resolved method so DIAG_RERUN can re-invoke it without
            // re-loading. Set BEFORE invoke so even a throwing probe is re-runnable after a fix
            // to the live state it reads.
            sLastRunMethod = run;
            sLastRunTypeName = typeName;
            sLastRunLabel = label;
            sLastRunAsmName = asmName;

            return InvokeRun(run, label, asmName, typeName);
        }

        // invoke a resolved probe Run() and format the report line. Shared by the
        // initial DIAG_RUN (via LoadAndRun) and DIAG_RERUN. Same try/catch contract: a "!"-prefixed
        // string maps to err; a hard native crash still takes the session down (documented).
        private static string InvokeRun(System.Reflection.MethodInfo run, string label, string asmName, string typeName)
        {
            object result;
            try
            {
                result = run.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException != null ? ex.InnerException : ex;
                return "!Run() threw: " + inner.GetType().FullName + ": " + inner.Message + " [asm=" + asmName + "]";
            }

            return "loaded=" + label + ";asm=" + asmName + ";type=" + typeName
                + ";run=" + (result == null ? "null" : result.ToString());
        }

        // Path navigation: field first (base-chain), then property getter — the operator named
        // the member explicitly, so a getter call is an informed choice here (NOT in recursion).
        private static object GetMemberValue(object target, string memberName)
        {
            if (target == null)
            {
                return null;
            }

            try
            {
                for (Type type = target.GetType(); type != null; type = type.BaseType)
                {
                    System.Reflection.FieldInfo field = type.GetField(
                        memberName,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (field != null)
                    {
                        return field.GetValue(target);
                    }
                }

                for (Type type = target.GetType(); type != null; type = type.BaseType)
                {
                    System.Reflection.PropertyInfo property = type.GetProperty(
                        memberName,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (property != null && property.CanRead)
                    {
                        return property.GetValue(target, null);
                    }
                }
            }
            catch (Exception)
            {
                // DIAG_WALK: a member that cannot be read walks as null, and the report shows that.
            }

            return null;
        }

        private static bool IsWalkLeaf(object value)
        {
            if (value == null)
            {
                return true;
            }

            Type type = value.GetType();
            return type.IsPrimitive || type.IsEnum || value is string || value is ObjectGuid || value is Vector3 || value is DateTime;
        }

        private static void WalkFields(StringBuilder report, string prefix, object target, int depthLeft, ref int nodes, int maxNodes, List<object> visited)
        {
            if (target == null || depthLeft <= 0 || nodes >= maxNodes)
            {
                return;
            }

            Type targetType = target.GetType();
            if (!targetType.IsValueType)
            {
                for (int i = 0; i < visited.Count; i++)
                {
                    if (object.ReferenceEquals(visited[i], target))
                    {
                        report.Append("\n").Append(prefix).Append(" = [cycle]");
                        return;
                    }
                }

                visited.Add(target);
            }

            for (Type type = targetType; type != null; type = type.BaseType)
            {
                System.Reflection.FieldInfo[] fields = type.GetFields(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (nodes >= maxNodes)
                    {
                        return;
                    }

                    nodes++;
                    System.Reflection.FieldInfo field = fields[i];
                    string label = prefix.Length > 0 ? prefix + "." + field.Name : field.Name;
                    object value;
                    try
                    {
                        value = field.GetValue(target);
                    }
                    catch (Exception ex)
                    {
                        report.Append("\n").Append(label).Append(" = !").Append(ex.GetType().Name);
                        continue;
                    }

                    if (IsWalkLeaf(value))
                    {
                        report.Append("\n").Append(label).Append(" = ").Append(value == null ? "null" : value.ToString());
                    }
                    else
                    {
                        report.Append("\n").Append(label).Append(" : ").Append(value.GetType().Name);
                        if (depthLeft > 1)
                        {
                            WalkFields(report, label, value, depthLeft - 1, ref nodes, maxNodes, visited);
                        }
                    }
                }
            }
        }

        private static void HandleDiagCaptureInteraction(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("DIAG_CAPTURE_INTERACTION requires a Sim object id.");
            }

            ulong simObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            Sim sim = FindSim(simObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.DiagInteractionBundleReport(command.CorrelationId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=not-found"));
                return;
            }

            string queueRole;
            bool transition;
            InteractionInstance interaction = TrySelectDiagInteraction(sim, out queueRole, out transition);

            if (interaction == null)
            {
                SendLine(BridgeCodec.DiagInteractionBundleReport(command.CorrelationId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=no-interaction"));
                return;
            }

            string report = CaptureInteractionBundle(sim, simObjectId, interaction, queueRole, transition);
            SendChunkedSnapshot(command.CorrelationId, BridgeCodec.DiagInteractionBundleReport(command.CorrelationId, "ok", report));
        }

        private static void HandleDiagInteractionState(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("DIAG_INTERACTION_STATE requires a Sim object id.");
            }

            ulong simObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            string watchedBookId = string.Empty;
            if (command.Parts.Length >= 4 && command.Parts[3] != null && command.Parts[3].Length > 0 && command.Parts[3] != "-")
            {
                watchedBookId = BridgeCodec.DecodeText(command.Parts[3]);
            }

            Sim sim = FindSim(simObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.DiagInteractionStateReport(command.CorrelationId, "err", "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=not-found"));
                return;
            }

            string report = BuildDiagInteractionStateReport(sim, simObjectId, watchedBookId);
            SendChunkedSnapshot(command.CorrelationId, BridgeCodec.DiagInteractionStateReport(command.CorrelationId, "ok", report));
        }

        private static InteractionInstance TrySelectDiagInteraction(Sim sim, out string queueRole, out bool transition)
        {
            queueRole = "none";
            transition = false;
            if (sim == null || sim.InteractionQueue == null)
            {
                return null;
            }

            InteractionQueue queue = sim.InteractionQueue;
            InteractionInstance interaction = null;
            try
            {
                interaction = sim.CurrentInteraction;
                if (interaction != null)
                {
                    queueRole = "current";
                }
            }
            catch
            {
                // Diagnostic read: an unreadable slot falls through to the next one.
            }

            if (interaction == null)
            {
                try
                {
                    interaction = queue.RunningInteraction;
                    if (interaction != null)
                    {
                        queueRole = "running";
                    }
                }
                catch
                {
                    // Diagnostic read: an unreadable slot falls through to the next one.
                }
            }

            if (interaction == null)
            {
                try
                {
                    interaction = queue.TransitionInteraction;
                    if (interaction != null)
                    {
                        queueRole = "transition";
                        transition = true;
                    }
                }
                catch
                {
                    // Diagnostic read: an unreadable slot falls through to the next one.
                }
            }

            if (interaction == null)
            {
                try
                {
                    interaction = queue.GetHeadInteraction();
                    if (interaction != null)
                    {
                        queueRole = "head";
                    }
                }
                catch
                {
                    // Diagnostic read: an unreadable head leaves queueRole none, which the report shows.
                }
            }

            return interaction;
        }

        private static string BuildDiagInteractionStateReport(Sim sim, ulong simObjectId, string watchedBookId)
        {
            StringBuilder report = new StringBuilder();
            AppendBundleRaw(report, "sim", simObjectId.ToString(CultureInfo.InvariantCulture));
            AppendBundleText(report, "name", SafeDiag(delegate { return sim.FullName; }));
            AppendBundleRaw(report, "role", sCurrentRole);
            AppendBundleRaw(report, "inWorld", SafeIsInWorld().ToString());
            AppendBundleRaw(report, "tick", SafeTick().ToString(CultureInfo.InvariantCulture));
            AppendBundleText(report, "postureType", SafeDiag(delegate { return sim.Posture == null ? string.Empty : sim.Posture.GetType().FullName; }));
            AppendBundleRaw(report, "postureContainerObjectId", TryGetPostureContainerObjectId(sim));
            AppendBundleRaw(report, "queueCount", SafeDiag(delegate { return sim.InteractionQueue == null ? "null" : sim.InteractionQueue.Count.ToString(CultureInfo.InvariantCulture); }));

            string queueRole;
            bool transition;
            InteractionInstance interaction = TrySelectDiagInteraction(sim, out queueRole, out transition);
            AppendDiagInteractionFields(report, sim, interaction, queueRole, transition);

            Book activeBook = interaction == null ? null : interaction.Target as Book;
            Book watchBook = null;
            if (watchedBookId != null && watchedBookId.Length > 0)
            {
                if (IsBookDataId(activeBook, watchedBookId))
                {
                    watchBook = activeBook;
                }
                else
                {
                    watchBook = TryFindInventoryBookByDataId(sim, watchedBookId);
                }
            }
            else if (activeBook != null)
            {
                watchBook = activeBook;
            }

            AppendBundleRaw(report, "watchBookFound", watchBook == null ? "0" : "1");
            if (watchBook == null)
            {
                AppendBundleRaw(report, "watchBookId", watchedBookId == null ? string.Empty : watchedBookId);
                AppendBundleRaw(report, "watchBookObjectId", string.Empty);
                AppendBundleRaw(report, "watchBookProgressNormalized", string.Empty);
                AppendBundleRaw(report, "watchBookLength", string.Empty);
            }
            else
            {
                AppendBookProgressFields(report, "watchBook", sim, watchBook);
            }

            return report.ToString();
        }

        private static void AppendDiagInteractionFields(StringBuilder report, Sim sim, InteractionInstance interaction, string queueRole, bool transition)
        {
            AppendBundleRaw(report, "activeQueueRole", queueRole);
            AppendBundleRaw(report, "activeIsTransition", transition.ToString());
            if (interaction == null)
            {
                AppendBundleRaw(report, "activeInteractionId", string.Empty);
                AppendBundleText(report, "activeInteractionKey", string.Empty);
                AppendBundleText(report, "activeInteractionType", string.Empty);
                AppendBundleRaw(report, "activeTargetObjectId", string.Empty);
                AppendBundleText(report, "activeTargetType", string.Empty);
                AppendBundleRaw(report, "activeBookId", string.Empty);
                AppendBundleRaw(report, "activeProgressNormalized", string.Empty);
                return;
            }

            AppendBundleRaw(report, "activeInteractionId", SafeDiag(delegate { return interaction.Id.ToString(CultureInfo.InvariantCulture); }));
            AppendBundleText(report, "activeInteractionKey", SafeDiag(delegate { return interaction.InteractionDefinition == null ? string.Empty : interaction.InteractionDefinition.GetType().FullName; }));
            AppendBundleText(report, "activeInteractionType", SafeDiag(delegate { return interaction.GetType().FullName; }));
            AppendBundleRaw(report, "activeTargetObjectId", SafeDiag(delegate { return interaction.Target == null ? string.Empty : interaction.Target.ObjectId.Value.ToString(CultureInfo.InvariantCulture); }));
            AppendBundleText(report, "activeTargetType", SafeDiag(delegate { return interaction.Target == null ? string.Empty : interaction.Target.GetType().FullName; }));

            Book activeBook = interaction.Target as Book;
            if (activeBook == null)
            {
                AppendBundleRaw(report, "activeBookId", string.Empty);
                AppendBundleRaw(report, "activeProgressNormalized", string.Empty);
            }
            else
            {
                AppendBundleRaw(report, "activeBookId", activeBook.Data == null ? string.Empty : activeBook.Data.ID);
                AppendBundleRaw(report, "activeProgressNormalized", TryReadBookProgress(sim, activeBook));
            }
        }

        private static void AppendBookProgressFields(StringBuilder report, string prefix, Sim sim, Book book)
        {
            AppendBundleRaw(report, prefix + "Id", book.Data == null ? string.Empty : book.Data.ID);
            AppendBundleRaw(report, prefix + "ObjectId", SafeDiag(delegate { return book.ObjectId.Value.ToString(CultureInfo.InvariantCulture); }));
            AppendBundleRaw(report, prefix + "ProgressNormalized", TryReadBookProgress(sim, book));
            AppendBundleRaw(report, prefix + "Length", book.Data == null ? string.Empty : book.Data.Length.ToString(CultureInfo.InvariantCulture));
        }

        private static string TryReadBookProgress(Sim sim, Book book)
        {
            if (sim == null || book == null || book.Data == null)
            {
                return string.Empty;
            }

            try
            {
                ReadBookData readData;
                if (sim.ReadBookDataList == null || !sim.ReadBookDataList.TryGetValue(book.Data.ID, out readData) || readData == null)
                {
                    return string.Empty;
                }

                if (book.Data.Length <= 0)
                {
                    return string.Empty;
                }

                float progress = readData.CurrentPage / (float)book.Data.Length;
                if (float.IsNaN(progress) || float.IsInfinity(progress))
                {
                    return string.Empty;
                }

                if (progress < 0f)
                {
                    progress = 0f;
                }
                else if (progress > 1f)
                {
                    progress = 1f;
                }

                return Float(progress);
            }
            catch (Exception ex)
            {
                return "!" + ex.GetType().Name;
            }
        }

        private static Book TryFindInventoryBookByDataId(Sim sim, string bookId)
        {
            if (sim == null || sim.Inventory == null || bookId == null || bookId.Length == 0)
            {
                return null;
            }

            foreach (Book book in Sims3.Gameplay.Queries.GetObjects<Book>())
            {
                if (book == null || book.Data == null || book.Data.ID != bookId)
                {
                    continue;
                }

                try
                {
                    if (sim.Inventory.Contains(book))
                    {
                        return book;
                    }
                }
                catch
                {
                    // Diagnostic read: a book that cannot be checked is skipped.
                }
            }

            return null;
        }

        private static bool IsBookDataId(Book book, string bookId)
        {
            return book != null && book.Data != null && book.Data.ID == bookId;
        }

        private static void ResolveCaptureContextForInstance(Sim sim, InteractionInstance interaction, out string queueRole, out bool transition)
        {
            queueRole = "none";
            transition = false;
            if (sim == null || interaction == null || sim.InteractionQueue == null)
            {
                return;
            }

            InteractionQueue queue = sim.InteractionQueue;
            try
            {
                if (object.ReferenceEquals(interaction, sim.CurrentInteraction))
                {
                    queueRole = "current";
                }
                else if (object.ReferenceEquals(interaction, queue.RunningInteraction))
                {
                    queueRole = "running";
                }
                else if (object.ReferenceEquals(interaction, queue.TransitionInteraction))
                {
                    queueRole = "transition";
                    transition = true;
                }
                else if (object.ReferenceEquals(interaction, queue.GetHeadInteraction()))
                {
                    queueRole = "head";
                }
            }
            catch
            {
                SwallowedErrors.Note();
            }

            if (!transition)
            {
                try
                {
                    transition = object.ReferenceEquals(interaction, queue.TransitionInteraction);
                }
                catch
                {
                    SwallowedErrors.Note();
                }
            }
        }

        // emit a full InteractionReplayBundle on the bridge for production mirroring.
        private static void SendInteractionReplayQueuedEvent(Sim sim, ulong simObjectId, InteractionInstance interaction)
        {
            if (interaction == null)
            {
                return;
            }

            string queueRole;
            bool transition;
            ResolveCaptureContextForInstance(sim, interaction, out queueRole, out transition);
            string report = CaptureInteractionBundle(sim, simObjectId, interaction, queueRole, transition);
            SendLine(BridgeCodec.EventInteractionReplayQueued(report));
        }

        private static string CaptureInteractionBundle(Sim sim, ulong simObjectId, InteractionInstance interaction, string queueRole, bool transition)
        {
            StringBuilder report = new StringBuilder();
            string interactionKey = SafeDiag(delegate { return interaction.InteractionDefinition == null ? string.Empty : interaction.InteractionDefinition.GetType().FullName; });
            string instanceType = SafeDiag(delegate { return interaction.GetType().FullName; });
            string targetObjectId = "0";
            string targetObjectType = string.Empty;
            Vector3 targetPosition = Vector3.Zero;
            bool hasTargetPosition = false;

            try
            {
                Vector3 destination = interaction.GetTargetPosition();
                if (IsFiniteInWorldPosition(destination))
                {
                    targetPosition = destination;
                    hasTargetPosition = true;
                }
            }
            catch
            {
                SwallowedErrors.Note();
            }

            try
            {
                if (interaction.Target != null)
                {
                    targetObjectId = interaction.Target.ObjectId.Value.ToString(CultureInfo.InvariantCulture);
                    targetObjectType = interaction.Target.GetType().FullName;
                    if (!hasTargetPosition)
                    {
                        Vector3 targetPos = interaction.Target.Position;
                        if (IsFiniteInWorldPosition(targetPos))
                        {
                            targetPosition = targetPos;
                            hasTargetPosition = true;
                        }
                    }
                }
            }
            catch
            {
                SwallowedErrors.Note();
            }

            string priorityLevel = string.Empty;
            string priorityValue = "0";
            try
            {
                InteractionPriority priority = interaction.GetPriority();
                priorityLevel = priority.Level.ToString();
                priorityValue = Float(priority.Value);
            }
            catch (Exception ex)
            {
                priorityLevel = "!" + ex.GetType().Name;
            }

            AppendBundleRaw(report, "version", "1");
            AppendBundleRaw(report, "actorObjectId", simObjectId.ToString(CultureInfo.InvariantCulture));
            AppendBundleRaw(report, "interactionId", SafeDiag(delegate { return interaction.Id.ToString(CultureInfo.InvariantCulture); }));
            AppendBundleText(report, "interactionKey", interactionKey);
            AppendBundleText(report, "interactionInstanceType", instanceType);
            AppendBundleText(report, "interactionDefinitionType", interactionKey);
            AppendBundleRaw(report, "targetObjectId", targetObjectId);
            AppendBundleText(report, "targetObjectType", targetObjectType);
            AppendBundleRaw(report, "hasTargetPosition", hasTargetPosition ? "1" : "0");
            AppendBundleRaw(report, "x", Float(targetPosition.x));
            AppendBundleRaw(report, "y", Float(targetPosition.y));
            AppendBundleRaw(report, "z", Float(targetPosition.z));
            AppendBundleText(report, "priorityLevel", priorityLevel);
            AppendBundleRaw(report, "priorityValue", priorityValue);
            AppendBundleRaw(report, "isUserDirected", IsInteractionUserDirected(interaction).ToString());
            AppendBundleRaw(report, "isAutonomous", SafeDiag(delegate { return interaction.Autonomous.ToString(); }));
            AppendBundleRaw(report, "isTransitionInteraction", transition.ToString());
            AppendBundleRaw(report, "queueRole", queueRole);
            AppendBundleText(report, "postureType", SafeDiag(delegate { return sim.Posture == null ? string.Empty : sim.Posture.GetType().FullName; }));
            AppendBundleRaw(report, "postureContainerObjectId", TryGetPostureContainerObjectId(sim));
            AppendBundleRaw(report, "hostTick", SafeTick().ToString(CultureInfo.InvariantCulture));
            AppendBundleText(report, "parameters", FormatInteractionParameters(sim, interaction));
            AppendBundleText(report, "policyHint", ResolveCapturePolicyHint(interactionKey, queueRole, transition));
            return report.ToString();
        }

        private static string TryGetPostureContainerObjectId(Sim sim)
        {
            return SafeDiag(delegate { return ReadPostureContainerObjectId(sim); });
        }

        private static string ReadPostureContainerObjectId(Sim sim)
        {
            if (sim == null || sim.Posture == null)
            {
                return string.Empty;
            }

            IGameObject container = sim.Posture.Container;
            if (container == null)
            {
                return string.Empty;
            }

            return container.ObjectId.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static string ReadDefinitionActionKey(InteractionInstance interaction)
        {
            if (interaction == null || interaction.InteractionDefinition == null)
            {
                return string.Empty;
            }

            InteractionDefinition definition = interaction.InteractionDefinition;
            System.Reflection.FieldInfo field = definition.GetType().GetField(
                "ActionKey",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field == null)
            {
                return string.Empty;
            }

            object value = field.GetValue(definition);
            return value == null ? string.Empty : value.ToString();
        }

        private static string FormatInteractionParameters(Sim sim, InteractionInstance interaction)
        {
            return SafeDiag(delegate { return BuildInteractionParameters(sim, interaction); });
        }

        private static string BuildInteractionParameters(Sim sim, InteractionInstance interaction)
        {
            if (interaction == null)
            {
                return string.Empty;
            }

            StringBuilder parts = new StringBuilder();
            AppendParameterPair(parts, "pushedAsContinuation", interaction.PushedAsContinuation.ToString());

            InteractionInstanceParameters parameters = interaction.GetInteractionParameters();
            AppendParameterPair(parts, "autonomous", parameters.Autonomous.ToString());
            AppendParameterPair(parts, "userDirected", parameters.UserDirected.ToString());
            AppendParameterPair(parts, "effectivelyAutonomous", parameters.EffectivelyAutonomous.ToString());
            AppendParameterPair(parts, "continuation", parameters.Continuation.ToString());
            AppendParameterPair(parts, "postureTransition", parameters.PostureTransition.ToString());

            string actionKey = ReadDefinitionActionKey(interaction);
            if (actionKey != null && actionKey.Length > 0)
            {
                AppendParameterPair(parts, "actionKey", actionKey);
            }

            try
            {
                GameObjectHit hit = parameters.Hit;
                Vector3 point = hit.mPoint;
                if (IsFiniteInWorldPosition(point))
                {
                    AppendParameterPair(parts, "hitX", Float(point.x));
                    AppendParameterPair(parts, "hitY", Float(point.y));
                    AppendParameterPair(parts, "hitZ", Float(point.z));
                }
            }
            catch
            {
                SwallowedErrors.Note();
            }

            AppendSeatHintParameters(parts, sim, interaction, parameters);
            AppendBookHintParameter(parts, interaction);
            AppendReadBookProgressParameter(parts, sim, interaction);
            return parts.ToString();
        }

        private static void AppendBookHintParameter(StringBuilder parts, InteractionInstance interaction)
        {
            if (interaction == null)
            {
                return;
            }

            Book book = interaction.Target as Book;
            if (book == null || book.Data == null || book.Data.ID == null || book.Data.ID.Length == 0)
            {
                return;
            }

            AppendParameterPair(parts, "bookId", book.Data.ID);
        }

        private static void AppendReadBookProgressParameter(StringBuilder parts, Sim sim, InteractionInstance interaction)
        {
            if (interaction == null || interaction.InteractionDefinition == null)
            {
                return;
            }

            string interactionKey = interaction.InteractionDefinition.GetType().FullName;
            if (!InteractionReplayApplySorter.IsReadBookTerminalHead(interactionKey))
            {
                return;
            }

            Book book = interaction.Target as Book;
            if (book == null || book.Data == null)
            {
                return;
            }

            try
            {
                float progress = ReadBookData.GetBookProgress(sim, book);
                if (float.IsNaN(progress) || float.IsInfinity(progress))
                {
                    return;
                }

                if (progress < 0f)
                {
                    progress = 0f;
                }
                else if (progress > 1f)
                {
                    progress = 1f;
                }

                AppendParameterPair(parts, "progressNormalized", Float(progress));
            }
            catch
            {
                SwallowedErrors.Note();
            }
        }

        private static void AppendSeatHintParameters(StringBuilder parts, Sim sim, InteractionInstance interaction, InteractionInstanceParameters parameters)
        {
            if (interaction == null || interaction.Target == null)
            {
                return;
            }

            GameObject target = interaction.Target as GameObject;
            if (target == null)
            {
                return;
            }

            SitData seat = TryGetCurrentSeatData(sim, target);
            if (seat == null)
            {
                Vector3 point = Vector3.Zero;
                bool hasPoint = false;
                try
                {
                    point = parameters.Hit.mPoint;
                    hasPoint = IsFiniteInWorldPosition(point) && point != Vector3.Zero;
                }
                catch
                {
                    SwallowedErrors.Note();
                }

                if (!hasPoint && sim != null)
                {
                    point = sim.Position;
                    hasPoint = IsFiniteInWorldPosition(point);
                }

                if (hasPoint)
                {
                    seat = FindSeatClosestToPoint(target, point, true);
                }
            }

            if (seat == null)
            {
                return;
            }

            Slot routingSlot = ResolveDefaultRoutingSlotForSeat(seat);
            AppendParameterPair(parts, "seatArea", seat.Area.ToString());
            AppendParameterPair(parts, "seatContainmentSlot", ((int)seat.ContainmentSlot).ToString(CultureInfo.InvariantCulture));
            AppendParameterPair(parts, "seatRoutingSlot", ((int)routingSlot).ToString(CultureInfo.InvariantCulture));
            AppendParameterPair(parts, "seatIkSuffix", seat.IKSuffix);
        }

        private static SitData TryGetCurrentSeatData(Sim sim, GameObject target)
        {
            if (sim == null || sim.Posture == null)
            {
                return null;
            }

            ISeatPosture seatPosture = sim.Posture as ISeatPosture;
            if (seatPosture == null || seatPosture.Seat == null)
            {
                return null;
            }

            if (seatPosture.Seat.Container == target)
            {
                return seatPosture.Seat;
            }

            return null;
        }

        private static SitData[] GetSitDataArray(GameObject target)
        {
            IHasSeatingGroup seatingGroupHost = target as IHasSeatingGroup;
            if (seatingGroupHost != null && seatingGroupHost.SeatingGroup != null)
            {
                return seatingGroupHost.SeatingGroup.SitDataArray;
            }

            if (target.PartComponent == null)
            {
                return new SitData[0];
            }

            List<PartData> parts = target.PartComponent.GetParts();
            List<SitData> seats = new List<SitData>();
            for (int i = 0; i < parts.Count; i++)
            {
                SitData seat = parts[i] as SitData;
                if (seat != null)
                {
                    seats.Add(seat);
                }
            }

            return seats.ToArray();
        }

        private static SitData FindSeatClosestToPoint(GameObject target, Vector3 point, bool requireEmptyContainment)
        {
            SitData[] seats = GetSitDataArray(target);
            SitData best = null;
            float bestDistance = float.MaxValue;
            ObjectGuid objectId = target.ObjectId;

            for (int i = 0; i < seats.Length; i++)
            {
                SitData seat = seats[i];
                if (seat == null || seat.Container != target)
                {
                    continue;
                }

                if (requireEmptyContainment && Slots.GetContainedObject(objectId, (uint)(int)seat.ContainmentSlot) != ObjectGuid.InvalidObjectGuid)
                {
                    continue;
                }

                Vector3 slotPosition = Slots.GetPositionOfSlot(objectId, (uint)(int)seat.ContainmentSlot);
                float dx = slotPosition.x - point.x;
                float dy = slotPosition.y - point.y;
                float dz = slotPosition.z - point.z;
                float distance = (dx * dx) + (dy * dy) + (dz * dz);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = seat;
                }
            }

            return best;
        }

        private static Slot ResolveDefaultRoutingSlotForSeat(SitData seat)
        {
            if (seat == null || seat.RoutingSlot == null || seat.RoutingSlot.Length == 0)
            {
                return (Slot)0;
            }

            return seat.RoutingSlot[0];
        }

        private static void AppendParameterPair(StringBuilder parts, string key, string value)
        {
            if (value == null || value.Length == 0)
            {
                return;
            }

            if (parts.Length > 0)
            {
                parts.Append(",");
            }

            parts.Append(key);
            parts.Append("=");
            parts.Append(value);
        }

        private static bool TryGetInteractionParameterValue(string interactionParameters, string key, out string value)
        {
            value = string.Empty;
            if (interactionParameters == null || interactionParameters.Length == 0 || key == null || key.Length == 0)
            {
                return false;
            }

            int index = 0;
            while (index < interactionParameters.Length)
            {
                int comma = interactionParameters.IndexOf(',', index);
                if (comma < 0)
                {
                    comma = interactionParameters.Length;
                }

                string pair = interactionParameters.Substring(index, comma - index);
                int equals = pair.IndexOf('=');
                if (equals > 0)
                {
                    string pairKey = pair.Substring(0, equals);
                    if (pairKey == key)
                    {
                        value = pair.Substring(equals + 1);
                        return value.Length > 0;
                    }
                }

                index = comma + 1;
            }

            return false;
        }

        private static void TrySeedReadBookProgress(Sim actor, Book book, string interactionParameters)
        {
            if (actor == null || book == null || book.Data == null)
            {
                return;
            }

            string progressText;
            if (!TryGetInteractionParameterValue(interactionParameters, "progressNormalized", out progressText))
            {
                return;
            }

            float progress;
            try
            {
                progress = float.Parse(progressText, CultureInfo.InvariantCulture);
            }
            catch
            {
                return;
            }

            if (float.IsNaN(progress) || float.IsInfinity(progress))
            {
                return;
            }

            if (progress < 0f)
            {
                progress = 0f;
            }
            else if (progress > 1f)
            {
                progress = 1f;
            }

            BookData data = book.Data;
            if (data.Length <= 0)
            {
                return;
            }

            ReadBookData readData;
            if (!actor.ReadBookDataList.TryGetValue(data.ID, out readData))
            {
                readData = new ReadBookData();
                actor.ReadBookDataList.Add(data.ID, readData);
                readData.BookID = data.ID;
            }

            readData.CurrentPage = progress * (float)data.Length;
        }

        private static string ResolveCapturePolicyHint(string interactionKey, string queueRole, bool transition)
        {
            if (TerminalInteractionFilter.IsNeverReplay(interactionKey))
            {
                return "NeverReplay";
            }

            if (transition || queueRole == "transition")
            {
                return "ForceAlign";
            }

            if (RoutingInteractionFilter.IsRoutingInteraction(interactionKey))
            {
                return "ReplayAsRoute";
            }

            if (TerminalInteractionFilter.IsTerminalReplay(interactionKey))
            {
                return "ReplayExact";
            }

            return string.Empty;
        }

        private static void AppendBundleRaw(StringBuilder report, string key, string value)
        {
            if (report.Length > 0)
            {
                report.Append(";");
            }

            report.Append(key);
            report.Append("=");
            report.Append(value == null ? string.Empty : value);
        }

        private static void AppendBundleText(StringBuilder report, string key, string value)
        {
            AppendBundleRaw(report, key, BridgeCodec.EncodeText(value));
        }

        // read-only re-attach diagnostic — what session-relevant state does the mod
        // hold right now? A re-attached controller starts with empty claim/role maps; this
        // report is how it (or the operator) sees what the previous session left behind.
        // Accepted in any role, like PROBE_SIM_STATUS.
        private static void HandleDiagState(BridgeCommand command)
        {
            string watched = "";
            foreach (ulong simObjectId in sAutonomyWatchLastInteractionIds.Keys)
            {
                if (watched.Length > 0)
                {
                    watched += ",";
                }

                watched += simObjectId.ToString(CultureInfo.InvariantCulture);
            }

            // T5a: dispatch = registered command count, so command-table parity with the
            // controller's BridgeLineCodec.KnownCommands can be checked live (both expected 40
            // after added APPLY_POSITIONS to both; added GATE_AUTONOMY/UNGATE_AUTONOMY).
            int dispatchCount = sDispatch == null ? 0 : sDispatch.Count;

            // command-lifecycle gate registry, each Sim tagged with its engage state, so a
            // live session can confirm "claimed but free until directed" and watch the gate
            // engage/disengage across a command. Format: "<simId>:<gated|free>,...".
            string commandGate = "";
            List<ulong> gatedKeys = sCommandGate.TrackedSims();
            for (int gi = 0; gi < gatedKeys.Count; gi++)
            {
                if (commandGate.Length > 0)
                {
                    commandGate += ",";
                }

                ulong gateSim = gatedKeys[gi];
                commandGate += gateSim.ToString(CultureInfo.InvariantCulture) + ":" + (sCommandGate.IsGated(gateSim) ? "gated" : "free");
            }

            // pumpTicks (bridge pump iterations) alongside the sim tick lets the
            // headless probe distinguish "sim clock paused" from "pump dead" per window state.
            string report = "role=" + sCurrentRole
                + ";inWorld=" + SafeIsInWorld().ToString()
                + ";tick=" + SafeTick().ToString(CultureInfo.InvariantCulture)
                + ";autonomyWatch=" + (watched.Length > 0 ? watched : "(none)")
                + ";commandGate=" + (commandGate.Length > 0 ? commandGate : "(none)")
                + ";pendingOutbound=" + sPendingOutboundLines.Count.ToString(CultureInfo.InvariantCulture)
                + ";pumpTicks=" + sBridgeProbeTicks.ToString(CultureInfo.InvariantCulture)
                + ";probeLoads=" + sProbeLoads.ToString(CultureInfo.InvariantCulture) + ";probeReuses=" + sProbeReuses.ToString(CultureInfo.InvariantCulture)
                + ";dispatch=" + dispatchCount.ToString(CultureInfo.InvariantCulture)
                + ";commandedSpeed=" + sCurrentSpeed
                + ";observedSpeed=" + SpeedEnumToWire(Sims3.Gameplay.Gameflow.CurrentGameSpeed);
            if (SafeIsInWorld())
            {
                try
                {
                    DateAndTime clockNow = SimClock.CurrentTime();
                    report += ";clockDay=" + SimClock.DayToInt(clockNow.DayOfWeek).ToString(CultureInfo.InvariantCulture)
                        + ";clockHours=" + SimClock.Hours24.ToString(CultureInfo.InvariantCulture)
                        + ";clockMinutes=" + SimClock.Minutes60.ToString(CultureInfo.InvariantCulture)
                        + ";clockTicks=" + SimClock.CurrentTicks.ToString(CultureInfo.InvariantCulture)
                        + ";clockSets=" + sWorldClockSets.ToString(CultureInfo.InvariantCulture) + "/" + sWorldClockSetsSkipped.ToString(CultureInfo.InvariantCulture)
                        + ";clockLastSetMin=" + (sWorldClockLastDeltaTicks / 37.5f).ToString("0.##", CultureInfo.InvariantCulture)
                        + ";hudClockDueMin=" + HudClockDueMinutes().ToString("0.##", CultureInfo.InvariantCulture)
                        + ";inventories=" + InventoryReport()
                        + ";uses=" + UseReport()
                        + ";refusals=" + RefusalReport();
                }
                catch
                {
                    SwallowedErrors.Note();
                }
            }

            // Last, so the fields before it keep their place: errors the mod swallowed since the game started.
            report += ";" + SwallowedErrors.DiagStateField();
            SendLine(BridgeCodec.DiagStateReport(command.CorrelationId, "ok", report));
        }

        // PROBE_SIM_STATUS (and the rest of the APPLY_*/PROBE_* state family) moved to the swappable
        // handler assembly (T5b Phase B): SimsOnlineServerHostMod/Handlers/HandlerSet.cs. Names are
        // registered from sSwappableCommandNames -> DispatchToHandler, which lazy-loads the embedded
        // handler on first use (EnsureHandlerLoaded). Replies are built shell-side through the
        // SendReply("SimStatusProbe", ...) service verb.

        // Dev tooling: controller-driven in-game toast. Accepted in any role.
        private static void HandleNotify(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("NOTIFY requires a hex-encoded message.");
            }

            string text = BridgeCodec.DecodeText(command.Parts[2]);
            ShowToast(text);
            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // the last notices this game showed, so a scenario can assert what a player saw.
        // NOTICES|corr|sinceSeq (any role) replies AUTONOMY_PROBE ok with "seq=<last>;lost=<n>;count=<k>"
        // and then one "seq|ms|text" line per notice after sinceSeq (ms = Environment.TickCount when
        // shown; the text's line breaks become spaces). lost counts the notices after sinceSeq that the
        // ring no longer holds.
        private const int NoticeRingSize = 32;
        private static readonly List<string> sNoticeRing = new List<string>();
        private static long sNoticeSeq;

        private static void RecordNotice(string text)
        {
            string flat = (text ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            sNoticeSeq++;
            sNoticeRing.Add(sNoticeSeq.ToString(CultureInfo.InvariantCulture) + "|"
                + System.Environment.TickCount.ToString(CultureInfo.InvariantCulture) + "|" + flat);
            if (sNoticeRing.Count > NoticeRingSize)
            {
                sNoticeRing.RemoveAt(0);
            }
        }

        private static void HandleNotices(BridgeCommand command)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            long since = command.Parts.Length > 2 && command.Parts[2].Length > 0 ? long.Parse(command.Parts[2], c) : 0L;
            long first = sNoticeSeq - sNoticeRing.Count + 1;
            long lost = Math.Max(0L, first - since - 1);
            StringBuilder rows = new StringBuilder();
            int count = 0;
            for (int i = 0; i < sNoticeRing.Count; i++)
            {
                if (first + i > since)
                {
                    rows.Append('\n').Append(sNoticeRing[i]);
                    count++;
                }
            }

            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok",
                "seq=" + sNoticeSeq.ToString(c) + ";lost=" + lost.ToString(c) + ";count=" + count.ToString(c) + rows.ToString()));
        }

        // Toasts are best-effort UI sugar; they must never break the bridge pump, so every
        // exception is swallowed. NotificationStyle 1 = kGameMessagePositive (decompile uses
        // the numeric cast; the enum member name is not part of our verified surface).
        private static void ShowToast(string text)
        {
            RecordNotice(text);
            try
            {
                Sims3.UI.StyledNotification.Format format = new Sims3.UI.StyledNotification.Format(text, (Sims3.UI.StyledNotification.NotificationStyle)1);
                Sims3.UI.StyledNotification.Show(format);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static Sims3.SimIFace.Gameflow.GameSpeed ParseSpeed(string value)
        {
            if (value == "Pause")
            {
                return Sims3.SimIFace.Gameflow.GameSpeed.Pause;
            }

            if (value == "Normal")
            {
                return Sims3.SimIFace.Gameflow.GameSpeed.Normal;
            }

            if (value == "Double")
            {
                return Sims3.SimIFace.Gameflow.GameSpeed.Double;
            }

            if (value == "Triple")
            {
                return Sims3.SimIFace.Gameflow.GameSpeed.Triple;
            }

            if (value == "Skip")
            {
                return Sims3.SimIFace.Gameflow.GameSpeed.Skip;
            }

            throw new InvalidOperationException("Unknown game speed: " + value);
        }

        private static void SendSnapshot(string correlationId, long sinceTick)
        {
            // Capture currentTick once so the trackers and the response line agree on the timestamp.
            long currentTick = SafeTick();

            // Phase 2b: when v11 multi-slot is active, encode SNAPSHOT2 binary directly
            // into a byte buffer — no ~2 MiB ASCII string, no UTF-8 GetBytes cliff.
            if (BridgeMemoryBuffer.MultiSlotEnabled)
            {
                if (TrySendSnapshot2Binary(correlationId, sinceTick, currentTick))
                {
                    return;
                }
            }

            string[] sims = SnapshotSims(sinceTick, currentTick);
            // Only baseline (full) snapshots count toward the lifecycle probe - delta snapshots
            // intentionally undercount because filtered entities are skipped.
            if (sinceTick < 0)
            {
                RecordFirstSnapshotRequestSample(sims.Length);
            }
            string[] objects = SnapshotObjects(sinceTick, currentTick);
            string line = BridgeCodec.Snapshot(correlationId, currentTick, SafeWorldFileName(), sCurrentSaveName, sCurrentSpeed, sims, objects);
            SendChunkedSnapshot(correlationId, line);
        }

        // Phase 2b: binary snapshot encoder. Writes engine state directly into a byte[]
        // buffer (Snapshot2Writer) — never builds the ~2 MiB ASCII SNAPSHOT| line. The buffer
        // is then striped across bulk slots (same as Phase 2a UTF-8 bulk) and BULK_REF is sent.
        // Falls back to the ASCII chunked path if bulk slots are unavailable.
        private static bool TrySendSnapshot2Binary(string correlationId, long sinceTick, long currentTick)
        {
            // First pass: count Sims and objects for the simCount/objectCount fields.
            // We do a single pass writing the header + counts, then iterate actors/objects
            // writing each entry directly. The writer auto-grows its buffer.
            Snapshot2Writer writer = new Snapshot2Writer(65536);

            writer.WriteHeader(currentTick, SafeWorldFileName(), sCurrentSaveName,
                Snapshot2Writer.SpeedToOrdinal(sCurrentSpeed));

            // We need the sim count before writing the entries. Two-pass: first count, then
            // write the count placeholder, then write entries. But Snapshot2Writer writes
            // sequentially, so we need to count first, then write count, then iterate again.
            // For large worlds (69 sims + 10K objects) the count pass is cheap (no encoding).

            int simCount = 0;
            IList<Sim> actors = LotManager.Actors;
            if (actors != null)
            {
                foreach (Sim sim in actors)
                {
                    if (sim == null)
                    {
                        continue;
                    }
                    try
                    {
                        long changedTick = UpdateSimTracker(sim, currentTick);
                        if (sinceTick >= 0L && changedTick <= sinceTick)
                        {
                            continue;
                        }
                        simCount++;
                    }
                    catch
                    {
                        SwallowedErrors.Note();
                    }
                }
            }

            if (sinceTick < 0)
            {
                RecordFirstSnapshotRequestSample(simCount);
            }

            int objectCount = 0;
            GameObject[] allObjects = Sims3.Gameplay.Queries.GetObjects<GameObject>();
            if (allObjects != null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject obj = allObjects[i];
                    if (obj == null || obj is Sim)
                    {
                        continue;
                    }
                    try
                    {
                        long changedTick = UpdateObjectTracker(obj, currentTick);
                        if (sinceTick >= 0L && changedTick <= sinceTick)
                        {
                            continue;
                        }
                        objectCount++;
                    }
                    catch
                    {
                        SwallowedErrors.Note();
                    }
                }
            }

            writer.WriteSimCount(simCount);

            // Second pass: write each Sim entry.
            if (actors != null)
            {
                foreach (Sim sim in actors)
                {
                    if (sim == null)
                    {
                        continue;
                    }
                    try
                    {
                        long changedTick = UpdateSimTracker(sim, currentTick);
                        if (sinceTick >= 0L && changedTick <= sinceTick)
                        {
                            continue;
                        }
                        WriteSim2(writer, sim);
                    }
                    catch
                    {
                        SwallowedErrors.Note();
                    }
                }
            }

            writer.WriteObjectCount(objectCount);

            // Second pass: write each object entry.
            if (allObjects != null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject obj = allObjects[i];
                    if (obj == null || obj is Sim)
                    {
                        continue;
                    }
                    try
                    {
                        long changedTick = UpdateObjectTracker(obj, currentTick);
                        if (sinceTick >= 0L && changedTick <= sinceTick)
                        {
                            continue;
                        }
                        WriteObject2(writer, obj);
                    }
                    catch
                    {
                        SwallowedErrors.Note();
                    }
                }
            }

            byte[] payload = writer.ToArray();
            return TrySendBulkBytes(correlationId, payload);
        }

        private static void WriteSim2(Snapshot2Writer writer, Sim sim)
        {
            Vector3 position = sim.Position;
            ulong householdId = 0uL;
            Household household = sim.Household;
            if (household != null)
            {
                householdId = household.HouseholdId;
            }

            ulong interactionId = CurrentInteractionId(sim);
            string interactionKey = CurrentInteractionKey(sim);

            ulong lotId = 0uL;
            Lot simLot = sim.LotCurrent;
            if (simLot != null)
            {
                lotId = simLot.LotId;
            }

            int queueCount = -1;
            if (sim.InteractionQueue != null)
            {
                queueCount = sim.InteractionQueue.Count;
            }

            // Forward-compat: posture container + type (Gap A from resync-state-checklist.md).
            ulong postureContainerId = 0uL;
            string postureType = null;
            try
            {
                Posture posture = sim.Posture;
                if (posture != null)
                {
                    postureType = posture.GetType().FullName;
                    IGameObject container = posture.Container;
                    if (container != null)
                    {
                        postureContainerId = container.ObjectId.Value;
                    }
                }
            }
            catch
            {
                SwallowedErrors.Note();
            }

            writer.WriteUInt64(WireSimObjectId(sim));
            writer.WriteString(SafeString(sim.FullName));
            writer.WriteUInt64(householdId);
            writer.WriteSingle(position.x);
            writer.WriteSingle(position.y);
            writer.WriteSingle(position.z);
            writer.WriteUInt64(interactionId);
            writer.WriteString(interactionKey != null && interactionKey.Length > 0 ? interactionKey : null);
            writer.WriteNullableInt32(0, false); // owningPlayerId - not tracked on mod side
            writer.WriteUInt64(lotId);
            writer.WriteNullableInt32(queueCount, queueCount >= 0);
            writer.WriteUInt64(postureContainerId);
            writer.WriteString(postureType);
            writer.WriteUInt64(sim.SimDescription != null ? sim.SimDescription.SimDescriptionId : 0uL);
        }

        private static void WriteObject2(Snapshot2Writer writer, GameObject obj)
        {
            Vector3 position = obj.Position;
            ulong lotId = 0uL;
            Lot lot = obj.LotCurrent;
            if (lot != null)
            {
                lotId = lot.LotId;
            }

            writer.WriteUInt64(obj.ObjectId.Value);
            writer.WriteString(obj.GetType().FullName);
            writer.WriteSingle(position.x);
            writer.WriteSingle(position.y);
            writer.WriteSingle(position.z);
            writer.WriteUInt64(lotId);
        }

        private static void HandleProbeSimLifecycle(BridgeCommand command)
        {
            string report = BuildSimLifecycleReport();
            SendLine(BridgeCodec.SimLifecycleProbe(command.CorrelationId, "ok", report));
        }

        // M4 P4.0.1: actor-lookup parity probe. Walk LotManager.Actors and call
        // FindSim on each Sim's ObjectId; assert all resolve. The first 10 failing
        // ObjectIds are returned in the report for diagnostics. This verifies the
        // snapshot/lookup parity contract established in P4.0.
        private static void HandleProbeActorLookup(BridgeCommand command)
        {
            int total = 0;
            int resolved = 0;
            int failed = 0;
            StringBuilder failures = new StringBuilder();
            int failuresShown = 0;
            const int maxFailuresShown = 10;

            foreach (Sim sim in LotManager.Actors)
            {
                if (sim == null)
                {
                    continue;
                }

                total++;
                ulong objectId = sim.ObjectId.Value;
                Sim found = FindSim(objectId);
                if (found != null)
                {
                    resolved++;
                }
                else
                {
                    failed++;
                    if (failuresShown < maxFailuresShown)
                    {
                        if (failuresShown > 0)
                        {
                            failures.Append(",");
                        }
                        failures.Append(objectId.ToString(CultureInfo.InvariantCulture));
                        failuresShown++;
                    }
                }
            }

            string report = "total=" + total.ToString(CultureInfo.InvariantCulture) +
                ";resolved=" + resolved.ToString(CultureInfo.InvariantCulture) +
                ";failed=" + failed.ToString(CultureInfo.InvariantCulture) +
                ";failures=" + failures.ToString();
            SendLine(BridgeCodec.ActorLookupProbe(command.CorrelationId, "ok", report));
        }

        // M4 P4.1.0.3: role switching support.
        private static void HandleSetRole(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("SET_ROLE requires a role argument (server|client).");
            }

            string role = command.Parts[2];
            if (role != "server" && role != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Invalid role: " + role + " (expected 'server' or 'client')."));
                return;
            }

            sCurrentRole = role;
            if (role == "server")
            {
                sPendingPositionApplies = null;
            }
            ArmPresenterRestore(role == "client");
            ShowToast("SimsOnline role: " + role);
            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // A bulk slot holds 512 KB; a hex-encoded report doubles its text, and the reply line adds its head.
        private const int BulkReplyMaxChars = 200000;

        // Below this many chars a line fits a control slot whatever it holds (4080 bytes, at most 3 per char in UTF-8).
        private const int ControlLineFastChars = 1360;

        // A reply line's correlation id: its second field when that is one (32 hex digits, not the zero id).
        private static string ReplyCorrelationId(string line)
        {
            int first = line.IndexOf('|');
            if (first < 0)
            {
                return null;
            }

            int second = line.IndexOf('|', first + 1);
            if (second < 0 || second - first - 1 != 32)
            {
                return null;
            }

            string id = line.Substring(first + 1, 32);
            return id == "00000000000000000000000000000000" ? null : id;
        }

        private static string WatchDumpLine(string correlationId, List<string> samples, int next)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return BridgeCodec.AutonomyProbe(correlationId, "ok",
                "samples=" + MotionWatch.Count.ToString(c) + ";next=" + next.ToString(c) + ";active=" + (MotionWatch.Active ? "1" : "0")
                + ";updates=" + MotionWatch.Updates.ToString(c) + ";dropped=" + MotionWatch.Dropped.ToString(c) + "\n" + string.Join("\n", samples.ToArray()));
        }

        // A reply too big for one control slot (4 KB) goes through a host bulk slot (512 KB) in one round trip: the line
        // is published into the slot and a BULK_REF names it. The controller decodes any reply line out of a bulk slot
        // (HostBridgeServer.ProcessBulkRefLine; a non-SNAPSHOT2 payload is read as UTF-8 and decoded as a response), so
        // bridgectl gets the same report as inline. False when it does not fit or no slot is free: the caller pages.
        private static bool TrySendBulkReply(string correlationId, string line)
        {
            byte[] payload = Encoding.UTF8.GetBytes(line);
            if (payload.Length > BridgeSharedRegionLayout.BulkSlotPayloadMax || !BridgeBulkBuffer.HasWritableHostSlots(1))
            {
                return false;
            }

            int slotIndex;
            uint crc32;
            if (!BridgeBulkBuffer.TryPublishHostBulk(correlationId, payload, out slotIndex, out crc32))
            {
                return false;
            }

            SendLine(BridgeCodec.BulkRefSnapshot(correlationId, slotIndex, payload.Length, crc32));
            return true;
        }

        // presenter-restore (fork PresenterHook.ResetSavedTasks; internal documentation): on a client,
        // every Sim loaded from the server's save resets instead of resuming the task the server saved mid-interaction,
        // which would run that interaction on the client. The client controller sends SET_ROLE before LOAD_SAVE, so the
        // hook is armed when Sim.OnLoad runs. Stock gameplay or an older fork: no hook, no-op.
        private static void ArmPresenterRestore(bool client)
        {
            try
            {
                Type hook = FindGameplayType("SimsOnline.Hooks.PresenterHook");
                MethodInfo set = hook == null ? null : hook.GetMethod("SetResetSavedTasks");
                if (set != null)
                {
                    set.Invoke(null, new object[] { client });
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // M4 P4.1.1.1: client-role state write that mirrors HandleQueueInteraction
        // but gates on sCurrentRole=="client" instead of the public protocol's ownership rule.
        //
        // accepts Terrain.GoHere AND the four ROUTING autonomous interactions
        // (RoutingInteractionFilter: GoHome / VisitCommunityLot / FollowerMeetUpAtPoint /
        // LeaderGoHereWith). A routing interaction is replayed as a GoHere to the destination on the
        // wire -- the mirrored Sim WALKS its route instead of teleport-sliding on the position
        // stream. The original routing key is echoed back unchanged (below) so the host's interaction
        // column (syncmon --client-snapshot) still shows GoHome, not GoHere. Terminal interactions
        // replay via TerminalInteractionFilter + TryQueueTerminalInteractionReplay ( Phase 2.5).
        private static InteractionDefinition ResolveTerminalInteractionDefinition(string interactionKey, Sim actor, GameObject target)
        {
            Book book = target as Book;
            if (interactionKey.StartsWith("Sims3.Gameplay.Objects.ReadBook+"))
            {
                if (book != null && actor.Inventory != null && actor.Inventory.Contains(book))
                {
                    return ReadBook.FromSimInventorySingleton;
                }
                return ReadBook.NotFromInventorySingleton;
            }

            if (interactionKey.StartsWith("Sims3.Gameplay.Objects.Bookshelf_ReadSomething+"))
            {
                return Bookshelf_ReadSomething.Singleton;
            }

            if (interactionKey.StartsWith("Sims3.Gameplay.InteractionsShared.Stand+"))
            {
                return Stand.Singleton;
            }

            if (interactionKey.StartsWith("Sims3.Gameplay.InteractionsShared.Sit+"))
            {
                return Sit.Singleton;
            }

            return TryResolveSingletonDefinitionByReflection(interactionKey);
        }

        private static InteractionDefinition TryResolveSingletonDefinitionByReflection(string interactionKey)
        {
            string typeName = interactionKey;
            if (typeName.EndsWith("+Definition"))
            {
                typeName = typeName.Substring(0, typeName.Length - "+Definition".Length);
            }

            System.Type type = typeof(Sim).Assembly.GetType(typeName);
            if (type == null)
            {
                type = typeof(Book).Assembly.GetType(typeName);
            }
            if (type == null)
            {
                type = typeof(Phone).Assembly.GetType(typeName);
            }
            if (type == null)
            {
                return null;
            }

            System.Reflection.FieldInfo singletonField = type.GetField(
                "Singleton",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (singletonField != null)
            {
                object value = singletonField.GetValue(null);
                if (value is InteractionDefinition)
                {
                    return (InteractionDefinition)value;
                }
            }

            return null;
        }

        // shared apply core for APPLY_INTERACTION_QUEUE (single, wire response) and
        // APPLY_INTERACTIONS (batch, silent per item). Returns true when queued/applied.
        private static bool TryApplyInteractionFields(
            ulong actorObjectId,
            string interactionKey,
            string targetIdStr,
            string hasPosStr,
            string xStr,
            string yStr,
            string zStr,
            bool emitWireResponse,
            string correlationId,
            string interactionParameters)
        {
            if (interactionParameters == null)
            {
                interactionParameters = string.Empty;
            }
            bool hasPosition = hasPosStr == "1";
            ulong targetObjectId = 0uL;
            if (targetIdStr != null && targetIdStr.Length > 0)
            {
                targetObjectId = ulong.Parse(targetIdStr, CultureInfo.InvariantCulture);
            }

            Sim actor = FindSim(actorObjectId);
            if (actor == null)
            {
                if (emitWireResponse)
                {
                    SendLine(BridgeCodec.Error(correlationId, "Sim not found: " + actorObjectId.ToString(CultureInfo.InvariantCulture)));
                }
                return false;
            }

            RepairOrphanedSocializingPosture(actor);

            bool directedGoHere = interactionKey == "Terrain.GoHere" || interactionKey.Contains("Terrain+GoHere");

            if (sCurrentRole == "client" && (TerminalInteractionFilter.IsSocialApproachReplay(interactionKey) || directedGoHere))
            {
                TryClearBlockingPuppetActivity(actor);
            }

            SelectSim(actor, SelectCause.Replay);

            if (TerminalInteractionFilter.IsTerminalReplay(interactionKey))
            {
                if (targetObjectId == 0uL)
                {
                    if (emitWireResponse)
                    {
                        SendLine(BridgeCodec.Error(correlationId, "Terminal interaction requires a target object id."));
                    }
                    return false;
                }

                GameObject terminalTarget = TryResolveTerminalReplayTarget(actor, interactionKey, targetObjectId, interactionParameters);
                if (terminalTarget != null)
                {
                    // client social approach = GoHere route only — never queue terminal
                    // SocializeWith (client FindBestSocial picks wrong social).
                    if (sCurrentRole == "client" && TerminalInteractionFilter.IsSocialApproachReplay(interactionKey))
                    {
                        // Fall through to the GoHere route-replay arm below.
                    }
                    else
                    {
                        return TryQueueTerminalInteractionReplayFields(
                            actor, actorObjectId, interactionKey, targetObjectId,
                            targetIdStr, hasPosStr, xStr, yStr, zStr,
                            emitWireResponse, correlationId, terminalTarget, interactionParameters);
                    }
                }

                // Client sandbox may not have the remote Sim loaded — walk to the emitted position.
                if (interactionKey.StartsWith("Sims3.Gameplay.Actors.Sim+SocializeWith+") && hasPosition)
                {
                    // Fall through to the GoHere route-replay arm below.
                }
                else
                {
                    if (emitWireResponse)
                    {
                        SendLine(BridgeCodec.Error(correlationId, "Target object not found: " + targetObjectId.ToString(CultureInfo.InvariantCulture)));
                    }
                    return false;
                }
            }
            else if (!TerminalInteractionFilter.IsRouteReplay(interactionKey))
            {
                // stage-2 safety net ( doctrine: unknown interactions must degrade
                // to a route/slide, NEVER reject/wedge). This is the mirror-apply path ONLY
                // (HandleApplyInteractionQueue / HandleApplyInteractions, both client role); the
                // captured-click QUEUE_INTERACTION path (HandleQueueInteraction) keeps its
                // fail-closed GoHere-only allowlist ( intentional fail-closed apply -- see
                // docs/engine/autonomy-target-position-emit.md). An unknown key that carries a
                // routable target degrades to a GoHere route-to-target (the same outcome the
                // controller's ReplayAsRoute policy already intends for unknown+target); one with
                // no routable target degrades to SlideOnly -- the snapshot stream already carries
                // position, so there is nothing to replay. We NEVER emit BridgeCodec.Error here: an
                // "ERROR|" wire line is a logq WEDGE_PATTERN and trips mirror_dispatch_partial_failure
                // (gate 2e), which is exactly the stage-2 blocker this removes.
                bool canRouteUnknown = hasPosition || targetObjectId != 0uL;
                if (!canRouteUnknown)
                {
                    if (emitWireResponse)
                    {
                        SendLine(BridgeCodec.Ok(correlationId));
                    }
                    return true;
                }
                // unknown + routable target: fall through to the GoHere route arm below.
            }

            Vector3 destination;
            bool walkToTarget = !hasPosition && targetObjectId != 0uL;
            if (hasPosition)
            {
                float x = float.Parse(xStr, CultureInfo.InvariantCulture);
                float y = float.Parse(yStr, CultureInfo.InvariantCulture);
                float z = float.Parse(zStr, CultureInfo.InvariantCulture);
                destination = SnapDestinationToTerrain(new Vector3(x, y, z));
            }
            else if (walkToTarget)
            {
                Sims3.Gameplay.Abstracts.GameObject target = FindTargetObject(targetObjectId);
                if (target == null)
                {
                    if (emitWireResponse)
                    {
                        SendLine(BridgeCodec.Error(correlationId, "Target object not found: " + targetObjectId.ToString(CultureInfo.InvariantCulture)));
                    }
                    return false;
                }
                destination = SnapDestinationToTerrain(target.Position);
            }
            else
            {
                if (emitWireResponse)
                {
                    SendLine(BridgeCodec.Error(correlationId, "Interaction replay requires a target position or target object id."));
                }
                return false;
            }

            InteractionPriority priority = directedGoHere
                ? new InteractionPriority(InteractionPriorityLevel.UserDirected)
                : new InteractionPriority(InteractionPriorityLevel.Autonomous);
            InteractionDefinition definition = Terrain.GoHere.GetSingleton(actor, destination);
            Terrain.GoHere goHere = definition.CreateInstanceWithCallbacks(
                Terrain.Singleton,
                actor,
                priority,
                false,
                true,
                new Callback(OnInteractionStarted),
                new Callback(OnInteractionCompleted),
                new Callback(OnInteractionFailed)) as Terrain.GoHere;

            if (goHere == null)
            {
                if (emitWireResponse)
                {
                    SendLine(BridgeCodec.Error(correlationId, "Terrain.GoHere did not create an interaction instance."));
                }
                return false;
            }

            goHere.SetDestination(destination, false);

            bool added;
            sIsProcessingQueueChange = true;
            try
            {
                added = actor.InteractionQueue.AddNext(goHere);
            }
            finally
            {
                sIsProcessingQueueChange = false;
            }

            if (!added)
            {
                if (emitWireResponse)
                {
                    SendLines(new string[] {
                        BridgeCodec.EventInteractionFailed(actorObjectId, goHere.Id, "InteractionQueue.AddNext returned false."),
                        BridgeCodec.Error(correlationId, "Terrain.GoHere could not be queued.")
                    });
                }
                return false;
            }

            if (sTrackedActiveActor == actor && sClientQueueKnownInteractions != null)
            {
                sClientQueueKnownInteractions.Add(goHere);
            }

            sTrackedInteractionIds[actorObjectId] = goHere.Id;
            if (emitWireResponse)
            {
                SendLines(new string[] {
                    BridgeCodec.EventInteractionQueued(actorObjectId, goHere.Id, interactionKey, targetIdStr, hasPosStr, xStr, yStr, zStr),
                    BridgeCodec.Ok(correlationId)
                });
            }

            return true;
        }

        private static bool ShouldPushReadBookAsContinuation(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            if (!interactionKey.StartsWith("Sims3.Gameplay.Objects.ReadBook+"))
            {
                return false;
            }

            return !interactionKey.StartsWith("Sims3.Gameplay.Objects.ReadBookChooser+");
        }

        private static InteractionInstance GetHeadOrRunningInteraction(Sim actor)
        {
            InteractionInstance head = null;
            try
            {
                head = actor.InteractionQueue.GetHeadInteraction();
            }
            catch
            {
                SwallowedErrors.Note();
            }

            if (head == null)
            {
                try
                {
                    head = actor.InteractionQueue.RunningInteraction;
                }
                catch
                {
                    SwallowedErrors.Note();
                }
            }

            return head;
        }

        private static GameObject TryResolveTerminalReplayTarget(Sim actor, string interactionKey, ulong targetObjectId, string interactionParameters)
        {
            GameObject target = FindTargetObject(targetObjectId);
            if (target != null)
            {
                return target;
            }

            if (InteractionReplayApplySorter.UsesInventoryBookTargetFallback(interactionKey)
                && actor != null
                && actor.Inventory != null)
            {
                string bookId;
                BookGeneral book = actor.Inventory.Find<BookGeneral>();
                if (TryGetInteractionParameterValue(interactionParameters, "bookId", out bookId))
                {
                    if (IsReplayBookId(book, bookId))
                    {
                        return book;
                    }

                    book = TryCreateInventoryBook(actor, bookId);
                    if (book != null)
                    {
                        return book;
                    }
                }

                book = actor.Inventory.Find<BookGeneral>();
                if (book != null)
                {
                    return book;
                }
            }

            return null;
        }

        private static bool IsReplayBookId(Book book, string bookId)
        {
            return book != null
                && book.Data != null
                && book.Data.ID == bookId;
        }

        private static BookGeneral TryCreateInventoryBook(Sim actor, string bookId)
        {
            if (actor == null || actor.Inventory == null || bookId == null || bookId.Length == 0)
            {
                return null;
            }

            BookGeneralData data;
            try
            {
                if (!BookData.BookGeneralDataList.TryGetValue(bookId, out data) || data == null)
                {
                    return null;
                }

                BookGeneral book = BookGeneral.CreateOutOfWorld(data);
                if (book == null)
                {
                    return null;
                }

                if (!actor.Inventory.TryToAdd(book))
                {
                    TryDestroyUnaddedBook(book);
                    return null;
                }

                return book;
            }
            catch
            {
                return null;
            }
        }

        private static void TryDestroyUnaddedBook(BookGeneral book)
        {
            if (book == null)
            {
                return;
            }

            try
            {
                book.Destroy();
            }
            catch
            {
                SwallowedErrors.Note();
            }
        }

        private static bool TryQueueTerminalInteractionReplayFields(
            Sim actor,
            ulong actorObjectId,
            string interactionKey,
            ulong targetObjectId,
            string targetIdStr,
            string hasPosStr,
            string xStr,
            string yStr,
            string zStr,
            bool emitWireResponse,
            string correlationId,
            GameObject target,
            string interactionParameters)
        {
            if (interactionParameters == null)
            {
                interactionParameters = string.Empty;
            }

            if (target == null)
            {
                target = TryResolveTerminalReplayTarget(actor, interactionKey, targetObjectId, interactionParameters);
            }

            if (target == null)
            {
                if (emitWireResponse)
                {
                    SendLine(BridgeCodec.Error(correlationId, "Target object not found: " + targetObjectId.ToString(CultureInfo.InvariantCulture)));
                }
                return false;
            }

            Book bookTarget = target as Book;
            if (bookTarget != null)
            {
                TrySeedReadBookProgress(actor, bookTarget, interactionParameters);
            }

            RepairOrphanedSocializingPosture(actor);

            if (interactionKey.StartsWith("Sims3.Gameplay.Actors.Sim+SocializeWith+"))
            {
                TryQueueStandBeforeSocialApproach(actor);
            }

            InteractionDefinition definition = ResolveTerminalInteractionDefinition(interactionKey, actor, target);
            if (definition == null)
            {
                if (emitWireResponse)
                {
                    SendLine(BridgeCodec.Error(correlationId, "Terminal interaction definition not resolved: " + interactionKey));
                }
                return false;
            }

            InteractionPriority priority = CreateReplayInteractionPriority(interactionParameters);
            InteractionInstance instance = definition.CreateInstanceWithCallbacks(
                target,
                actor,
                priority,
                false,
                true,
                new Callback(OnInteractionStarted),
                new Callback(OnInteractionCompleted),
                new Callback(OnInteractionFailed)) as InteractionInstance;

            if (instance == null)
            {
                if (emitWireResponse)
                {
                    SendLine(BridgeCodec.Error(correlationId, "Terminal interaction did not create an instance: " + interactionKey));
                }
                return false;
            }

            TrySetSitReplayPoint(instance, interactionKey, hasPosStr, xStr, yStr, zStr, interactionParameters, target);

            bool added;
            sIsProcessingQueueChange = true;
            try
            {
                if (ShouldPushReadBookAsContinuation(interactionKey))
                {
                    InteractionInstance head = GetHeadOrRunningInteraction(actor);
                    if (head != null)
                    {
                        added = actor.InteractionQueue.PushAsContinuation(head, instance, true, false);
                    }
                    else
                    {
                        added = actor.InteractionQueue.AddNext(instance);
                    }
                }
                else if (SitTransitionEmitFilter.IsSitTransitionReplay(interactionKey))
                {
                    InteractionInstance head = null;
                    try
                    {
                        head = actor.InteractionQueue.GetHeadInteraction();
                    }
                    catch
                    {
                        SwallowedErrors.Note();
                    }

                    if (head == null)
                    {
                        try
                        {
                            head = actor.InteractionQueue.RunningInteraction;
                        }
                        catch
                        {
                            SwallowedErrors.Note();
                        }
                    }

                    if (head != null)
                    {
                        added = actor.InteractionQueue.PushAsContinuation(head, instance, true, false);
                    }
                    else
                    {
                        added = actor.InteractionQueue.AddNext(instance);
                    }
                }
                else
                {
                    added = actor.InteractionQueue.AddNext(instance);
                }
            }
            finally
            {
                sIsProcessingQueueChange = false;
            }

            if (!added)
            {
                if (IsRequestedReadBookAlreadyRunning(actor, interactionKey, target))
                {
                    ulong appliedInteractionId = CurrentInteractionId(actor);
                    if (sTrackedActiveActor == actor && sClientQueueKnownInteractions != null)
                    {
                        InteractionInstance current = CurrentInteractionInstance(actor);
                        if (current != null)
                        {
                            sClientQueueKnownInteractions.Add(current);
                        }
                    }

                    sTrackedInteractionIds[actorObjectId] = appliedInteractionId;
                    if (emitWireResponse)
                    {
                        SendLines(new string[] {
                            BridgeCodec.EventInteractionQueued(actorObjectId, appliedInteractionId, interactionKey, targetIdStr, hasPosStr, xStr, yStr, zStr),
                            BridgeCodec.Ok(correlationId)
                        });
                    }

                    return true;
                }

                if (emitWireResponse)
                {
                    SendLines(new string[] {
                        BridgeCodec.EventInteractionFailed(actorObjectId, instance.Id, "InteractionQueue.AddNext returned false."),
                        BridgeCodec.Error(correlationId, "Terminal interaction could not be queued.")
                    });
                }
                return false;
            }

            if (sTrackedActiveActor == actor && sClientQueueKnownInteractions != null)
            {
                sClientQueueKnownInteractions.Add(instance);
            }

            sTrackedInteractionIds[actorObjectId] = instance.Id;
            if (emitWireResponse)
            {
                SendLines(new string[] {
                    BridgeCodec.EventInteractionQueued(actorObjectId, instance.Id, interactionKey, targetIdStr, hasPosStr, xStr, yStr, zStr),
                    BridgeCodec.Ok(correlationId)
                });
            }

            return true;
        }

        private static bool IsRequestedReadBookAlreadyRunning(Sim actor, string interactionKey, GameObject target)
        {
            if (!ShouldPushReadBookAsContinuation(interactionKey) || actor == null)
            {
                return false;
            }

            InteractionInstance current = CurrentInteractionInstance(actor);
            if (current == null || current.InteractionDefinition == null)
            {
                return false;
            }

            string currentKey = current.InteractionDefinition.GetType().FullName;
            if (!ShouldPushReadBookAsContinuation(currentKey))
            {
                return false;
            }

            Book expectedBook = target as Book;
            Book currentBook = current.Target as Book;
            if (expectedBook == null || currentBook == null)
            {
                return false;
            }

            if (expectedBook.ObjectId.mValue == currentBook.ObjectId.mValue)
            {
                return true;
            }

            return IsSameBookData(expectedBook, currentBook);
        }

        private static InteractionPriority CreateReplayInteractionPriority(string interactionParameters)
        {
            return new InteractionPriority(ResolveReplayPriorityLevel(interactionParameters));
        }

        private static InteractionPriorityLevel ResolveReplayPriorityLevel(string interactionParameters)
        {
            if (sCurrentRole != "client")
            {
                return InteractionPriorityLevel.Autonomous;
            }

            if (IsReplayParameterTrue(interactionParameters, "userDirected"))
            {
                return InteractionPriorityLevel.UserDirected;
            }

            if (IsReplayParameterTrue(interactionParameters, "autonomous")
                || IsReplayParameterTrue(interactionParameters, "effectivelyAutonomous"))
            {
                return InteractionPriorityLevel.Autonomous;
            }

            return InteractionPriorityLevel.UserDirected;
        }

        private static bool IsReplayParameterTrue(string parameters, string key)
        {
            string value = ReadParameter(parameters, key);
            return value == "1" || value == "True" || value == "true";
        }

        private static bool IsSameBookData(Book left, Book right)
        {
            return left != null
                && right != null
                && left.Data != null
                && right.Data != null
                && left.Data.ID == right.Data.ID;
        }

        private static void TryQueueTerminalInteractionReplay(
            BridgeCommand command,
            Sim actor,
            ulong actorObjectId,
            string interactionKey,
            ulong targetObjectId)
        {
            TryQueueTerminalInteractionReplayFields(
                actor,
                actorObjectId,
                interactionKey,
                targetObjectId,
                command.Parts[4],
                command.Parts[5],
                command.Parts[6],
                command.Parts[7],
                command.Parts[8],
                true,
                command.CorrelationId,
                null,
                string.Empty);
        }

        // batched interaction replay — one frame in, N applies in one tick, one OK out.
        // Wire: APPLY_INTERACTIONS|<corr>|<actor:hexKey:targetId:hasPos:x:y:z>;...
        private static void HandleApplyInteractions(BridgeCommand command)
        {
            if (command.Parts.Length < 3)
            {
                throw new InvalidOperationException("APPLY_INTERACTIONS requires a payload.");
            }

            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_INTERACTIONS rejected: role is '" + sCurrentRole + "' (must be 'client'). Send SET_ROLE first."));
                return;
            }

            string payload = command.Parts[2];
            if (payload == null || payload.Length == 0)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_INTERACTIONS payload is empty."));
                return;
            }

            string[] items = payload.Split(';');
            int applied = 0;
            int skipped = 0;

            // Pass 1: precursors + terminal head (ReadBook) before Sit transition replay.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < items.Length; i++)
                {
                    string item = items[i];
                    if (item == null || item.Length == 0)
                    {
                        continue;
                    }

                    string[] f = item.Split(':');
                    if (f.Length < 7)
                    {
                        if (pass == 0)
                        {
                            skipped++;
                        }
                        continue;
                    }

                    string interactionKey = BridgeCodec.DecodeText(f[1]);
                    bool isSitTransition = InteractionReplayApplySorter.IsSitTransitionDeferred(interactionKey);
                    if ((pass == 0 && isSitTransition) || (pass == 1 && !isSitTransition))
                    {
                        continue;
                    }

                    ulong actorObjectId;
                    try
                    {
                        actorObjectId = ulong.Parse(f[0], CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        if (pass == 1)
                        {
                            skipped++;
                        }
                        continue;
                    }

                    string interactionParameters = string.Empty;
                    if (f.Length >= 8)
                    {
                        interactionParameters = BridgeCodec.DecodeText(f[7]);
                    }

                    if (TryApplyInteractionFields(
                        actorObjectId,
                        interactionKey,
                        f[2],
                        f[3],
                        f[4],
                        f[5],
                        f[6],
                        false,
                        command.CorrelationId,
                        interactionParameters))
                    {
                        applied++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            if (applied == 0)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_INTERACTIONS applied 0 interactions (skipped=" + skipped.ToString(CultureInfo.InvariantCulture) + ")."));
                return;
            }

            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // authoritative posture/container alignment on the client puppet. First family:
        // Sit transition queued via PushAsContinuation against postureContainerObjectId (the seat).
        private static void HandleApplyForceAlign(BridgeCommand command)
        {
            if (command.Parts.Length < 6)
            {
                throw new InvalidOperationException("APPLY_FORCE_ALIGN requires actor, key, posture container, and transition flag.");
            }

            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_FORCE_ALIGN rejected: role is '" + sCurrentRole + "' (must be 'client'). Send SET_ROLE first."));
                return;
            }

            ulong actorObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            string interactionKey = BridgeCodec.DecodeText(command.Parts[3]);
            ulong postureContainerObjectId = ulong.Parse(command.Parts[4], CultureInfo.InvariantCulture);
            string forceAlignParameters = command.Parts.Length >= 7 ? BridgeCodec.DecodeText(command.Parts[6]) : string.Empty;
            TryApplyForceAlignFields(
                actorObjectId,
                interactionKey,
                postureContainerObjectId,
                forceAlignParameters,
                true,
                command.CorrelationId);
        }

        private static bool TryApplyForceAlignFields(
            ulong actorObjectId,
            string interactionKey,
            ulong postureContainerObjectId,
            string forceAlignParameters,
            bool emitWireResponse,
            string correlationId)
        {
            if (postureContainerObjectId == 0uL || postureContainerObjectId == actorObjectId)
            {
                if (emitWireResponse)
                {
                    SendLine(BridgeCodec.Ok(correlationId));
                }

                return true;
            }

            if (SitTransitionEmitFilter.IsSitTransitionReplay(interactionKey))
            {
                Sim actor = FindSim(actorObjectId);
                if (actor == null)
                {
                    if (emitWireResponse)
                    {
                        SendLine(BridgeCodec.Error(correlationId, "Sim not found: " + actorObjectId.ToString(CultureInfo.InvariantCulture)));
                    }

                    return false;
                }

                if (TryApplyExactSeatForceAlign(actor, interactionKey, postureContainerObjectId, forceAlignParameters, emitWireResponse, correlationId))
                {
                    return true;
                }

                // ForceAlign direct-seat failed (Sim not at chair routing slot).
                // Queue a GoHere to the chair first, then push the Sit as a continuation
                // so the Sim routes to the chair before sitting. This handles the case
                // where the Sim is standing far from the seat when ForceAlign fires.
                string targetIdStr = postureContainerObjectId.ToString(CultureInfo.InvariantCulture);
                string hasPos = "0";
                string x = "0";
                string y = "0";
                string z = "0";
                Vector3 point;
                if (TryResolveForceAlignSeatPoint(postureContainerObjectId, forceAlignParameters, out point))
                {
                    hasPos = "1";
                    x = point.x.ToString(CultureInfo.InvariantCulture);
                    y = point.y.ToString(CultureInfo.InvariantCulture);
                    z = point.z.ToString(CultureInfo.InvariantCulture);
                }

                if (hasPos == "1" && TryQueueGoHereThenSit(actor, postureContainerObjectId, point, forceAlignParameters, emitWireResponse, correlationId))
                {
                    return true;
                }

                return TryQueueTerminalInteractionReplayFields(
                    actor,
                    actorObjectId,
                    interactionKey,
                    postureContainerObjectId,
                    targetIdStr,
                    hasPos,
                    x,
                    y,
                    z,
                    emitWireResponse,
                    correlationId,
                    null,
                    forceAlignParameters);
            }

            if (interactionKey.StartsWith("Sims3.Gameplay.InteractionsShared.Stand+"))
            {
                Sim actor = FindSim(actorObjectId);
                if (actor == null)
                {
                    if (emitWireResponse)
                    {
                        SendLine(BridgeCodec.Error(correlationId, "Sim not found: " + actorObjectId.ToString(CultureInfo.InvariantCulture)));
                    }

                    return false;
                }

                return TryQueueTerminalInteractionReplayFields(
                    actor,
                    actorObjectId,
                    interactionKey,
                    actorObjectId,
                    actorObjectId.ToString(CultureInfo.InvariantCulture),
                    "0",
                    "0",
                    "0",
                    "0",
                    emitWireResponse,
                    correlationId,
                    null,
                    string.Empty);
            }

            if (emitWireResponse)
            {
                SendLine(BridgeCodec.Ok(correlationId));
            }

            return true;
        }

        private static bool TryApplyExactSeatForceAlign(
            Sim actor,
            string interactionKey,
            ulong postureContainerObjectId,
            string forceAlignParameters,
            bool emitWireResponse,
            string correlationId)
        {
            if (forceAlignParameters == null || forceAlignParameters.Length == 0)
            {
                return false;
            }

            GameObject target = new ObjectGuid(postureContainerObjectId).ObjectFromId<GameObject>();
            if (target == null)
            {
                return false;
            }

            Seat seat = ResolveSeatFromForceAlignParameters(target, actor, forceAlignParameters);
            if (seat == null)
            {
                return false;
            }

            if (IsSeatOccupiedByOtherLivePosture(seat, actor))
            {
                return false;
            }

            if (TryDirectExactSeatSitDown(actor, target, seat, forceAlignParameters))
            {
                if (emitWireResponse)
                {
                    SendLine(BridgeCodec.Ok(correlationId));
                }

                return true;
            }

            return false;
        }

        private static bool TryDirectExactSeatSitDown(Sim actor, GameObject target, Seat seat, string forceAlignParameters)
        {
            if (actor == null || target == null || seat == null)
            {
                return false;
            }

            try
            {
                Slot routingSlot = ResolveRoutingSlotFromForceAlignParameters(seat, forceAlignParameters);
                ClearSeatOccupancyForSim(actor, target);
                ClearStaleSeatOccupant(seat, target);
                if (!seat.SitDown(actor, routingSlot, null, false))
                {
                    ClearStaleSeatOccupant(seat, target);
                    return false;
                }

                return IsActorInSeat(actor, seat);
            }
            catch
            {
                ClearStaleSeatOccupant(seat, target);
                return false;
            }
        }

        private static bool IsActorInSeat(Sim actor, Seat seat)
        {
            if (actor == null || seat == null)
            {
                return false;
            }

            ISeatPosture seatPosture = actor.Posture as ISeatPosture;
            return seatPosture != null && seatPosture.Seat == seat;
        }

        // When direct-seat ForceAlign fails (Sim is far from chair),
        // queue a GoHere to the chair position, then push the Sit as a continuation
        // so the Sim routes to the chair before sitting. This fixes the timing gap
        // where ReadBook terminal queue fails because the Sim hasn't reached the seat.
        private static bool TryQueueGoHereThenSit(
            Sim actor,
            ulong seatObjectId,
            Vector3 seatPoint,
            string forceAlignParameters,
            bool emitWireResponse,
            string correlationId)
        {
            if (actor == null)
            {
                return false;
            }

            // Resolve the seat object for the Sit target
            GameObject seatTarget = new ObjectGuid(seatObjectId).ObjectFromId<GameObject>();
            if (seatTarget == null)
            {
                return false;
            }

            // Create GoHere to the chair position
            InteractionDefinition goHereDef = Terrain.GoHere.GetSingleton(actor, seatPoint);
            Terrain.GoHere goHere = goHereDef.CreateInstanceWithCallbacks(
                Terrain.Singleton,
                actor,
                CreateReplayInteractionPriority(forceAlignParameters),
                false,
                true,
                new Callback(OnInteractionStarted),
                new Callback(OnInteractionCompleted),
                new Callback(OnInteractionFailed)) as Terrain.GoHere;

            if (goHere == null)
            {
                return false;
            }

            goHere.SetDestination(seatPoint, false);

            // Queue the GoHere
            bool goHereAdded;
            sIsProcessingQueueChange = true;
            try
            {
                goHereAdded = actor.InteractionQueue.AddNext(goHere);
            }
            finally
            {
                sIsProcessingQueueChange = false;
            }

            if (!goHereAdded)
            {
                return false;
            }

            if (sTrackedActiveActor == actor && sClientQueueKnownInteractions != null)
            {
                sClientQueueKnownInteractions.Add(goHere);
            }

            // Create the Sit interaction targeting the seat
            InteractionDefinition sitDef = ResolveTerminalInteractionDefinition(
                "Sims3.Gameplay.InteractionsShared.Sit+Definition",
                actor,
                seatTarget);
            if (sitDef == null)
            {
                sitDef = Sit.Singleton;
            }

            InteractionInstance sitInstance = sitDef.CreateInstanceWithCallbacks(
                seatTarget,
                actor,
                CreateReplayInteractionPriority(forceAlignParameters),
                false,
                true,
                new Callback(OnInteractionStarted),
                new Callback(OnInteractionCompleted),
                new Callback(OnInteractionFailed)) as InteractionInstance;

            if (sitInstance == null)
            {
                return false;
            }

            // Push the Sit as a continuation of the GoHere so it executes after routing
            bool sitAdded;
            sIsProcessingQueueChange = true;
            try
            {
                sitAdded = actor.InteractionQueue.PushAsContinuation(goHere, sitInstance, true, false);
            }
            finally
            {
                sIsProcessingQueueChange = false;
            }

            if (!sitAdded)
            {
                return false;
            }

            if (sTrackedActiveActor == actor && sClientQueueKnownInteractions != null)
            {
                sClientQueueKnownInteractions.Add(sitInstance);
            }

            sTrackedInteractionIds[actor.ObjectId.Value] = sitInstance.Id;

            if (emitWireResponse)
            {
                SendLines(new string[] {
                    BridgeCodec.EventInteractionQueued(
                        actor.ObjectId.Value,
                        sitInstance.Id,
                        "Sims3.Gameplay.InteractionsShared.Sit+Definition",
                        seatObjectId.ToString(CultureInfo.InvariantCulture),
                        "1",
                        seatPoint.x.ToString(CultureInfo.InvariantCulture),
                        seatPoint.y.ToString(CultureInfo.InvariantCulture),
                        seatPoint.z.ToString(CultureInfo.InvariantCulture)),
                    BridgeCodec.Ok(correlationId)
                });
            }

            return true;
        }

        private static void TrySetSitReplayPoint(
            InteractionInstance instance,
            string interactionKey,
            string hasPosStr,
            string xStr,
            string yStr,
            string zStr,
            string interactionParameters,
            GameObject target)
        {
            if (instance == null || !SitTransitionEmitFilter.IsSitTransitionReplay(interactionKey))
            {
                return;
            }

            Vector3 point;
            if (!TryReadPoint(interactionParameters, out point)
                && !TryParsePositionFields(hasPosStr, xStr, yStr, zStr, out point)
                && !TryResolveSeatPoint(target, interactionParameters, out point))
            {
                return;
            }

            SetPrivateVector3Field(instance, "Point", point);
        }

        private static bool TryResolveForceAlignSeatPoint(ulong postureContainerObjectId, string forceAlignParameters, out Vector3 point)
        {
            point = Vector3.Zero;
            GameObject target = new ObjectGuid(postureContainerObjectId).ObjectFromId<GameObject>();
            return TryResolveSeatPoint(target, forceAlignParameters, out point);
        }

        private static bool TryResolveSeatPoint(GameObject target, string parameters, out Vector3 point)
        {
            point = Vector3.Zero;
            if (target == null)
            {
                return false;
            }

            Seat seat = ResolveSeatFromForceAlignParameters(target, null, parameters);
            if (seat == null)
            {
                return false;
            }

            point = Slots.GetPositionOfSlot(target.ObjectId, (uint)(int)seat.ContainmentSlot);
            return IsFiniteInWorldPosition(point);
        }

        private static bool TryParsePositionFields(string hasPosStr, string xStr, string yStr, string zStr, out Vector3 point)
        {
            point = Vector3.Zero;
            if (hasPosStr != "1")
            {
                return false;
            }

            float x;
            float y;
            float z;
            if (!float.TryParse(xStr, NumberStyles.Float, CultureInfo.InvariantCulture, out x))
            {
                return false;
            }

            if (!float.TryParse(yStr, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            {
                return false;
            }

            if (!float.TryParse(zStr, NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            {
                return false;
            }

            point = new Vector3(x, y, z);
            return IsFiniteInWorldPosition(point);
        }

        private static void SetPrivateVector3Field(object instance, string fieldName, Vector3 value)
        {
            if (instance == null || fieldName == null || fieldName.Length == 0)
            {
                return;
            }

            Type type = instance.GetType();
            while (type != null)
            {
                System.Reflection.FieldInfo field = type.GetField(
                    fieldName,
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public);
                if (field != null)
                {
                    field.SetValue(instance, value);
                    return;
                }

                type = type.BaseType;
            }
        }

        private static Seat ResolveSeatFromForceAlignParameters(GameObject target, Sim actor, string parameters)
        {
            Slot containmentSlot;
            if (TryParseSlotParameter(parameters, "seatContainmentSlot", out containmentSlot))
            {
                Seat bySlot = FindSeatByContainmentSlot(target, containmentSlot);
                if (bySlot != null)
                {
                    return bySlot;
                }
            }

            string areaText = ReadParameter(parameters, "seatArea");
            if (areaText.Length > 0)
            {
                Seat byArea = FindSeatByArea(target, areaText);
                if (byArea != null)
                {
                    return byArea;
                }
            }

            Vector3 hitPoint;
            if (TryReadPoint(parameters, out hitPoint))
            {
                SitData byHit = FindSeatClosestToPoint(target, hitPoint, false);
                if (byHit is Seat)
                {
                    return byHit as Seat;
                }
            }

            SitData current = TryGetCurrentSeatData(actor, target);
            return current as Seat;
        }

        private static Seat FindSeatByContainmentSlot(GameObject target, Slot containmentSlot)
        {
            SitData[] seats = GetSitDataArray(target);
            for (int i = 0; i < seats.Length; i++)
            {
                Seat seat = seats[i] as Seat;
                if (seat != null && seat.ContainmentSlot == containmentSlot)
                {
                    return seat;
                }
            }

            return null;
        }

        private static Seat FindSeatByArea(GameObject target, string areaText)
        {
            SitData[] seats = GetSitDataArray(target);
            for (int i = 0; i < seats.Length; i++)
            {
                Seat seat = seats[i] as Seat;
                if (seat != null && seat.Area.ToString() == areaText)
                {
                    return seat;
                }
            }

            return null;
        }

        private static Slot ResolveRoutingSlotFromForceAlignParameters(Seat seat, string parameters)
        {
            Slot routingSlot;
            if (TryParseSlotParameter(parameters, "seatRoutingSlot", out routingSlot))
            {
                return routingSlot;
            }

            return ResolveDefaultRoutingSlotForSeat(seat);
        }

        private static bool TryParseSlotParameter(string parameters, string key, out Slot slot)
        {
            slot = (Slot)0;
            string value = ReadParameter(parameters, key);
            if (value.Length == 0)
            {
                return false;
            }

            int raw;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
            {
                return false;
            }

            slot = (Slot)raw;
            return true;
        }

        private static bool TryReadPoint(string parameters, out Vector3 point)
        {
            point = Vector3.Zero;
            float x;
            float y;
            float z;
            if (!float.TryParse(ReadParameter(parameters, "hitX"), NumberStyles.Float, CultureInfo.InvariantCulture, out x))
            {
                return false;
            }

            if (!float.TryParse(ReadParameter(parameters, "hitY"), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            {
                return false;
            }

            if (!float.TryParse(ReadParameter(parameters, "hitZ"), NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            {
                return false;
            }

            point = new Vector3(x, y, z);
            return IsFiniteInWorldPosition(point) && point != Vector3.Zero;
        }

        private static string ReadParameter(string parameters, string key)
        {
            if (parameters == null || parameters.Length == 0)
            {
                return string.Empty;
            }

            string[] segments = parameters.Split(',');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                int eq = segment.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                if (segment.Substring(0, eq) == key)
                {
                    return segment.Substring(eq + 1);
                }
            }

            return string.Empty;
        }

        private static bool IsSeatOccupiedByOtherLivePosture(Seat seat, Sim actor)
        {
            if (seat == null || seat.ContainedSim == null || seat.ContainedSim == actor)
            {
                return false;
            }

            ISeatPosture seatPosture = seat.ContainedSim.Posture as ISeatPosture;
            return seatPosture != null && seatPosture.Seat == seat;
        }

        private static void ClearStaleSeatOccupant(Seat seat, GameObject target)
        {
            if (seat == null || seat.ContainedSim == null)
            {
                return;
            }

            ISeatPosture seatPosture = seat.ContainedSim.Posture as ISeatPosture;
            if (seatPosture == null || seatPosture.Seat != seat)
            {
                seat.SetContainedSim(null, target.PartComponent);
            }
        }

        private static void ClearSeatOccupancyForSim(Sim actor, GameObject target)
        {
            ClearSeatOccupancyForSimOnObject(actor, target);
            ISeatPosture seatPosture = actor.Posture as ISeatPosture;
            if (seatPosture != null && seatPosture.Seat != null && seatPosture.Seat.Container != target)
            {
                ClearSeatOccupancyForSimOnObject(actor, seatPosture.Seat.Container);
            }
        }

        private static void ClearSeatOccupancyForSimOnObject(Sim actor, GameObject target)
        {
            if (actor == null || target == null)
            {
                return;
            }

            SitData[] seats = GetSitDataArray(target);
            for (int i = 0; i < seats.Length; i++)
            {
                SitData seat = seats[i];
                if (seat != null && seat.ContainedSim == actor)
                {
                    seat.SetContainedSim(null, target.PartComponent);
                }
            }

            while (target.ActorsUsingMe != null && target.ActorsUsingMe.Contains(actor))
            {
                target.RemoveFromUseList(actor);
            }
        }

        private static void HandleApplyInteractionQueue(BridgeCommand command)
        {
            if (command.Parts.Length < 9)
            {
                throw new InvalidOperationException("APPLY_INTERACTION_QUEUE requires actor, key, target, position flag, and coordinates.");
            }

            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "APPLY_INTERACTION_QUEUE rejected: role is '" + sCurrentRole + "' (must be 'client'). Send SET_ROLE first."));
                return;
            }

            ulong actorObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            string interactionKey = BridgeCodec.DecodeText(command.Parts[3]);
            string interactionParameters = command.Parts.Length >= 10
                ? BridgeCodec.DecodeText(command.Parts[9])
                : string.Empty;
            TryApplyInteractionFields(
                actorObjectId,
                interactionKey,
                command.Parts[4],
                command.Parts[5],
                command.Parts[6],
                command.Parts[7],
                command.Parts[8],
                true,
                command.CorrelationId,
                interactionParameters);
        }

        // T10: synthesize a UserDirected GoHere that travels the REAL P4.4 capture
        // interceptor instead of the server-authoritative queue path — the autonomous stand-in
        // for a human terrain click in the client window (kills ut.py's input() gate).
        //
        // Wire: INJECT_CLICK|<corr>|<simId>|<x>|<y>|<z>  (client role only; admin line protocol).
        //
        // This is the INVERSE of HandleApplyInteractionQueue: that path is the echo coming back
        // from the server and MUST suppress the interceptor (guard set, instance pre-registered);
        // here we WANT the interceptor to fire, so the guard stays clear and the instance is not
        // pre-registered. AddNext raises QueueChanged synchronously → OnActiveActorQueueChanged →
        // InterceptClientInteraction deletes it and emits EventIntentCaptured, exactly as a real
        // click does. From there the existing capture→forward→queue→echo chain is unchanged.
        private static void HandleInjectClick(BridgeCommand command)
        {
            if (command.Parts.Length < 6)
            {
                throw new InvalidOperationException("INJECT_CLICK requires a Sim object id and x/y/z target.");
            }

            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "INJECT_CLICK rejected: role is '" + sCurrentRole + "' (must be 'client'). This command synthesizes a client-side player click."));
                return;
            }

            ulong actorObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            Sim actor = FindSim(actorObjectId);
            if (actor == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + actorObjectId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            // The interceptor only watches Sim.ActiveActor's queue (sTrackedActiveActor). Select
            // the target and re-arm the QueueChanged subscription so our AddNext is observed.
            if (!SelectSim(actor, SelectCause.Player))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "PlumbBob.ForceSelectActor returned false."));
                return;
            }
            PollClientInputInterception();

            RepairOrphanedSocializingPosture(actor);

            float x = float.Parse(command.Parts[3], CultureInfo.InvariantCulture);
            float y = float.Parse(command.Parts[4], CultureInfo.InvariantCulture);
            float z = float.Parse(command.Parts[5], CultureInfo.InvariantCulture);
            Vector3 destination = SnapDestinationToTerrain(new Vector3(x, y, z));
            InteractionPriority priority = new InteractionPriority(InteractionPriorityLevel.UserDirected);
            InteractionDefinition definition = Terrain.GoHere.GetSingleton(actor, destination);
            Terrain.GoHere goHere = definition.CreateInstanceWithCallbacks(
                Terrain.Singleton,
                actor,
                priority,
                false,
                true,
                new Callback(OnInteractionStarted),
                new Callback(OnInteractionCompleted),
                new Callback(OnInteractionFailed)) as Terrain.GoHere;

            if (goHere == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Terrain.GoHere did not create an interaction instance."));
                return;
            }

            goHere.SetDestination(destination, false);

            // The interceptor recovers the click target by reflecting Hit.mPoint (see
            // InterceptClientInteraction); a programmatic GoHere leaves Hit null, so it would
            // fall back to Target.Position (the Terrain singleton) and forward positionFlag=0,
            // which the server rejects. Stamp Hit.mPoint with the destination so the synthesized
            // intent carries the real target, identical to a human click. The unconditional
            // reassign at the end covers Hit being a value type (we mutate a boxed copy).
            try
            {
                System.Reflection.FieldInfo hitField = goHere.GetType().GetField("Hit");
                if (hitField != null)
                {
                    object hit = hitField.GetValue(goHere);
                    if (hit == null)
                    {
                        hit = Activator.CreateInstance(hitField.FieldType);
                    }
                    System.Reflection.FieldInfo pointField = hit.GetType().GetField("mPoint");
                    if (pointField != null)
                    {
                        pointField.SetValue(hit, destination);
                        hitField.SetValue(goHere, hit);
                    }
                }
            }
            catch (Exception ex)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "INJECT_CLICK could not stamp Hit.mPoint: " + ex.Message));
                return;
            }

            // Guard CLEAR (inverse of the echo path): let the synchronous QueueChanged fire and
            // run InterceptClientInteraction. Do NOT pre-register in sClientQueueKnownInteractions.
            bool added = actor.InteractionQueue.AddNext(goHere);
            if (!added)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Terrain.GoHere could not be queued."));
                return;
            }

            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // Step 2: ReadBookChooser or GetBook decision injection (client role).
        // Wire: INJECT_READ_INTENT|<corr>|<simId>|<bookObjectId>|<hexMode>|[hexParams]  mode=chooser|getbook
        private static void HandleInjectReadIntent(BridgeCommand command)
        {
            if (command.Parts.Length < 5)
            {
                throw new InvalidOperationException("INJECT_READ_INTENT requires a Sim object id, book object id, and mode.");
            }

            if (sCurrentRole != "client")
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "INJECT_READ_INTENT rejected: role is '" + sCurrentRole + "' (must be 'client')."));
                return;
            }

            ulong actorObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            ulong bookObjectId = ulong.Parse(command.Parts[3], CultureInfo.InvariantCulture);
            string mode = BridgeCodec.DecodeText(command.Parts[4]);
            string intentParameters = command.Parts.Length >= 6 ? BridgeCodec.DecodeText(command.Parts[5]) : string.Empty;

            Sim actor = FindSim(actorObjectId);
            if (actor == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Sim not found: " + actorObjectId.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            if (!SelectSim(actor, SelectCause.Replay))
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "PlumbBob.ForceSelectActor returned false."));
                return;
            }

            RepairOrphanedSocializingPosture(actor);

            string resolveBookKey = "Sims3.Gameplay.Objects.ReadBook+Definition";
            InteractionDefinition definition = null;
            if (mode == "getbook")
            {
                resolveBookKey = "Sims3.Gameplay.Objects.GetBook+Definition";
                definition = ResolveInteractionDefinitionSingleton("Sims3.Gameplay.Objects.GetBook");
            }
            else if (mode == "chooser")
            {
                definition = ResolveInteractionDefinitionSingleton("Sims3.Gameplay.Objects.ReadBookChooser");
            }
            else
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "INJECT_READ_INTENT mode must be chooser or getbook."));
                return;
            }

            Sims3.Gameplay.Abstracts.GameObject bookTarget = TryResolveTerminalReplayTarget(
                actor,
                resolveBookKey,
                bookObjectId,
                intentParameters) as Sims3.Gameplay.Abstracts.GameObject;
            if (bookTarget == null)
            {
                string bookIdHint = string.Empty;
                string bookIdValue;
                if (TryGetInteractionParameterValue(intentParameters, "bookId", out bookIdValue)
                    && bookIdValue != null
                    && bookIdValue.Length > 0)
                {
                    bookIdHint = " bookId=" + bookIdValue;
                }

                SendLine(BridgeCodec.Error(
                    command.CorrelationId,
                    "Book target not found: bookObjectId="
                        + bookObjectId.ToString(CultureInfo.InvariantCulture)
                        + bookIdHint));
                return;
            }

            if (definition == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "Could not resolve interaction definition for mode " + mode + "."));
                return;
            }

            InteractionPriority priority = new InteractionPriority(InteractionPriorityLevel.UserDirected);
            InteractionInstance instance = definition.CreateInstance(
                bookTarget,
                actor,
                priority,
                false,
                true) as InteractionInstance;

            if (instance == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "INJECT_READ_INTENT did not create an interaction instance."));
                return;
            }

            // pin the seat. Without a pre-seated Sim, the chooser's ReadBook drives a
            // posture transition whose Sit gets a zero hit point, so the engine
            // (Sit.GetContainmentSlotClosestToHit) seats the Sim at the slot nearest the *client*
            // Sim -- diverging from the host's seat. When the inject carries the host Sit's seat,
            // route the Sim into that exact seat first via a native Sit aimed at the slot's world
            // position, then run the chooser as a continuation so ReadBook's Sitting precondition is
            // already satisfied in the host's seat. This stays within decision-injection
            // (the engine routes + sits itself); we only pin which seat the decision targets.
            InteractionInstance seatPin = TryCreateSeatPinnedSit(actor, intentParameters);

            bool added;
            if (seatPin != null)
            {
                sIsProcessingQueueChange = true;
                try
                {
                    added = actor.InteractionQueue.AddNext(seatPin)
                        && actor.InteractionQueue.PushAsContinuation(seatPin, instance, true, false);
                }
                finally
                {
                    sIsProcessingQueueChange = false;
                }
            }
            else
            {
                added = actor.InteractionQueue.AddNext(instance);
            }

            if (!added)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "INJECT_READ_INTENT could not queue interaction."));
                return;
            }

            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // build a native Sit aimed at the host slot's world position so the client engine
        // routes the Sim into the SAME seat the host used. Returns null when the inject carries no
        // seat (legacy chooser-only path) or when the slot point cannot be resolved -- an unaimed
        // Sit would only reproduce the divergence, so in that case we leave the chooser untouched.
        private static InteractionInstance TryCreateSeatPinnedSit(Sim actor, string intentParameters)
        {
            if (actor == null)
            {
                return null;
            }

            ulong seatObjectId;
            if (!ulong.TryParse(
                    ReadParameter(intentParameters, "seatObjectId"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out seatObjectId)
                || seatObjectId == 0uL)
            {
                return null;
            }

            GameObject seatTarget = new ObjectGuid(seatObjectId).ObjectFromId<GameObject>();
            if (seatTarget == null)
            {
                return null;
            }

            Vector3 seatPoint;
            if (!TryResolveSeatPoint(seatTarget, intentParameters, out seatPoint))
            {
                return null;
            }

            InteractionDefinition sitDef = ResolveTerminalInteractionDefinition(
                "Sims3.Gameplay.InteractionsShared.Sit+Definition",
                actor,
                seatTarget);
            if (sitDef == null)
            {
                sitDef = Sit.Singleton;
            }

            InteractionInstance sit = sitDef.CreateInstanceWithCallbacks(
                seatTarget,
                actor,
                new InteractionPriority(InteractionPriorityLevel.UserDirected),
                false,
                true,
                new Callback(OnInteractionStarted),
                new Callback(OnInteractionCompleted),
                new Callback(OnInteractionFailed));
            if (sit == null)
            {
                return null;
            }

            // Sit.Init already ran during creation with a zero hit; override the private Point field
            // so Sit.GetContainmentSlotClosestToHit selects the host slot instead of Actor.Position.
            SetPrivateVector3Field(sit, "Point", seatPoint);
            return sit;
        }

        private static InteractionDefinition ResolveInteractionDefinitionSingleton(string typeFullName)
        {
            Type definitionType = typeof(Book).Assembly.GetType(typeFullName);
            if (definitionType == null)
            {
                return null;
            }

            System.Reflection.FieldInfo field = definitionType.GetField(
                "Singleton",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field == null)
            {
                return null;
            }

            object value = field.GetValue(null);
            if (value is InteractionDefinition)
            {
                return (InteractionDefinition)value;
            }

            return null;
        }

        // M4 P4.1.1.2: look up a household by its HouseholdId by iterating
        // Household.sHouseholdList. Mirrors the FindSim linear-scan pattern.
        private static Household FindHousehold(ulong householdId)
        {
            foreach (Household candidate in Household.sHouseholdList)
            {
                if (candidate != null && candidate.HouseholdId == householdId)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void HandleProbeStateInjection(BridgeCommand command)
        {
            if (command.Parts.Length < 6)
            {
                throw new InvalidOperationException("PROBE_STATE_INJECTION requires a Sim object id and x/y/z target.");
            }

            ulong simObjectId = ulong.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            Sim sim = FindSim(simObjectId);
            if (sim == null)
            {
                SendLine(BridgeCodec.StateInjectionProbe(
                    command.CorrelationId,
                    "none",
                    "sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) + ";error=not-found"));
                return;
            }

            float x = float.Parse(command.Parts[3], CultureInfo.InvariantCulture);
            float y = float.Parse(command.Parts[4], CultureInfo.InvariantCulture);
            float z = float.Parse(command.Parts[5], CultureInfo.InvariantCulture);
            Vector3 target = new Vector3(x, y, z);
            Vector3 before = sim.Position;
            ulong beforeInteraction = CurrentInteractionId(sim);

            sim.SetPosition(target);

            Vector3 after = sim.Position;
            ulong afterInteraction = CurrentInteractionId(sim);
            float distance = Distance(after, target);
            string report = "method=Sim.SetPosition" +
                ";sim=" + simObjectId.ToString(CultureInfo.InvariantCulture) +
                ";before=" + Vector(before) +
                ";target=" + Vector(target) +
                ";after=" + Vector(after) +
                ";beforeInteraction=" + beforeInteraction.ToString(CultureInfo.InvariantCulture) +
                ";afterInteraction=" + afterInteraction.ToString(CultureInfo.InvariantCulture) +
                ";distance=" + Float(distance);

            SendLine(BridgeCodec.StateInjectionProbe(command.CorrelationId, "ok", report));
        }

        private static string BuildSimLifecycleReport()
        {
            SimLifecycleSample current = new SimLifecycleSample("current");
            RecordSimLifecycleSample(current);

            return "api=LotManager.Actors" +
                ";worldLoad=" + sWorldLoadSample.Encode() +
                ";loadingScreenDisposed=" + sLoadingScreenDisposedSample.Encode() +
                ";firstSnapshotRequest=" + sFirstSnapshotRequestSample.Encode() +
                ";current=" + current.Encode();
        }

        private static void RecordFirstSnapshotRequestSample(int snapshotSims)
        {
            if (sFirstSnapshotRequestSample.Seen)
            {
                return;
            }

            RecordSimLifecycleSample(sFirstSnapshotRequestSample, snapshotSims);
        }

        private static void RecordSimLifecycleSample(SimLifecycleSample sample)
        {
            RecordSimLifecycleSample(sample, CountSnapshotSims());
        }

        private static void RecordSimLifecycleSample(SimLifecycleSample sample, int snapshotSims)
        {
            sample.Seen = true;
            sample.Tick = SafeTick();
            sample.Actors = SafeActorCount();
            sample.VisibleActors = SafeVisibleActorCount();
            sample.SnapshotSims = snapshotSims;
        }

        private static int SafeActorCount()
        {
            try
            {
                return CountSimList(LotManager.Actors);
            }
            catch
            {
                return -1;
            }
        }

        private static int SafeVisibleActorCount()
        {
            try
            {
                return CountSimList(Sim.VisibleActors);
            }
            catch
            {
                return -1;
            }
        }

        private static int CountSimList(IList<Sim> sims)
        {
            if (sims == null)
            {
                return -1;
            }

            int count = 0;
            foreach (Sim sim in sims)
            {
                if (sim != null)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountSnapshotSims()
        {
            IList<Sim> actors = LotManager.Actors;
            if (actors == null)
            {
                return -1;
            }

            int count = 0;
            foreach (Sim sim in actors)
            {
                if (sim == null)
                {
                    continue;
                }

                try
                {
                    EncodeSimSnapshot(sim);
                    count++;
                }
                catch
                {
                    SwallowedErrors.Note();
                }
            }

            return count;
        }

        private static string[] SnapshotSims(long sinceTick, long currentTick)
        {
            // P3.3: update each Sim's change tracker first (so subsequent calls see the latest
            // state), then filter out entities unchanged since sinceTick when sinceTick >= 0.
            List<string> items = new List<string>();
            foreach (Sim sim in LotManager.Actors)
            {
                if (sim == null)
                {
                    continue;
                }

                try
                {
                    long changedTick = UpdateSimTracker(sim, currentTick);
                    if (sinceTick >= 0L && changedTick <= sinceTick)
                    {
                        continue;
                    }

                    items.Add(EncodeSimSnapshot(sim));
                }
                catch
                {
                    SwallowedErrors.Note();
                }
            }

            return items.ToArray();
        }

        private static string[] SnapshotObjects(long sinceTick, long currentTick)
        {
            List<string> items = new List<string>();
            GameObject[] objects = Sims3.Gameplay.Queries.GetObjects<GameObject>();
            if (objects == null)
            {
                return items.ToArray();
            }

            for (int i = 0; i < objects.Length; i++)
            {
                GameObject obj = objects[i];
                if (obj == null || obj is Sim)
                {
                    continue;
                }

                try
                {
                    long changedTick = UpdateObjectTracker(obj, currentTick);
                    if (sinceTick >= 0L && changedTick <= sinceTick)
                    {
                        continue;
                    }

                    items.Add(EncodeObjectSnapshot(obj));
                }
                catch
                {
                    SwallowedErrors.Note();
                }
            }

            return items.ToArray();
        }

        private static string EncodeSimSnapshot(Sim sim)
        {
            Vector3 position = sim.Position;
            ulong householdId = 0uL;
            Household household = sim.Household;
            if (household != null)
            {
                householdId = household.HouseholdId;
            }

            string currentInteractionId = "-";
            ulong interactionId = CurrentInteractionId(sim);
            if (interactionId != 0uL)
            {
                currentInteractionId = interactionId.ToString(CultureInfo.InvariantCulture);
            }

            string currentInteractionKey = "-";
            string interactionKey = CurrentInteractionKey(sim);
            if (interactionKey != null && interactionKey.Length > 0)
            {
                currentInteractionKey = BridgeCodec.EncodeText(interactionKey);
            }

            string lotId = "-";
            Lot simLot = sim.LotCurrent;
            if (simLot != null)
            {
                lotId = simLot.LotId.ToString(CultureInfo.InvariantCulture);
            }

            string queueCount = "-";
            if (sim.InteractionQueue != null)
            {
                queueCount = sim.InteractionQueue.Count.ToString(CultureInfo.InvariantCulture);
            }

            return WireSimObjectId(sim).ToString(CultureInfo.InvariantCulture) + ":" +
                BridgeCodec.EncodeText(SafeString(sim.FullName)) + ":" +
                householdId.ToString(CultureInfo.InvariantCulture) + ":" +
                Float(position.x) + ":" +
                Float(position.y) + ":" +
                Float(position.z) + ":" +
                currentInteractionId + ":" +
                currentInteractionKey + ":-:" +
                lotId + ":" +
                queueCount + ":" +
                (sim.SimDescription != null ? sim.SimDescription.SimDescriptionId.ToString(CultureInfo.InvariantCulture) : "-");
        }

        private static string EncodeObjectSnapshot(GameObject obj)
        {
            Vector3 position = obj.Position;
            string lotId = "-";
            Lot lot = obj.LotCurrent;
            if (lot != null)
            {
                lotId = lot.LotId.ToString(CultureInfo.InvariantCulture);
            }

            return obj.ObjectId.Value.ToString(CultureInfo.InvariantCulture) + ":" +
                BridgeCodec.EncodeText(obj.GetType().FullName) + ":" +
                Float(position.x) + ":" +
                Float(position.y) + ":" +
                Float(position.z) + ":" +
                lotId;
        }

        private static long UpdateSimTracker(Sim sim, long currentTick)
        {
            ulong id = sim.ObjectId.Value;
            Vector3 pos = sim.Position;
            ulong interactionId = CurrentInteractionId(sim);
            SimChangeTracker tracker;
            if (sSimTrackers.TryGetValue(id, out tracker))
            {
                if (!VectorEquals(tracker.LastPosition, pos) || tracker.LastInteractionId != interactionId)
                {
                    tracker.LastPosition = pos;
                    tracker.LastInteractionId = interactionId;
                    tracker.LastChangedTick = currentTick;
                }
            }
            else
            {
                tracker = new SimChangeTracker();
                tracker.LastPosition = pos;
                tracker.LastInteractionId = interactionId;
                tracker.LastChangedTick = currentTick;
                sSimTrackers[id] = tracker;
            }

            return tracker.LastChangedTick;
        }

        private static long UpdateObjectTracker(GameObject obj, long currentTick)
        {
            ulong id = obj.ObjectId.Value;
            Vector3 pos = obj.Position;
            ulong lotId = 0uL;
            Lot lot = obj.LotCurrent;
            if (lot != null)
            {
                lotId = lot.LotId;
            }

            ObjectChangeTracker tracker;
            if (sObjectTrackers.TryGetValue(id, out tracker))
            {
                if (!VectorEquals(tracker.LastPosition, pos) || tracker.LastLotId != lotId)
                {
                    tracker.LastPosition = pos;
                    tracker.LastLotId = lotId;
                    tracker.LastChangedTick = currentTick;
                }
            }
            else
            {
                tracker = new ObjectChangeTracker();
                tracker.LastPosition = pos;
                tracker.LastLotId = lotId;
                tracker.LastChangedTick = currentTick;
                sObjectTrackers[id] = tracker;
            }

            return tracker.LastChangedTick;
        }

        private static bool VectorEquals(Vector3 a, Vector3 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z;
        }

        private static void ClearChangeTrackers()
        {
            sSimTrackers.Clear();
            sObjectTrackers.Clear();
        }

        private static void SendChunkedSnapshot(string correlationId, string line)
        {
            if (TrySendBulkSnapshot(correlationId, line))
            {
                return;
            }

            if (line.Length <= MaxChunkPayloadChars
                && Encoding.UTF8.GetByteCount(line) <= BridgeMemoryBuffer.BufferSize - BridgeMemoryBuffer.HeaderSize)
            {
                SendLine(line);
                return;
            }

            int count = (line.Length + MaxChunkPayloadChars - 1) / MaxChunkPayloadChars;
            string[] lines = new string[count];
            for (int i = 0; i < count; i++)
            {
                int start = i * MaxChunkPayloadChars;
                int length = Math.Min(MaxChunkPayloadChars, line.Length - start);
                lines[i] = BridgeCodec.SnapshotChunk(correlationId, i, count, line.Substring(start, length));
            }

            SendLines(lines);
        }

        private static bool TrySendBulkSnapshot(string correlationId, string line)
        {
            if (!BridgeMemoryBuffer.MultiSlotEnabled || line == null || line.Length <= 0)
            {
                return false;
            }

            // Multi-megabyte snapshots stay on SNAPSHOT_CHUNK until bulk encoding uses
            // sub-slot byte steps per pump tick (524 KiB GetBytes per tick still blocks ~25s on mono).
            if (line.Length > BridgeSharedRegionLayout.BulkSlotPayloadMax)
            {
                return false;
            }

            byte[] payload = Encoding.UTF8.GetBytes(line);
            if (payload.Length <= 0 || payload.Length > BridgeSharedRegionLayout.BulkSlotPayloadMax)
            {
                return false;
            }

            if (!BridgeBulkBuffer.HasWritableHostSlots(1))
            {
                return false;
            }

            int slotIndex;
            uint crc32;
            if (!BridgeBulkBuffer.TryPublishHostBulk(correlationId, payload, out slotIndex, out crc32))
            {
                return false;
            }

            SendLine(BridgeCodec.BulkRefSnapshot(correlationId, slotIndex, payload.Length, crc32));
            return true;
        }

        // Phase 2b: publish raw binary bytes (SNAPSHOT2) into bulk slots. Unlike
        // TrySendBulkSnapshot, the payload is already a byte[] — no Encoding.UTF8.GetBytes
        // call that blocks the mono pump for ~25s on a 2 MiB string. Stripes across up to 4
        // bulk slots when the payload exceeds a single slot (512 KiB).
        private static bool TrySendBulkBytes(string correlationId, byte[] payload)
        {
            if (!BridgeMemoryBuffer.MultiSlotEnabled || payload == null || payload.Length <= 0)
            {
                return false;
            }

            if (payload.Length <= BridgeSharedRegionLayout.BulkSlotPayloadMax)
            {
                if (!BridgeBulkBuffer.HasWritableHostSlots(1))
                {
                    return false;
                }

                int slotIndex;
                uint crc32;
                if (!BridgeBulkBuffer.TryPublishHostBulk(correlationId, payload, out slotIndex, out crc32))
                {
                    return false;
                }

                SendLine(BridgeCodec.BulkRefSnapshot(correlationId, slotIndex, payload.Length, crc32));
                return true;
            }

            // Stripe across multiple bulk slots. Publish all slices first, then SendLines once —
            // SendLine in a loop on the pump thread deadlocks outbound drain ( Phase 2a).
            int partCount = (payload.Length + BridgeSharedRegionLayout.BulkSlotPayloadMax - 1) / BridgeSharedRegionLayout.BulkSlotPayloadMax;
            if (partCount > BridgeSharedRegionLayout.BulkSlotCount)
            {
                return false;
            }

            if (!BridgeBulkBuffer.HasWritableHostSlots(partCount))
            {
                return false;
            }

            string[] refLines = new string[partCount];
            for (int partIndex = 0; partIndex < partCount; partIndex++)
            {
                int offset = partIndex * BridgeSharedRegionLayout.BulkSlotPayloadMax;
                int length = Math.Min(BridgeSharedRegionLayout.BulkSlotPayloadMax, payload.Length - offset);

                int slotIndex;
                uint crc32;
                if (!BridgeBulkBuffer.TryPublishHostBulkBytes(correlationId, payload, offset, length, out slotIndex, out crc32))
                {
                    return false;
                }

                refLines[partIndex] = BridgeCodec.BulkRefSnapshotPart(correlationId, slotIndex, partIndex, partCount, payload.Length, crc32);
            }

            SendLines(refLines);
            return true;
        }

        private static string Float(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Vector(Vector3 value)
        {
            return Float(value.x) + "," + Float(value.y) + "," + Float(value.z);
        }

        private static float Distance(Vector3 left, Vector3 right)
        {
            float x = left.x - right.x;
            float y = left.y - right.y;
            float z = left.z - right.z;
            return (float)Math.Sqrt((double)((x * x) + (y * y) + (z * z)));
        }

        private static string SafeString(string value)
        {
            return value == null ? string.Empty : value;
        }

        private static Sim FindSim(ulong objectId)
        {
            // W2 Sim identity: an id the server aliased names a person, not this process's object.
            // Resolve it by description only; an unrelated local object may hold the same id.
            ulong aliasDescriptionId;
            if (sSimAliases.Count > 0 && sSimAliases.TryGetValue(objectId, out aliasDescriptionId))
            {
                return FindSimByDescription(aliasDescriptionId);
            }

            // M4 prep: prefer LotManager.Actors linear scan so any Sim that
            // appears in SnapshotSims is also resolvable here. ObjectGuid.
            // ObjectFromId<Sim>() only resolves Sims currently active on a
            // lot, which silently fails for snapshot-enumerated Sims that
            // are off the active lot (e.g. Bebe Hart in Sunset Valley). The
            // proxy lookup remains as a defensive fallback for any Sim
            // outside the LotManager.Actors enumeration.
            foreach (Sim candidate in LotManager.Actors)
            {
                if (candidate != null && candidate.ObjectId.Value == objectId)
                {
                    return candidate;
                }
            }

            ObjectGuid guid = new ObjectGuid(objectId);
            return guid.ObjectFromId<Sim>();
        }

        // The interaction the autonomy watcher reports for a Sim: prefer the engine's current
        // interaction, then the queue head, then the running interaction. CurrentInteractionId /
        // CurrentInteractionKey both project from this single resolution so the id, key, and
        // route destination all describe the SAME instance in one poll tick.
        private static InteractionInstance CurrentInteractionInstance(Sim sim)
        {
            if (sim == null || sim.InteractionQueue == null)
            {
                return null;
            }

            InteractionInstance interaction = sim.CurrentInteraction;
            if (interaction == null)
            {
                interaction = sim.InteractionQueue.GetHeadInteraction();
            }

            if (interaction == null)
            {
                interaction = sim.InteractionQueue.RunningInteraction;
            }

            return interaction;
        }

        private static ulong CurrentInteractionId(Sim sim)
        {
            InteractionInstance interaction = CurrentInteractionInstance(sim);
            return interaction == null ? 0uL : interaction.Id;
        }

        private static string CurrentInteractionKey(Sim sim)
        {
            InteractionInstance interaction = CurrentInteractionInstance(sim);
            if (interaction == null || interaction.InteractionDefinition == null)
            {
                return string.Empty;
            }

            return interaction.InteractionDefinition.GetType().FullName;
        }

        // does the Sim's current interaction target a world object? Stationary autonomous
        // interactions with a target (cook at stove, read a book, eat a plate, sit on a chair) carry
        // the object id + its position on the emit so the client puppet can walk-to-target. Wrapped
        // so a getter throwing mid-simulation cannot kill the pump; on any failure it answers FALSE
        // (the safe direction -- a missed stationary emit only leaves a puppet positionally correct
        // via the snapshot stream, whereas a wrong emit floods the bridge with an unreplayable line).
        private static bool CurrentInteractionHasTarget(Sim sim)
        {
            if (sim == null)
            {
                return false;
            }

            try
            {
                InteractionInstance interaction = CurrentInteractionInstance(sim);
                return interaction != null && interaction.Target != null;
            }
            catch
            {
                return false;
            }
        }

        // build the InteractionQueued wire line for a watched Sim's newly-changed
        // autonomous interaction. ROUTING interactions (GoHome / VisitCommunityLot /
        // FollowerMeetUpAtPoint / LeaderGoHereWith, per RoutingInteractionFilter) carry their route
        // destination so the mirror client can replay them as a GoHere-to-destination and the Sim
        // WALKS instead of teleport-sliding on the position stream. follow-up:
        // NON-ROUTING interactions that target a world object (GetBook, VisitObject, cook at a
        // stove, sit on a chair) also carry the target id + its position so the client puppet
        // walk-to-target replays them (step B below). Only interactions with NO target object keep
        // the no-target 3-arg emit (the translator drops them -- nothing to replay; the snapshot
        // stream carries position).
        //
        // Fail-closed: a routing interaction whose GetTargetPosition() is not a finite, in-world
        // point (Vector3.Invalid = +Inf, Vector3.OutOfWorld = -20000, or the route not yet planned)
        // falls back to the no-target emit rather than fabricating a destination.
        private static string BuildInteractionQueuedLine(Sim sim, ulong simObjectId, ulong interactionId)
        {
            return BuildInteractionQueuedLineForInstance(simObjectId, CurrentInteractionInstance(sim));
        }

        private static string BuildInteractionQueuedLineForInstance(ulong simObjectId, InteractionInstance interaction)
        {
            string interactionKey = (interaction == null || interaction.InteractionDefinition == null)
                ? string.Empty
                : interaction.InteractionDefinition.GetType().FullName;
            ulong interactionId = interaction == null ? 0uL : interaction.Id;

            if (interaction != null && RoutingInteractionFilter.IsRoutingInteraction(interactionKey))
            {
                Vector3 destination = interaction.GetTargetPosition();
                if (IsFiniteInWorldPosition(destination))
                {
                    ulong targetId = 0uL;
                    if (interaction.Target != null)
                    {
                        targetId = interaction.Target.ObjectId.Value;
                    }

                    return BridgeCodec.EventInteractionQueued(
                        simObjectId,
                        interactionId,
                        interactionKey,
                        targetId.ToString(CultureInfo.InvariantCulture),
                        "1",
                        Float(destination.x),
                        Float(destination.y),
                        Float(destination.z));
                }
            }

            // follow-up: non-routing autonomous interactions (GetBook, VisitObject, …) often
            // carry a target object. Emit its world position when readable so the mirror client can
            // walk-to-target via the existing GoHere apply path; when only the target id is known,
            // emit hasPosition=0 so the client resolves GameObject.Position at apply time.
            if (interaction != null && interaction.Target != null)
            {
                ulong targetId = interaction.Target.ObjectId.Value;
                Vector3 destination = interaction.GetTargetPosition();
                if (!IsFiniteInWorldPosition(destination))
                {
                    destination = interaction.Target.Position;
                }
                if (IsFiniteInWorldPosition(destination))
                {
                    return BridgeCodec.EventInteractionQueued(
                        simObjectId,
                        interactionId,
                        interactionKey,
                        targetId.ToString(CultureInfo.InvariantCulture),
                        "1",
                        Float(destination.x),
                        Float(destination.y),
                        Float(destination.z));
                }
                if (targetId != 0uL)
                {
                    return BridgeCodec.EventInteractionQueued(
                        simObjectId,
                        interactionId,
                        interactionKey,
                        targetId.ToString(CultureInfo.InvariantCulture),
                        "0",
                        "0",
                        "0",
                        "0");
                }
            }

            return BridgeCodec.EventInteractionQueued(simObjectId, interactionId, interactionKey);
        }

        // reject the engine's no-destination sentinels (Vector3.Invalid = +Inf on every
        // axis; Vector3.OutOfWorld = -20000 on every axis) and any NaN, so a routing interaction
        // with no planned route falls back to the no-target emit instead of shipping a garbage
        // GoHere endpoint. The +/-100000 bound is well outside any real Sims 3 world extent.
        private static bool IsFiniteInWorldPosition(Vector3 position)
        {
            if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z))
            {
                return false;
            }

            if (float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
            {
                return false;
            }

            if (position.x <= -100000f || position.y <= -100000f || position.z <= -100000f)
            {
                return false;
            }

            if (position.x >= 100000f || position.y >= 100000f || position.z >= 100000f)
            {
                return false;
            }

            return true;
        }

        private static bool IsInteractionUserDirected(InteractionInstance interaction)
        {
            if (interaction == null)
            {
                return false;
            }

            try
            {
                InteractionPriority priority = interaction.GetPriority();
                return priority.Level == InteractionPriorityLevel.UserDirected;
            }
            catch
            {
                return true;
            }
        }

        private static InteractionInstance TryGetTransitionInteraction(Sim sim)
        {
            if (sim == null || sim.InteractionQueue == null)
            {
                return null;
            }

            try
            {
                return sim.InteractionQueue.TransitionInteraction;
            }
            catch
            {
                return null;
            }
        }

        private static void SeedClaimedTransitionSitBaseline(Sim sim, ulong simObjectId)
        {
            InteractionInstance transition = TryGetTransitionInteraction(sim);
            if (transition != null
                && transition.InteractionDefinition != null
                && SitTransitionEmitFilter.IsSitTransitionReplay(transition.InteractionDefinition.GetType().FullName))
            {
                sClaimedTransitionSitLastEmittedIds[simObjectId] = transition.Id;
            }
            else
            {
                sClaimedTransitionSitLastEmittedIds[simObjectId] = 0uL;
            }
        }

        // seat binding: Sit often runs as TransitionInteraction while ReadBook is the queue
        // head, so CurrentInteractionId never changes to Sit and the client picks the wrong chair.
        private static InteractionInstance TryTakeAutonomousTransitionSit(Sim sim, ulong simObjectId)
        {
            InteractionInstance transition = TryGetTransitionInteraction(sim);
            if (transition == null || transition.InteractionDefinition == null)
            {
                return null;
            }

            string interactionKey = transition.InteractionDefinition.GetType().FullName;
            if (!SitTransitionEmitFilter.IsSitTransitionReplay(interactionKey))
            {
                return null;
            }

            if (IsInteractionUserDirected(transition))
            {
                return null;
            }

            if (transition.Target == null)
            {
                return null;
            }

            ulong sitId = transition.Id;
            ulong lastEmitted = 0uL;
            if (sClaimedTransitionSitLastEmittedIds.TryGetValue(simObjectId, out lastEmitted)
                && !SitTransitionEmitFilter.ShouldEmitTransitionSit(lastEmitted, sitId))
            {
                return null;
            }

            sClaimedTransitionSitLastEmittedIds[simObjectId] = sitId;
            return transition;
        }

        private static void TryEmitClaimedTransitionSit(Sim sim, ulong simObjectId)
        {
            InteractionInstance transition = TryTakeAutonomousTransitionSit(sim, simObjectId);
            if (transition == null)
            {
                return;
            }

            SendInteractionReplayQueuedEvent(sim, simObjectId, transition);
        }

        // legacy DISABLE_AUTONOMY watch emits InteractionQueued (not replay bundles) for
        // queue-head changes, but book-chain Sit still runs as TransitionInteraction. Mirror the
        // claimed-Sim transition emit here so GetBook+Sit+ReadBook batches complete on the client.
        private static void TryEmitAutonomyWatchTransitionSit(Sim sim, ulong simObjectId)
        {
            InteractionInstance transition = TryTakeAutonomousTransitionSit(sim, simObjectId);
            if (transition == null)
            {
                return;
            }

            SendLine(BuildInteractionQueuedLineForInstance(simObjectId, transition));
        }

        private static void PollAutonomyWatchQueues()
        {
            if (sAutonomyWatchLastInteractionIds.Count == 0)
            {
                return;
            }

            List<ulong> keys = new List<ulong>(sAutonomyWatchLastInteractionIds.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                ulong simObjectId = keys[i];
                Sim sim = FindSim(simObjectId);
                if (sim == null)
                {
                    sAutonomyWatchLastInteractionIds.Remove(simObjectId);
                    sClaimedTransitionSitLastEmittedIds.Remove(simObjectId);
                    continue;
                }

                TryEmitAutonomyWatchTransitionSit(sim, simObjectId);

                ulong interactionId = CurrentInteractionId(sim);
                ulong previousId = sAutonomyWatchLastInteractionIds[simObjectId];
                if (interactionId != 0uL && interactionId != previousId)
                {
                    sAutonomyWatchLastInteractionIds[simObjectId] = interactionId;
                    // routing interactions carry their destination (8-arg) so the mirror
                    // walks; everything else keeps the no-target 3-arg emit (translator drops it).
                    SendLine(BuildInteractionQueuedLine(sim, simObjectId, interactionId));
                }
            }
        }

        // (pure-puppet Phase 1): emit a CLAIMED Sim's AUTONOMOUS interaction changes to the
        // mirror client, decoupled from the legacy autonomy-disabled watch set. PollAutonomyWatchQueues
        // only covers sAutonomyWatchLastInteractionIds (the superseded DISABLE_AUTONOMY path); under
        // a claimed Sim is tracked in sCommandGate WITH free will, so the host never emitted its
        // host-run autonomous interactions and the client puppet diverged (converged by position,
        // ran a different interaction). This poll closes that gap so the puppet replays what the host runs.
        //
        // EMITS ONLY AUTONOMOUS interactions. A UserDirected interaction at the queue head is a player
        // command, which the authoritative session already broadcasts to clients as the echo --
        // emitting it here too would double-replay it on the client (a second AddNext past the
        // anti-recapture guard). The last-seen id is still advanced for a UserDirected head so the NEXT
        // autonomous interaction (after the command drains) is detected as a change and emitted.
        //
        // Routing interactions carry their destination (BuildInteractionQueuedLine, 8-arg) so the client
        // replays them as a GoHere and the puppet WALKS; a stationary autonomous interaction with a
        // target object carries the target id + position ( step B) so the puppet
        // walk-to-targets it. A stationary interaction with no target emits the no-target 3-arg line
        // (the translator drops it -- routing/target-first scope, animation deferred for the no-target
        // registry slice).
        private static void PollClaimedSimInteractionEmit()
        {
            if (sCommandGate.TrackedCount == 0)
            {
                if (sClaimedInteractionLastIds.Count > 0)
                {
                    sClaimedInteractionLastIds.Clear();
                }
                if (sClaimedTransitionSitLastEmittedIds.Count > 0)
                {
                    sClaimedTransitionSitLastEmittedIds.Clear();
                }
                return;
            }

            List<ulong> keys = sCommandGate.TrackedSims();

            // Prune last-seen entries for Sims no longer claimed (released / left) so the dict cannot
            // grow unbounded and a re-claim re-seeds cleanly.
            if (sClaimedInteractionLastIds.Count > 0)
            {
                List<ulong> stale = null;
                foreach (ulong tracked in sClaimedInteractionLastIds.Keys)
                {
                    if (!keys.Contains(tracked))
                    {
                        if (stale == null)
                        {
                            stale = new List<ulong>();
                        }
                        stale.Add(tracked);
                    }
                }
                if (stale != null)
                {
                    for (int s = 0; s < stale.Count; s++)
                    {
                        sClaimedInteractionLastIds.Remove(stale[s]);
                        sClaimedTransitionSitLastEmittedIds.Remove(stale[s]);
                    }
                }
            }

            for (int i = 0; i < keys.Count; i++)
            {
                ulong simObjectId = keys[i];

                // Disjoint-by-design from the legacy autonomy-watch set, but guard defensively so a Sim
                // that is somehow in both is never emitted twice in one tick.
                if (sAutonomyWatchLastInteractionIds.ContainsKey(simObjectId))
                {
                    continue;
                }

                Sim sim = FindSim(simObjectId);
                if (sim == null)
                {
                    // Transient resolution miss (off-lot / mid-load) is NOT proof of non-existence
                    // (interactions.md Bebe Hart class). Keep the last-seen entry; the authoritative
                    // removal is the controller's UNGATE_AUTONOMY on release/leave/disconnect.
                    continue;
                }

                ulong interactionId = CurrentInteractionId(sim);

                bool seeded = sClaimedInteractionLastIds.ContainsKey(simObjectId);
                if (!seeded)
                {
                    // First sight after claim: seed the baseline WITHOUT emitting, mirroring the
                    // PollAutonomyWatchQueues / HandleDisableAutonomy seed-then-emit-on-change discipline.
                    sClaimedInteractionLastIds[simObjectId] = interactionId;
                    SeedClaimedTransitionSitBaseline(sim, simObjectId);
                    continue;
                }

                TryEmitClaimedTransitionSit(sim, simObjectId);

                ulong previousId = sClaimedInteractionLastIds[simObjectId];
                if (interactionId == 0uL || interactionId == previousId)
                {
                    continue;
                }

                // The interaction changed -- always advance the baseline so it is not re-detected.
                sClaimedInteractionLastIds[simObjectId] = interactionId;

                // Skip player-directed orders: the authoritative session emits those ( echo);
                // re-emitting here would double-replay on the client. Only host-chosen AUTONOMOUS
                // interactions need this decoupled emit.
                if (IsCurrentInteractionUserDirected(sim))
                {
                    continue;
                }

                SendInteractionReplayQueuedEvent(sim, simObjectId, CurrentInteractionInstance(sim));
            }
        }

        // (pure-puppet Phase 2): emit an UNCLAIMED tier-1 townie's AUTONOMOUS ROUTING
        // interaction to the mirror so the puppet WALKS the route instead of teleport-sliding on the
        // position stream. This is the THIRD, disjoint host emit:
        //   * PollAutonomyWatchQueues       -> legacy DISABLE_AUTONOMY watch set
        //   * PollClaimedSimInteractionEmit -> CLAIMED Sims ( command gate)
        //   * PollUnclaimedRoutingEmit      -> UNCLAIMED Sims on a tier-1 (claimed) lot   <-- this
        // Neither of the first two covers an unclaimed townie, so its host-run routes were never
        // mirrored -- the Phase 2 autonomous-townie case ( was only unit-proven). Scope +
        // routing-only gating keep this off the single-frame bridge floor: only Sims on a lot
        // some player can see, only routing interactions (which carry a replayable destination).
        // UnclaimedRoutingEmitFilter.ShouldEmit owns the pure membership decision; this method owns the
        // engine side effects.
        //
        // SAFE vs the autonomy churn area: the client already SUPPRESSES a mirrored (unclaimed) townie's
        // local AI on first snapshot sight (MirrorEngine.OnMirroredSnapshotSims), so a replayed host
        // route does NOT fight a local-AI route. The controller re-filters per client
        // (MirroringInterestScope.ShouldMirrorInteractionQueued, keyed on the same claimed-lot set), so a
        // client with no claim on this lot still never receives it -- the host scope here only spares the
        // bridge, it is not the authority on per-client visibility.
        private static void PollUnclaimedRoutingEmit()
        {
            // No claims -> no interest lots -> nothing to emit (mirror is in full-world bootstrap anyway).
            // Clear stale last-emitted records so a fresh claim re-seeds cleanly.
            if (sCommandGate.TrackedCount == 0)
            {
                if (sUnclaimedRoutingLastEmittedIds.Count > 0)
                {
                    sUnclaimedRoutingLastEmittedIds.Clear();
                }
                return;
            }

            // Pass 1: the tier-1 interest-lot set = lots that currently host a CLAIMED Sim. Resolved in a
            // single LotManager.Actors scan (no per-claim FindSim re-scan).
            List<ulong> interestLots = new List<ulong>();
            foreach (Sim candidate in LotManager.Actors)
            {
                if (candidate == null)
                {
                    continue;
                }
                if (!sCommandGate.IsTracked(candidate.ObjectId.Value))
                {
                    continue;
                }
                Lot claimedLot = candidate.LotCurrent;
                if (claimedLot != null)
                {
                    ulong claimedLotId = claimedLot.LotId;
                    if (claimedLotId != 0uL && !interestLots.Contains(claimedLotId))
                    {
                        interestLots.Add(claimedLotId);
                    }
                }
            }

            // No resolvable claimed lot this tick (claimed Sims mid-load / off-lot): emit nothing, but do
            // NOT clear the dedupe records -- a transient resolution miss is not proof of anything.
            if (interestLots.Count == 0)
            {
                return;
            }

            // Pass 2: emit each eligible unclaimed townie's newly-seen autonomous routing interaction.
            foreach (Sim sim in LotManager.Actors)
            {
                if (sim == null)
                {
                    continue;
                }

                ulong simObjectId = sim.ObjectId.Value;
                bool isClaimed = sCommandGate.IsTracked(simObjectId);
                bool isLegacyWatched = sAutonomyWatchLastInteractionIds.ContainsKey(simObjectId);

                // A Sim now covered by another emit must not also carry a stale unclaimed record.
                if (isClaimed || isLegacyWatched)
                {
                    if (sUnclaimedRoutingLastEmittedIds.ContainsKey(simObjectId))
                    {
                        sUnclaimedRoutingLastEmittedIds.Remove(simObjectId);
                    }
                    continue;
                }

                bool onInterestLot = false;
                Lot simLot = sim.LotCurrent;
                if (simLot != null)
                {
                    ulong simLotId = simLot.LotId;
                    onInterestLot = simLotId != 0uL && interestLots.Contains(simLotId);
                }

                string interactionKey = CurrentInteractionKey(sim);
                bool isRouting = RoutingInteractionFilter.IsRoutingInteraction(interactionKey);
                bool hasTargetObject = CurrentInteractionHasTarget(sim);

                if (!UnclaimedRoutingEmitFilter.ShouldEmit(onInterestLot, isClaimed, isLegacyWatched, isRouting, hasTargetObject))
                {
                    continue;
                }

                // Emit once per distinct interaction id (dedupe-by-emitted-id): the FIRST sight of
                // a townie's new interaction id IS the event, unlike the claimed poll's seed-then-change
                // (an unclaimed Sim is only watched WHILE acting on an interest lot, so seeding would
                // miss its current activity).
                ulong interactionId = CurrentInteractionId(sim);
                if (interactionId == 0uL)
                {
                    continue;
                }

                ulong lastEmitted;
                if (sUnclaimedRoutingLastEmittedIds.TryGetValue(simObjectId, out lastEmitted)
                    && lastEmitted == interactionId)
                {
                    continue;
                }

                // Defensive: never replay a UserDirected head through the autonomous path. An unclaimed
                // Sim should not have one, but the engine queues UserDirected interactions the player
                // never clicked; a directed order is the session echo's job, not this emit.
                if (IsCurrentInteractionUserDirected(sim))
                {
                    continue;
                }

                sUnclaimedRoutingLastEmittedIds[simObjectId] = interactionId;
                SendLine(BuildInteractionQueuedLine(sim, simObjectId, interactionId));
            }
        }

        // is the Sim's CURRENT interaction a player-directed (UserDirected) one? Keeps the
        // claimed-Sim autonomous emit (PollClaimedSimInteractionEmit) from double-emitting directed
        // orders that the authoritative session already echoes. Wrapped so a getter throwing
        // mid-simulation cannot kill the pump; on any failure it answers TRUE (suppress the emit) -- the
        // safe direction, since a missed autonomous emit only delays a puppet animation whereas a
        // wrongly-emitted directed order double-replays.
        private static bool IsCurrentInteractionUserDirected(Sim sim)
        {
            if (sim == null)
            {
                return false;
            }

            InteractionInstance instance = CurrentInteractionInstance(sim);
            if (instance == null)
            {
                return false;
            }

            try
            {
                InteractionPriority priority = instance.GetPriority();
                return priority.Level == InteractionPriorityLevel.UserDirected;
            }
            catch
            {
                return true;
            }
        }

        // command-lifecycle autonomy gate poll. For each registered (claimed) Sim, read
        // whether a player command (any UserDirected interaction) is currently in its queue and let
        // the pure gate decide the transition: first command in -> IncrementAutonomyDisabled; last
        // command out -> DecrementAutonomyDisabled. The engine itself preempts the running
        // autonomous action by HigherPriorityNext priority when the UserDirected GoHere is AddNext'd
        // (HandleQueueInteraction) — the gate never cancels interactions, so the orphaned-
        // SocializingPosture bug is not re-triggered. Mirrors PollAutonomyWatchQueues' structure.
        private static void PollCommandLifecycleGates()
        {
            if (sCommandGate.TrackedCount == 0)
            {
                return;
            }

            List<ulong> keys = sCommandGate.TrackedSims();
            for (int i = 0; i < keys.Count; i++)
            {
                ulong simObjectId = keys[i];
                Sim sim = FindSim(simObjectId);
                if (sim == null)
                {
                    // MUST keep an engaged gate tracked through a transient FindSim miss: a Sim that
                    // momentarily fails resolution (off-lot, mid-load) is NOT proof of non-existence
                    // (interactions.md — Bebe Hart class), and dropping it while engaged would leak
                    // the outstanding IncrementAutonomyDisabled. The authoritative removal is the
                    // controller's UNGATE_AUTONOMY on release/leave/disconnect, which decrements
                    // cleanly. If the Sim is genuinely gone the controller will clear it.
                    continue;
                }

                bool determinate;
                bool userDirectedPresent = HasUserDirectedInteraction(sim, out determinate);
                if (!determinate)
                {
                    // The queue could not be read cleanly this tick (engine mutated it mid-scan).
                    // Skip the observation entirely — an indeterminate read must NOT drive a gate
                    // transition (a false "absent" while engaged would spuriously resume autonomy
                    // mid-command). The next pump tick re-reads.
                    continue;
                }

                GateAction action = sCommandGate.Observe(simObjectId, userDirectedPresent);
                ApplyGateAction(sim, action);
            }
        }

        // apply a gate transition to the engine's per-Sim autonomy ref-count. Disable
        // increments unconditionally (the gate's engaged bit, not the engine count, tracks balance).
        // Enable is guarded by IsAnyAutonomyEnabled so a foreign decrement (a reset, or any
        // other system that already restored autonomy) can never drive the shared ref-count negative
        // — the same guard HandleEnableAutonomy's reset path uses.
        private static void ApplyGateAction(Sim sim, GateAction action)
        {
            if (sim == null || sim.Autonomy == null)
            {
                return;
            }

            if (action == GateAction.Disable)
            {
                sim.Autonomy.IncrementAutonomyDisabled();
            }
            else if (action == GateAction.Enable)
            {
                if (!AutonomyRestrictions.IsAnyAutonomyEnabled(sim))
                {
                    sim.Autonomy.DecrementAutonomyDisabled();
                }
            }
        }

        // does this Sim have ANY UserDirected interaction in its queue right now? The gate
        // decision is boolean (a player command is present or it is not), not an integer depth.
        // Iterate InteractionList ONLY: on this engine RunningInteraction is the head of
        // InteractionList, so adding it (or TransitionInteraction) into a count would double-count
        // and the queue would appear non-empty after the command finished — autonomy would never
        // resume. This mirrors the proven UserDirected scan in OnActiveActorQueueChanged. Every
        // accessor is wrapped so a getter throwing mid-simulation cannot kill the pump.
        //
        // determinate=false means the queue could not be read cleanly (the engine mutated it
        // mid-iteration). The caller MUST then skip the gate observation: an indeterminate read
        // returned as a false "absent" would spuriously disengage a gate mid-command.
        private static bool HasUserDirectedInteraction(Sim sim, out bool determinate)
        {
            determinate = true;

            if (sim == null)
            {
                return false;
            }

            InteractionQueue queue = sim.InteractionQueue;
            if (queue == null)
            {
                return false;
            }

            try
            {
                foreach (InteractionInstance inst in queue.InteractionList)
                {
                    if (inst == null)
                    {
                        continue;
                    }

                    try
                    {
                        InteractionPriority priority = inst.GetPriority();
                        if (priority.Level == InteractionPriorityLevel.UserDirected)
                        {
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                        // A single interaction's priority getter threw — skip it, keep scanning.
                    }
                }
            }
            catch (Exception)
            {
                // The engine mutated InteractionList mid-iteration. Report indeterminate so the
                // caller leaves the gate state untouched this tick; the next pump tick re-reads.
                determinate = false;
                return false;
            }

            return false;
        }

        private static void PollSpeedWatchdog()
        {
            if (sRemoteDialogsOpen > 0)
            {
                return; // a remote dialog's pause is not the player's speed choice
            }

            if (sSuppressSpeedCapture)
            {
                return;
            }

            if (sBridgeProbeTicks < sSpeedCaptureCooldownUntilTick)
            {
                return;
            }

            // (No longer skipped while position applies are pending: on clients that queue holds positions of Sims the
            // client doesn't have, so it was never empty and no speed click was ever forwarded, cold-test-077.)
            Sims3.SimIFace.Gameflow.GameSpeed liveSpeed = Sims3.Gameplay.Gameflow.CurrentGameSpeed;
            string liveWire = SpeedEnumToWire(liveSpeed);
            if (liveWire == sCurrentSpeed)
            {
                sSpeedCaptureCandidateWire = string.Empty;
                sSpeedCaptureStablePolls = 0;
                return;
            }

            // Debounce: require two consecutive polls (~500ms) before acting. Filters
            // one-tick Pause glitches from window focus / alt-tab during dev sessions.
            if (liveWire != sSpeedCaptureCandidateWire)
            {
                sSpeedCaptureCandidateWire = liveWire;
                sSpeedCaptureStablePolls = 1;
                return;
            }

            sSpeedCaptureStablePolls++;
            if (sSpeedCaptureStablePolls < 2)
            {
                return;
            }

            sSpeedCaptureCandidateWire = string.Empty;
            sSpeedCaptureStablePolls = 0;

            if (sCurrentRole == "client")
            {
                // While the server fast-forwards (Skip, everyone asleep there), this client's own game may drop Skip:
                // its copies of the Sims aren't asleep. That is not the player's choice; don't send it back (it would
                // end the server's fast-forward). A pause is always the player's.
                if (sCurrentSpeed == "Skip" && liveWire != "Pause")
                {
                    return;
                }

                // Client-role: forward the UI-initiated speed change to the server.
                sSuppressSpeedCapture = true;
                try
                {
                    Sims3.Gameplay.Gameflow.SetGameSpeed(
                        ParseSpeed(sCurrentSpeed),
                        Sims3.Gameplay.Gameflow.SetGameSpeedContext.GameStates);
                }
                finally
                {
                    sSuppressSpeedCapture = false;
                }

                SendLine(BridgeCodec.EventSpeedIntentCaptured(liveWire));
                ShowToast("Speed sent to server: " + liveWire);
            }
            else
            {
                // Server-role: the server's own game changed speed: its speed buttons, or the game fast-forwarding while
                // every Sim sleeps. The server is the authority, so this is the new speed for everyone: keep it and tell
                // the controller, which broadcasts it to the clients. (It used to be reverted within a poll.)
                sCurrentSpeed = liveWire;
                sSpeedCaptureCooldownUntilTick = sBridgeProbeTicks + SpeedCaptureCooldownTicks;
                SendLine(BridgeCodec.EventSpeedIntentCaptured(liveWire));
            }
        }

        private static string SpeedEnumToWire(Sims3.SimIFace.Gameflow.GameSpeed speed)
        {
            if (speed == Sims3.SimIFace.Gameflow.GameSpeed.Pause)
            {
                return "Pause";
            }
            if (speed == Sims3.SimIFace.Gameflow.GameSpeed.Normal)
            {
                return "Normal";
            }
            if (speed == Sims3.SimIFace.Gameflow.GameSpeed.Double)
            {
                return "Double";
            }
            if (speed == Sims3.SimIFace.Gameflow.GameSpeed.Triple)
            {
                return "Triple";
            }
            if (speed == Sims3.SimIFace.Gameflow.GameSpeed.Skip)
            {
                return "Skip";
            }
            return "Normal";
        }

        private static void EnqueuePendingPositionApply(ulong simObjectId, Vector3 target)
        {
            if (sPendingPositionApplies == null)
            {
                sPendingPositionApplies = new Dictionary<ulong, PendingPositionApply>();
            }

            if (!sPendingPositionApplies.ContainsKey(simObjectId) &&
                sPendingPositionApplies.Count >= PendingPositionApplyMaxEntries)
            {
                return;
            }

            PendingPositionApply pending = new PendingPositionApply();
            pending.Target = target;
            pending.EnqueuedAtBridgeTick = sBridgeProbeTicks;
            sPendingPositionApplies[simObjectId] = pending;
        }

        private static void PollClientPendingPositionApplies()
        {
            if (sCurrentRole != "client" || sPendingPositionApplies == null || sPendingPositionApplies.Count == 0)
            {
                return;
            }

            List<ulong> resolved = new List<ulong>();
            List<ulong> expired = new List<ulong>();

            foreach (KeyValuePair<ulong, PendingPositionApply> entry in sPendingPositionApplies)
            {
                if (sBridgeProbeTicks - entry.Value.EnqueuedAtBridgeTick > PendingPositionApplyMaxTicks)
                {
                    expired.Add(entry.Key);
                    continue;
                }

                Sim sim = FindSim(entry.Key);
                if (sim != null)
                {
                    sim.SetPosition(entry.Value.Target);
                    resolved.Add(entry.Key);
                }
            }

            for (int i = 0; i < resolved.Count; i++)
            {
                sPendingPositionApplies.Remove(resolved[i]);
            }

            for (int i = 0; i < expired.Count; i++)
            {
                sPendingPositionApplies.Remove(expired[i]);
            }
        }

        private static void PollClientInputInterception()
        {
            if (sCurrentRole != "client")
            {
                return;
            }

            Sim activeActor = Sim.ActiveActor;
            if (sTrackedActiveActor != activeActor)
            {
                if (sTrackedActiveActor != null && sTrackedActiveActor.InteractionQueue != null)
                {
                    sTrackedActiveActor.InteractionQueue.QueueChanged -= new Sims3.Gameplay.ActorSystems.InteractionQueue.QueueChangedCallback(OnActiveActorQueueChanged);
                }

                sTrackedActiveActor = activeActor;

                if (sTrackedActiveActor != null && sTrackedActiveActor.InteractionQueue != null)
                {
                    sTrackedActiveActor.InteractionQueue.QueueChanged += new Sims3.Gameplay.ActorSystems.InteractionQueue.QueueChangedCallback(OnActiveActorQueueChanged);
                    sClientQueueKnownInteractions = new List<InteractionInstance>(sTrackedActiveActor.InteractionQueue.InteractionList);
                }
            }
        }

        private static void OnActiveActorQueueChanged()
        {
            if (sIsProcessingQueueChange || sTrackedActiveActor == null || sTrackedActiveActor.InteractionQueue == null)
            {
                return;
            }

            sIsProcessingQueueChange = true;
            try
            {
                List<InteractionInstance> currentQueue = new List<InteractionInstance>(sTrackedActiveActor.InteractionQueue.InteractionList);
                foreach (InteractionInstance inst in currentQueue)
                {
                    if (inst != null && !sClientQueueKnownInteractions.Contains(inst))
                    {
                        InteractionPriority priority = inst.GetPriority();
                        if (priority.Level == InteractionPriorityLevel.UserDirected)
                        {
                            // UserDirected priority alone does NOT mean "the player clicked
                            // it." The engine pushes many interactions onto a directed Sim's queue at
                            // UserDirected priority (ReactionInteraction via Sim.PlayReaction, plus
                            // ~260 other CreateInstance call sites: BuffExhausted.PassOut,
                            // BeGivenSimoleons, WatchRobotDance, ...). Forwarding any of those as a
                            // captured intent makes the server reject it ("Unsupported interaction
                            // key"), which trips UT-1's wedge assertion. Forward ONLY interactions the
                            // server accepts as player commands -- the exact GoHere allowlist the
                            // server applies at HandleQueueInteraction (:863) / HandleApplyInteraction-
                            // Queue (:2476). This is the same string the forward keys on, so client
                            // and server agree and new reaction-like types fail closed.
                            string interactionKey = inst.InteractionDefinition != null ? inst.InteractionDefinition.GetType().FullName : string.Empty;
                            if (ClientCaptureFilter.ShouldForwardCapturedInteraction(interactionKey))
                            {
                                InterceptClientInteraction(sTrackedActiveActor, inst);
                            }
                            else
                            {
                                // Leave the engine interaction in the queue (do NOT remove it -- it is
                                // the Sim's own reaction/behavior) and emit a benign, log-visible drop
                                // diagnostic. NOT an ERROR line: it must not match logq.WEDGE_PATTERNS.
                                SendLine(BridgeCodec.EventCaptureFilterDropped(sTrackedActiveActor.ObjectId.Value, interactionKey));
                            }
                        }
                    }
                }
                sClientQueueKnownInteractions = currentQueue;
            }
            catch (Exception ex)
            {
                SendLine(BridgeCodec.Error("00000000000000000000000000000000", "QueueChanged hook error: " + ex.Message));
            }
            finally
            {
                sIsProcessingQueueChange = false;
            }
        }

        private static void InterceptClientInteraction(Sim actor, InteractionInstance inst)
        {
            sTrackedActiveActor.InteractionQueue.RemoveInteractionByRef(inst);

            ulong actorId = actor.ObjectId.Value;
            ulong targetId = inst.Target != null ? inst.Target.ObjectId.Value : 0uL;
            string interactionKey = inst.InteractionDefinition != null ? inst.InteractionDefinition.GetType().FullName : string.Empty;

            Vector3 targetPos = inst.Target != null ? inst.Target.Position : Vector3.Zero;
            bool hasPosition = false;

            try
            {
                System.Reflection.FieldInfo hitField = inst.GetType().GetField("Hit");
                if (hitField != null)
                {
                    object hitVal = hitField.GetValue(inst);
                    if (hitVal != null)
                    {
                        System.Reflection.FieldInfo pointField = hitVal.GetType().GetField("mPoint");
                        if (pointField != null)
                        {
                            targetPos = (Vector3)pointField.GetValue(hitVal);
                            hasPosition = true;
                        }
                    }
                }
            }
            catch
            {
                SwallowedErrors.Note();
            }

            string positionFlag = hasPosition ? "1" : "0";
            SendLine(BridgeCodec.EventIntentCaptured(actorId, targetId, interactionKey, positionFlag, Float(targetPos.x), Float(targetPos.y), Float(targetPos.z)));

            string toastKey = interactionKey;
            if (toastKey.Length > 40)
            {
                toastKey = toastKey.Substring(toastKey.Length - 40);
            }
            ShowToast("Sent to server: " + toastKey);
        }

        private static void OnInteractionStarted(Sim sim, float value)
        {
            ulong actorObjectId = sim == null ? 0uL : sim.ObjectId.Value;
            ulong interactionId = CurrentInteractionId(sim);
            if (interactionId == 0uL && sTrackedInteractionIds.ContainsKey(actorObjectId))
            {
                interactionId = sTrackedInteractionIds[actorObjectId];
            }

            SendLine(BridgeCodec.EventInteractionStarted(actorObjectId, interactionId));
        }

        private static void OnInteractionCompleted(Sim sim, float value)
        {
            ulong actorObjectId = sim == null ? 0uL : sim.ObjectId.Value;
            ulong interactionId = 0uL;
            if (sTrackedInteractionIds.ContainsKey(actorObjectId))
            {
                interactionId = sTrackedInteractionIds[actorObjectId];
                sTrackedInteractionIds.Remove(actorObjectId);
            }

            if (interactionId == 0uL)
            {
                interactionId = CurrentInteractionId(sim);
            }

            SendLine(BridgeCodec.EventInteractionCompleted(actorObjectId, interactionId));
        }

        private static void OnInteractionFailed(Sim sim, float value)
        {
            ulong actorObjectId = sim == null ? 0uL : sim.ObjectId.Value;
            ulong interactionId = 0uL;
            if (sTrackedInteractionIds.ContainsKey(actorObjectId))
            {
                interactionId = sTrackedInteractionIds[actorObjectId];
                sTrackedInteractionIds.Remove(actorObjectId);
            }

            if (interactionId == 0uL)
            {
                interactionId = CurrentInteractionId(sim);
            }

            // Diagnostic context: a bare "callback failed" cost a whole debugging loop once.
            // Key/position/speed/role are best-effort reads at failure time.
            string detail = "Interaction callback failed (key=" + CurrentInteractionKey(sim) +
                ", speed=" + sCurrentSpeed + ", role=" + sCurrentRole;
            if (sim != null)
            {
                Vector3 position = sim.Position;
                detail += ", pos=" + Float(position.x) + "/" + Float(position.y) + "/" + Float(position.z);
            }
            detail += ")";
            SendLine(BridgeCodec.EventInteractionFailed(actorObjectId, interactionId, detail));
        }

        private static void SendSessionReadyIfPending()
        {
            if (sPendingLoadCorrelationId == null)
            {
                return;
            }

            // P3.3: entering a new session - clear delta change trackers so prior entity state
            // never contaminates the first snapshot under the new save.
            ClearChangeTrackers();

            SendLine(BridgeCodec.SessionReady(sPendingLoadCorrelationId, sCurrentSaveName, SafeWorldFileName(), SafeTick()));
            BridgeMemoryBuffer.NoteControllerActivity();
            sPendingLoadCorrelationId = null;
            sLoadRequestPending = false;
            sLoadTransitionStarted = false;
            sStartupLoadRequested = false;
            sLoadViaMenu = false;
            sDeferredLoadTicks = 0;
        }

        private static void RejectPendingLoad(string reason)
        {
            if (sPendingLoadCorrelationId != null)
            {
                SendLine(BridgeCodec.Error(sPendingLoadCorrelationId, reason));
            }

            sPendingLoadCorrelationId = null;
            sLoadRequestPending = false;
            sLoadTransitionStarted = false;
            sStartupLoadRequested = false;
            sLoadViaMenu = false;
            sDeferredLoadTicks = 0;
        }

        // root cause: these used to write the first line DIRECTLY into the frame slot
        // whenever the queue was empty, with no CanWriteHostLine check. Any spontaneous line
        // (OnInteractionFailed callbacks, autonomy-watch events, input-interception intents)
        // destroyed an unread frame in place — which is exactly how QUEUE_INTERACTION /
        // APPLY_INTERACTION_QUEUE OK responses vanished and wedged the bridge for the full
        // host timeout. Direct write is now allowed only when the slot is actually consumable;
        // otherwise the line queues for DrainOutboundBudgeted (10ms pump cadence while pending).
        private static void SendLine(string line)
        {
            // A reply too big for a control slot became an ERROR with a zero correlation id (WriteHostLine) that no caller
            // matches, so the command timed out after 15 s: the SACS_REPLAY report outgrew 4 KB hex-encoded and
            // fire-near's assert timed out (2026-10-17). A reply line goes through a host bulk slot instead, under its own
            // correlation id; anything else (or no slot free) still meets the error.
            if (line != null && line.Length > ControlLineFastChars && Encoding.UTF8.GetByteCount(line) > BridgeMemoryBuffer.BufferSize - BridgeMemoryBuffer.HeaderSize)
            {
                string correlationId = ReplyCorrelationId(line);
                lock (sSendLock)
                {
                    if (correlationId != null && TrySendBulkReply(correlationId, line))
                    {
                        return;
                    }
                }
            }

            lock (sSendLock)
            {
                // SendLine carries small latency-sensitive control/command/event lines ->
                // PRIORITY lane. Fast-path direct write only when BOTH lanes are empty (else a
                // priority line could be written ahead of an already-queued line out of order, or
                // lose the consumed-slot it expects); otherwise enqueue priority.
                if (sPendingOutboundLines.IsEmpty && BridgeMemoryBuffer.CanWriteHostLine())
                {
                    BridgeMemoryBuffer.WriteHostLine(line);
                }
                else
                {
                    sPendingOutboundLines.EnqueuePriority(line);
                }
            }
        }

        private static void SendLines(string[] lines)
        {
            if (lines == null || lines.Length == 0)
            {
                return;
            }

            lock (sSendLock)
            {
                // SendLines is the only bulk producer (snapshot chunks via
                // SendChunkedSnapshot) -> BULK lane. Chunks stay in order relative to each other
                // (FIFO within the lane). Fast-path the first line only when BOTH lanes are empty.
                int start = 0;
                if (sPendingOutboundLines.IsEmpty && BridgeMemoryBuffer.CanWriteHostLine())
                {
                    BridgeMemoryBuffer.WriteHostLine(lines[0]);
                    start = 1;
                }

                for (int i = start; i < lines.Length; i++)
                {
                    sPendingOutboundLines.EnqueueBulk(lines[i]);
                }
            }
        }

        private static bool FlushQueuedOutboundLine()
        {
            lock (sSendLock)
            {
                // dequeue priority-first (bulk only when priority empty) inside the helper.
                if (!sPendingOutboundLines.HasPending)
                {
                    return false;
                }

                // re-check consumability under the send lock — the drain loop's check
                // happens outside it, and a sim-thread SendLine may have written in between.
                if (!BridgeMemoryBuffer.CanWriteHostLine())
                {
                    return false;
                }

                BridgeMemoryBuffer.WriteHostLine(sPendingOutboundLines.Dequeue());
                return true;
            }
        }

        // legacy lossy write used only while the controller has never stamped a frame
        // consumed (pre- controller). Overwrites whatever is in the slot.
        private static bool FlushQueuedOutboundLineUnchecked()
        {
            lock (sSendLock)
            {
                // priority-first here too, so a stalled-slot fallback still flushes the
                // latency-sensitive line ahead of the bulk backlog.
                if (!sPendingOutboundLines.HasPending)
                {
                    return false;
                }

                BridgeMemoryBuffer.WriteHostLine(sPendingOutboundLines.Dequeue());
                return true;
            }
        }

        private static long SafeTick()
        {
            try
            {
                return Simulator.TicksElapsed();
            }
            catch
            {
                return 0;
            }
        }

        private static string SafeWorldFileName()
        {
            try
            {
                return World.GetWorldFileName();
            }
            catch
            {
                return string.Empty;
            }
        }

        // W2 part B (internal documentation): Story Progression moves, marries, births and
        // kills Sims town-wide on its own alarm; on a client that is pure divergence, the server alone
        // runs it. While in client role and in-world, keep it Disabled; restore it if the role goes back
        // to server. Cheap per tick: one role/in-world check, the engine call only on a transition.
        private static bool sClientStoryProgressionDisabled;

        // presentation stream: the server records every animation call from world load on, so
        // the controller can stream them and late joiners get each driver's full path. A world load
        // installs a fresh proxy, so this re-checks every tick (one reflection read).
        // nobody sits at the server's window, and most dialogs pause the whole simulator until a
        // click (ModalDialog.Pause, PauseMode.PauseSimulator: game speed locked to 0, the task asleep). So in
        // server role modal dialogs are off: ModalDialog.Show* returns its default answer at once. This covers
        // the dialogs whose Show checks the flag (SimpleMessage, StringInput, TwoButton, AcceptCancel,
        // ThreeButton: ~74% of gameplay call sites); 60 dialog classes ignore it (ObjectPicker,
        // ObjectListPicker, SimplePurchase, ...), which the UI fork's remote dialogs must cover. A TS3 that
        // becomes a client gets its dialogs back.
        private static bool sDialogsDisabledByUs;

        private static void ApplyServerDialogPolicy()
        {
            try
            {
                if (sCurrentRole == "server")
                {
                    if (Sims3.UI.ModalDialog.EnableModalDialogs)
                    {
                        Sims3.UI.ModalDialog.EnableModalDialogs = false;
                        sDialogsDisabledByUs = true;
                    }
                }
                else if (sDialogsDisabledByUs)
                {
                    Sims3.UI.ModalDialog.EnableModalDialogs = true;
                    sDialogsDisabledByUs = false;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // remote dialogs.
        // Server role: the UI fork's Sims3.UI.SimsOnlineDialogHook (absent on a stock UI) never displays a
        // dialog opened on a Sim's task, and queues a claimed Sim's common dialogs for its player. This pump
        // keeps the hook's Sim sets current (every Sim; the claimed Sims from SET_SIM_SCOPE), sends the queued
        // requests (EVENT|DialogRequest) and hands answers back (DIALOG_ANSWER).
        // Client role: SHOW_DIALOG shows a request with the stock UI on a simulator task and sends the answer
        // (EVENT|DialogAnswer); a "Notification" request is shown as a notification and not answered.
        // DIALOG_AUTOANSWER answers without UI and logs what it would have shown (tests).
        private static ulong[] sScopeSims = new ulong[0];

        // A player's claimed Sim (SET_SIM_SCOPE's sims): MethodSwap's GoHome gate. Lock-free: the array is
        // replaced whole.
        internal static bool IsScopeSim(ulong simId)
        {
            ulong[] sims = sScopeSims;
            for (int i = 0; i < sims.Length; i++)
            {
                if (sims[i] == simId)
                {
                    return true;
                }
            }

            return false;
        }

        // a Sim whose household holds a claimed Sim, the fork's H1 rule (docs/engine/scope-hooks.md), for the
        // GoHome gate: a claimed Sim's housemate is selectable on the server too and goes home only when its player says so.
        internal static bool IsScopeHousehold(Sim sim)
        {
            ulong[] claimed = sScopeSims;
            if (sim == null || claimed.Length == 0)
            {
                return false;
            }

            try
            {
                Household household = sim.Household;
                List<Sim> actors = household == null ? null : household.AllActors;
                if (actors == null || actors.Count == 0)
                {
                    return IsScopeSim(sim.ObjectId.Value);
                }

                ulong[] members = new ulong[actors.Count];
                for (int i = 0; i < actors.Count; i++)
                {
                    members[i] = actors[i] == null ? 0uL : actors[i].ObjectId.Value;
                }

                return SimScopeWire.AnyClaimed(claimed, members);
            }
            catch (Exception)
            {
                return IsScopeSim(sim.ObjectId.Value);
            }
        }

        private static bool sDialogHookResolved;
        private static Type sDialogHookType;
        private static int sDialogHookRefreshTick = -100;
        private static long sDialogRequestsSent;
        private static long sDialogAnswersApplied;
        private static int sRemoteDialogsOpen;
        private static string sDialogAutoAnswer;
        private static readonly List<string> sDialogLog = new List<string>();

        private static Type DialogHook()
        {
            if (!sDialogHookResolved)
            {
                sDialogHookResolved = true;
                try
                {
                    sDialogHookType = typeof(Sims3.UI.ModalDialog).Assembly.GetType("Sims3.UI.SimsOnlineDialogHook");
                }
                catch (Exception)
                {
                    sDialogHookType = null;
                }
            }

            return sDialogHookType;
        }

        // Stings (AudioRecorder, seam-gaps row 4): the claimed Sim a sting belongs to: the Sim itself, or the claimed Sim of
        // its household ('s H1 rule: a housemate's career sting is that player's too); 0 = nobody's.
        private static ulong StingOwnerOf(ulong simId)
        {
            if (IsScopeSim(simId))
            {
                return simId;
            }

            try
            {
                Sim sim = Sims3.Gameplay.Abstracts.GameObject.GetObject<Sim>(new ObjectGuid(simId));
                Household household = sim == null ? null : sim.Household;
                List<Sim> actors = household == null ? null : household.AllActors;
                for (int i = 0; actors != null && i < actors.Count; i++)
                {
                    if (actors[i] != null && IsScopeSim(actors[i].ObjectId.Value))
                    {
                        return actors[i].ObjectId.Value;
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return 0uL;
        }

        private static int sNextStingId = 1000000000; // apart from the dialog hook's request ids; a sting is never answered
        private static long sStingsSent;
        private static long sStingsPlayed;

        // Server role: send the queued stings to their owners as remote dialogs of kind Sting ('s routing: the
        // session sends a DialogRequest to the player who claims its Sim, and nobody else).
        private static void PumpStings()
        {
            try
            {
                if (sCurrentRole != "server" || AudioRecorder.PendingStings.Count == 0)
                {
                    return;
                }

                CultureInfo c = CultureInfo.InvariantCulture;
                for (int i = 0; i < AudioRecorder.PendingStings.Count; i++)
                {
                    string[] sting = AudioRecorder.PendingStings[i];
                    SendLine(BridgeCodec.EventDialogRequest(new string[] { (sNextStingId++).ToString(c), sting[0], "Sting", sting[1], sting[2] }));
                    sStingsSent++;
                }

                AudioRecorder.PendingStings.Clear();
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // Client role: a sting of this player's Sim (name, the server's Sim it plays on or 0): on this client's copy of the
        // Sim when there is one, else as the server played it (no position).
        private static void PlayRemoteSting(string[] args)
        {
            try
            {
                string name = DialogArg(args, 0);
                ulong onSim;
                ulong.TryParse(DialogArg(args, 1), NumberStyles.None, CultureInfo.InvariantCulture, out onSim);
                ObjectGuid copy = onSim == 0uL ? ObjectGuid.InvalidObjectGuid : ReplayObject(onSim);
                uint handle = copy.IsValid ? Audio.StartObjectSound(copy, name, false) : Audio.StartSound(name);
                if (handle != 0u)
                {
                    sStingsPlayed++;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static void PumpDialogHook()
        {
            try
            {
                Type hook = DialogHook();
                if (hook == null)
                {
                    return;
                }

                bool server = sCurrentRole == "server" && SafeIsInWorld();
                FieldInfo enabled = hook.GetField("Enabled");
                if ((bool)enabled.GetValue(null) != server)
                {
                    enabled.SetValue(null, server);
                }

                if (!server)
                {
                    return;
                }

                if (sBridgeProbeTicks - sDialogHookRefreshTick >= 8)
                {
                    sDialogHookRefreshTick = sBridgeProbeTicks;
                    Dictionary<ulong, bool> sims = new Dictionary<ulong, bool>();
                    foreach (Sim sim in LotManager.Actors)
                    {
                        if (sim != null)
                        {
                            sims[sim.ObjectId.Value] = true;
                        }
                    }

                    hook.GetField("SimTasks").SetValue(null, sims);
                    Dictionary<ulong, bool> remote = new Dictionary<ulong, bool>();
                    for (int i = 0; i < sScopeSims.Length; i++)
                    {
                        remote[sScopeSims[i]] = true;
                    }

                    hook.GetField("RemoteSims").SetValue(null, remote);
                }

                System.Collections.IList outbox = hook.GetField("Outbox").GetValue(null) as System.Collections.IList;
                if (outbox == null || outbox.Count == 0)
                {
                    return;
                }

                for (int i = 0; i < outbox.Count; i++)
                {
                    string[] entry = outbox[i] as string[];
                    if (entry != null && entry.Length >= 3)
                    {
                        SendLine(BridgeCodec.EventDialogRequest(entry));
                        sDialogRequestsSent++;
                    }
                }

                outbox.Clear();
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // DIALOG_ANSWER|corr|requestId|hexAnswer ("-" = none: the dialog keeps its default). An answer that
        // arrives after the hook timed out stays in Answers unread (rare; bounded by timed-out requests).
        private static void HandleDialogAnswer(BridgeCommand command)
        {
            Type hook = DialogHook();
            if (command.Parts.Length < 4 || sCurrentRole != "server" || hook == null)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "DIALOG_ANSWER needs the server role, the UI fork's dialog hook, a request id and an answer."));
                return;
            }

            int id = int.Parse(command.Parts[2], CultureInfo.InvariantCulture);
            string answer = command.Parts[3] == "-" ? "-" : BridgeCodec.DecodeText(command.Parts[3]);
            System.Collections.IDictionary answers = hook.GetField("Answers").GetValue(null) as System.Collections.IDictionary;
            answers[id] = answer;
            sDialogAnswersApplied++;
            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // SHOW_DIALOG|corr|requestId|sim|hexKind|argCount|hexArg...
        private static void HandleShowDialog(BridgeCommand command)
        {
            if (sCurrentRole != "client" || command.Parts.Length < 6)
            {
                SendLine(BridgeCodec.Error(command.CorrelationId, "SHOW_DIALOG is client-role only and needs id, sim, kind and arguments."));
                return;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            int id = int.Parse(command.Parts[2], c);
            ulong sim = ulong.Parse(command.Parts[3], c);
            string kind = BridgeCodec.DecodeText(command.Parts[4]);
            int count = int.Parse(command.Parts[5], c);
            string[] args = new string[count];
            for (int i = 0; i < count; i++)
            {
                args[i] = 6 + i < command.Parts.Length ? BridgeCodec.DecodeText(command.Parts[6 + i]) : string.Empty;
            }

            Simulator.AddObject(new OneShotFunctionTask(new Function(new RemoteDialogRunner(id, sim, kind, args).Run)));
            SendLine(BridgeCodec.Ok(command.CorrelationId));
        }

        // DIALOG_AUTOANSWER|corr|off|-|hexAnswer: tests answer remote dialogs without UI. Replies with the log of
        // dialogs shown (kind:args), newest last.
        private static void HandleDialogAutoAnswer(BridgeCommand command)
        {
            if (command.Parts.Length >= 3)
            {
                string value = command.Parts[2];
                sDialogAutoAnswer = value == "off" ? null : value == "-" ? "-" : BridgeCodec.DecodeText(value);
            }

            SendLine(BridgeCodec.AutonomyProbe(command.CorrelationId, "ok", "auto=" + (sDialogAutoAnswer == null ? "off" : sDialogAutoAnswer)
                + ";open=" + sRemoteDialogsOpen.ToString(CultureInfo.InvariantCulture) + ";sent=" + sDialogRequestsSent.ToString(CultureInfo.InvariantCulture)
                + ";answered=" + sDialogAnswersApplied.ToString(CultureInfo.InvariantCulture) + ";log=" + string.Join(" || ", sDialogLog.ToArray()).Replace(';', ',')));
        }

        private sealed class RemoteDialogRunner
        {
            private readonly int mId;
            private readonly ulong mSim;
            private readonly string mKind;
            private readonly string[] mArgs;

            public RemoteDialogRunner(int id, ulong sim, string kind, string[] args)
            {
                mId = id;
                mSim = sim;
                mKind = kind;
                mArgs = args;
            }

            public void Run()
            {
                ShowRemoteDialog(mId, mSim, mKind, mArgs);
            }
        }

        // the server's picker as text (Sims3.UI.SimsOnlineDialogHook.PickerArgs): { title, buttonTrue,
        // buttonFalse, numSelectableRows, headers, tab... }. Rebuilt with text columns; each row's item is its
        // "tab:row", which is the answer. Thumbnails aren't carried yet.
        private const char PickerColumnSeparator = (char)0x1F;
        private const char PickerRowSeparator = (char)0x1E;
        private const char PickerTabSeparator = (char)0x1D;

        private static string ShowRemotePicker(string[] args)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            int numSelectable;
            if (!int.TryParse(DialogArg(args, 3), NumberStyles.Integer, c, out numSelectable) || numSelectable < 1)
            {
                numSelectable = 1;
            }

            List<Sims3.UI.ObjectPicker.HeaderInfo> headers = new List<Sims3.UI.ObjectPicker.HeaderInfo>();
            foreach (string record in DialogArg(args, 4).Split(PickerRowSeparator))
            {
                if (record.Length == 0)
                {
                    continue;
                }

                string[] f = record.Split(PickerColumnSeparator);
                int width;
                if (f.Length < 4 || !int.TryParse(f[2], NumberStyles.Integer, c, out width))
                {
                    continue;
                }

                headers.Add(new Sims3.UI.ObjectPicker.HeaderInfo(f[0], f[1], width, f[3] == "1"));
            }

            List<Sims3.UI.ObjectPicker.TabInfo> tabs = new List<Sims3.UI.ObjectPicker.TabInfo>();
            for (int t = 5; t < args.Length; t++)
            {
                string spec = args[t];
                int split = spec.IndexOf(PickerTabSeparator);
                string tabText = split < 0 ? string.Empty : spec.Substring(0, split);
                string body = split < 0 ? spec : spec.Substring(split + 1);
                List<Sims3.UI.ObjectPicker.RowInfo> rows = new List<Sims3.UI.ObjectPicker.RowInfo>();
                string[] records = body.Split(PickerRowSeparator);
                for (int r = 0; r < records.Length; r++)
                {
                    if (r == records.Length - 1 && records[r].Length == 0)
                    {
                        break; // the trailing separator
                    }

                    List<Sims3.UI.ObjectPicker.ColumnInfo> columns = new List<Sims3.UI.ObjectPicker.ColumnInfo>();
                    string[] cells = records[r].Split(PickerColumnSeparator);
                    for (int k = 0; k < cells.Length; k++)
                    {
                        if (k == cells.Length - 1 && cells[k].Length == 0)
                        {
                            break;
                        }

                        columns.Add(new Sims3.UI.ObjectPicker.TextColumn(cells[k]));
                    }

                    rows.Add(new Sims3.UI.ObjectPicker.RowInfo((t - 5).ToString(c) + ":" + r.ToString(c), columns));
                }

                tabs.Add(new Sims3.UI.ObjectPicker.TabInfo(string.Empty, tabText, rows));
            }

            List<Sims3.UI.ObjectPicker.RowInfo> picked = Sims3.UI.ObjectPickerDialog.Show(DialogArg(args, 0), DialogArg(args, 1), DialogArg(args, 2), tabs, headers, numSelectable);
            if (picked == null || picked.Count == 0)
            {
                return null;
            }

            List<string> answer = new List<string>();
            foreach (Sims3.UI.ObjectPicker.RowInfo row in picked)
            {
                answer.Add(row.Item as string);
            }

            return string.Join(",", answer.ToArray());
        }

        private static string DialogArg(string[] args, int index)
        {
            return index < args.Length ? args[index] : string.Empty;
        }

        private static void ShowRemoteDialog(int id, ulong sim, string kind, string[] args)
        {
            string answer = null;
            if (kind == "Sting")
            {
                if (sDialogLog.Count >= 20)
                {
                    sDialogLog.RemoveAt(0);
                }

                sDialogLog.Add(kind + ":" + string.Join("/", args));
                PlayRemoteSting(args);
                return;
            }

            bool reply = kind != "Message" && kind != "Notification";
            sRemoteDialogsOpen++;
            try
            {
                if (sDialogLog.Count >= 20)
                {
                    sDialogLog.RemoveAt(0);
                }

                sDialogLog.Add(kind + ":" + string.Join("/", args));
                if (sDialogAutoAnswer != null)
                {
                    answer = sDialogAutoAnswer == "-" ? null : sDialogAutoAnswer;
                }
                else if (kind == "TwoButton")
                {
                    answer = Sims3.UI.TwoButtonDialog.Show(DialogArg(args, 0), DialogArg(args, 1), DialogArg(args, 2)) ? "1" : "0";
                }
                else if (kind == "AcceptCancel")
                {
                    answer = Sims3.UI.AcceptCancelDialog.Show(DialogArg(args, 0)) ? "1" : "0";
                }
                else if (kind == "ThreeButton")
                {
                    answer = ((int)Sims3.UI.ThreeButtonDialog.Show(DialogArg(args, 0), DialogArg(args, 1), DialogArg(args, 2), DialogArg(args, 3))).ToString(CultureInfo.InvariantCulture);
                }
                else if (kind == "StringInput")
                {
                    answer = Sims3.UI.StringInputDialog.Show(DialogArg(args, 0), DialogArg(args, 1), DialogArg(args, 2));
                }
                else if (kind == "Message")
                {
                    Sims3.UI.SimpleMessageDialog.Show(DialogArg(args, 0), DialogArg(args, 1));
                }
                else if (kind == "ObjectPicker")
                {
                    answer = ShowRemotePicker(args);
                }
                else if (kind == "Notification")
                {
                    // text, style, object1, object2 (the server's ids: this client's copies for the portraits)
                    CultureInfo c = CultureInfo.InvariantCulture;
                    ulong first;
                    ulong second;
                    uint style;
                    ulong.TryParse(DialogArg(args, 2), NumberStyles.None, c, out first);
                    ulong.TryParse(DialogArg(args, 3), NumberStyles.None, c, out second);
                    if (!uint.TryParse(DialogArg(args, 1), NumberStyles.None, c, out style) || style == 0u)
                    {
                        style = 2u; // kSystemMessage
                    }

                    ObjectGuid object1 = first == 0uL ? ObjectGuid.InvalidObjectGuid : ReplayObject(first);
                    ObjectGuid object2 = second == 0uL ? ObjectGuid.InvalidObjectGuid : ReplayObject(second);
                    Sims3.UI.StyledNotification.Show(new Sims3.UI.StyledNotification.Format(DialogArg(args, 0), object1, object2, (Sims3.UI.StyledNotification.NotificationStyle)style));
                }
            }
            catch (Exception)
            {
                answer = null;
            }
            finally
            {
                sRemoteDialogsOpen--;
            }

            if (reply)
            {
                SendLine(BridgeCodec.EventDialogAnswer(id, sim, answer));
            }
        }

        private static void EnsureServerSacsRecorder()
        {
            try
            {
                if (sCurrentRole != "server" || !SafeIsInWorld())
                {
                    return;
                }

                if (!SacsRecorder.Installed)
                {
                    SacsRecorder.Install();
                }

                if (!SlotsRecorder.Installed)
                {
                    SlotsRecorder.Install();
                }

                if (!VfxRecorder.Installed)
                {
                    VfxRecorder.Install();
                }

                if (!AnimationRecorder.Installed)
                {
                    AnimationRecorder.Install();
                }

                if (!SimulatorRecorder.Installed)
                {
                    SimulatorRecorder.Install();
                }

                if (!WorldRecorder.Installed)
                {
                    WorldRecorder.Install();
                }

                if (!RouteRecorder.Installed)
                {
                    RouteRecorder.Install();
                }

                if (!ObjectsRecorder.Installed)
                {
                    ObjectsRecorder.Install();
                }

                if (!AudioRecorder.Installed)
                {
                    AudioRecorder.Install();
                }

                if (AudioRecorder.StingOwner == null)
                {
                    AudioRecorder.StingOwner = new Converter<ulong, ulong>(StingOwnerOf);
                }

                if (!LookAtRecorder.Installed)
                {
                    LookAtRecorder.Install();
                }

                SacsRecorder.RingEnabled = true;
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static void ApplyClientStoryProgressionPolicy()
        {
            try
            {
                bool wantDisabled = sCurrentRole == "client" && SafeIsInWorld();
                if (wantDisabled == sClientStoryProgressionDisabled)
                {
                    return;
                }

                if (wantDisabled)
                {
                    Sims3.Gameplay.StoryProgression.StoryProgressionService.SetMode(Sims3.Gameplay.StoryProgression.StoryProgressionService.Mode.Disabled);
                    sClientStoryProgressionDisabled = true;
                }
                else if (sCurrentRole != "client")
                {
                    Sims3.Gameplay.StoryProgression.StoryProgressionService.SetMode(Sims3.Gameplay.StoryProgression.StoryProgressionService.Mode.Enabled);
                    sClientStoryProgressionDisabled = false;
                }
                else
                {
                    // Left the world as a client: the next world load starts a fresh service.
                    sClientStoryProgressionDisabled = false;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static bool SafeIsInWorld()
        {
            try
            {
                return GameStates.IsInWorld();
            }
            catch
            {
                return false;
            }
        }
    }
}
