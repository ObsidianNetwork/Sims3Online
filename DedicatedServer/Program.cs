using System.Globalization;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;
using System.Diagnostics;
using Sentry;
using System.Runtime.InteropServices;
using System.Text;

namespace SimsOnline.DedicatedServer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        SessionConfig config = SessionConfig.FromArgs(args);

        await using Observability.SimsOnlineTelemetry telemetry =
            Observability.SentryConfigurator.Configure(config, Observability.SentryReleaseResolver.Resolve());
        telemetry.AddBreadcrumb("controller.starting");
        telemetry.LogInfo("controller.starting");
        telemetry.RecordRuntimeActiveMetric(active: true);

        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        // SIGTERM (dev-session's graceful stop, systemd, `kill <pid>`) must run the same
        // graceful path as Ctrl+C — otherwise the process dies without sending the bridge
        // DISCONNECT and the mod only learns the controller is gone after its 10s evidence
        // window. Without this registration .NET's default SIGTERM handling exits without
        // unwinding the finally blocks below.
        //
        // IMPORTANT — launch vs attach mode + SIGTERM latency (2026-07-03):
        //   • Attach mode (--attach-existing): the game was started externally, the controller
        //     does NOT own it, so StopAsync's StopOwnedHostProcessAsync is a no-op. SIGTERM
        //     exits in <1s after sending DISCONNECT. This is the path `server-reattach` and
        //     `--attach-pid` use.
        //   • Launch mode (default): the controller launched the game and owns it, so
        //     StopAsync calls StopOwnedHostProcessAsync which kills the owned TS3 (CloseMainWindow
        //     -> up to 10s wait -> Kill entireProcessTree). SIGTERM can take 10-15s here.
        //     `server-reattach` MUST NOT use SIGTERM on a launch-mode controller — it goes
        //     straight to SIGKILL via Stop-ControllerForReattach so the warm game stays alive.
        //     dev-session's Stop-ControllerProcess (used by `down`, `stop-controller`) DOES send
        //     SIGTERM first (the graceful DISCONNECT is desirable for those verbs), escalating
        //     to SIGKILL after the grace window if the owned-game teardown hangs.
        using PosixSignalRegistration? sigterm = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });

        Console.WriteLine("SimsOnline dedicated server");
        Console.WriteLine($"TS3 host: {config.Ts3ExecutablePath}");
        Console.WriteLine($"Save: {config.SaveName}");
        Console.WriteLine($"Listen: {config.ListenEndpoint}");
        Console.WriteLine($"Host bridge: {config.HostBridgeEndpoint}");
        Console.WriteLine($"Host probe only: {config.HostProbeOnly}");
        Console.WriteLine($"Probe only: {config.ProbeOnly}");
        Console.WriteLine($"Snapshot and exit: {config.SnapshotAndExit}");
        Console.WriteLine($"Probe ObjectGuid stability: {config.ProbeObjectGuidStability}");
        Console.WriteLine($"Probe Sim lifecycle: {config.ProbeSimLifecycle}");
        Console.WriteLine($"Probe state injection: {config.ProbeStateInjection}");
        Console.WriteLine($"Probe actor-lookup parity: {config.ProbeActorLookupParity}");
        Console.WriteLine($"Probe role switch: {config.ProbeRoleSwitch}");
        Console.WriteLine($"Probe motive roundtrip: {config.ProbeMotiveRoundtrip}");
        Console.WriteLine($"Probe interaction roundtrip: {config.ProbeInteractionRoundtrip}");
        Console.WriteLine($"Probe cash roundtrip: {config.ProbeCashRoundtrip}");
        Console.WriteLine($"Probe skill roundtrip: {config.ProbeSkillRoundtrip}");
        Console.WriteLine($"Probe autonomy roundtrip: {config.ProbeAutonomyRoundtrip}");
        Console.WriteLine($"Probe load path comparison: {config.ProbeLoadPathComparison}");
        Console.WriteLine($"Probe autonomy: {config.ProbeAutonomy}");
        Console.WriteLine($"Go here: {config.GoHere}");

        // 2026-07-03 dev-tooling phase 1: machine-readable lifecycle state for agents/scripts
        // (no more grepping stdout for the ready line). Null when --state-file is not passed.
        RunStateFile? state = config.StateFilePath != null ? new RunStateFile(config.StateFilePath) : null;
        state?.Set("save", config.SaveName);
        state?.Set("listen", config.ListenEndpoint);
        state?.Set("bridgectl", config.BridgectlEndpoint);
        state?.Set("mode", config.AttachExistingHost ? "attach" : "launch");
        state?.Transition("starting");

        try
        {
            string? probeArgumentError = config.ValidateProbeArguments();
            if (probeArgumentError != null)
            {
                Console.Error.WriteLine(probeArgumentError);
                return 1;
            }

            if (config.ProbeObjectGuidStability)
            {
                return await RunObjectGuidStabilityProbeAsync(config, shutdown.Token);
            }

            if (config.ProbeLoadPathComparison)
            {
                return await RunLoadPathComparisonProbeAsync(config, shutdown.Token);
            }

            await using IAuthoritativeSimulationHost host = new HeadlessSims3Host(config);
            AuthoritativeSession session = new(host);
            session.ConfigureInterestScopedMirroring(!config.DisableInterestScopedMirroring);
            // SIMSONLINE_CLIENT_NATIVE_MODES=0 keeps every client a presenter (one simulator).
            // presentation stream (default on; SIMSONLINE_PRESENTATION_STREAM=0 turns it off). Streamed
            // animation and a client simulating its lot natively would fight, so the stream also turns
            // client-native modes off unless SIMSONLINE_CLIENT_NATIVE_MODES=1 keeps them.
            bool presentationStream = Environment.GetEnvironmentVariable("SIMSONLINE_PRESENTATION_STREAM") != "0";
            // the session tells each joiner whether the stream runs; it runs only in the sync broadcaster.
            session.ConfigurePresentationStream(presentationStream && SyncBroadcaster.Runs(config));
            string? nativeModes = Environment.GetEnvironmentVariable("SIMSONLINE_CLIENT_NATIVE_MODES");
            if (nativeModes == "0" || (presentationStream && nativeModes != "1"))
            {
                session.ConfigureClientNativeModes(false);
                Console.WriteLine("[config] client-native lot modes off: every player is a presenter");
            }

            Console.WriteLine($"[config] presentation stream {(presentationStream ? "on" : "off")}");
            // route stream (opt-in): clients walk streamed Sims along their own route to the server's goal.
            bool routeStream = presentationStream && Environment.GetEnvironmentVariable("SIMSONLINE_ROUTE_STREAM") == "1";
            session.ConfigureRouteStream(routeStream);
            if (routeStream)
            {
                Console.WriteLine("[config] route stream on");
            }
            // card 55 (opt-in): the server's walks take the Rust plan at the RouteCtor seam (sent once the session has started, with its plan hop).
            bool rustRoute = Environment.GetEnvironmentVariable("SIMSONLINE_RUST_ROUTE") == "1";
            session.ConfigureRustRoute(rustRoute);
            if (rustRoute)
            {
                Console.WriteLine("[config] rust route on");
            }
            WireProbeEvents(host);
            WireBridgeTrace(host, config);
            if (state != null && host is HeadlessSims3Host headlessForState)
            {
                headlessForState.PhaseSink = (phase, detail) =>
                {
                    if (headlessForState.HostProcessId is { } gamePid)
                    {
                        state.Set("gamePid", gamePid);
                        telemetry.SetTs3ProcessId(gamePid);
                    }

                    if (headlessForState.Bridge.ModBuildStamp is { } stamp)
                    {
                        state.Set("modBuild", stamp);
                    }

                    state.Transition(phase, detail);
                };
            }

            try
            {
                if (config.HostProbeOnly)
                {
                    await host.StartAsync(shutdown.Token);
                    if (host is HeadlessSims3Host probeHost && probeHost.HostProcessId is { } probePid)
                    {
                        telemetry.SetTs3ProcessId(probePid);
                    }
                    Console.WriteLine("Sims 3 host bridge probe succeeded.");
                    Console.Out.Flush();
                    return 0;
                }

                state?.Transition("session-starting", config.AttachExistingHost ? "re-attach (skip load if in-world)" : "load save");
                telemetry.AddBreadcrumb("session.starting");
                using (Observability.SimsOnlineTelemetryTransaction startTransaction =
                    telemetry.StartTransaction("session.start", Observability.SentryTracesSampler.OpSessionStart))
                {
                    try
                    {
                        await session.StartAsync(config.SaveName, shutdown.Token, config.AttachExistingHost);
                        if (host is HeadlessSims3Host startedHost && startedHost.HostProcessId is { } gamePid)
                        {
                            telemetry.SetTs3ProcessId(gamePid);
                        }
                        startTransaction.Complete(succeeded: true);
                    }
                    catch (Exception ex)
                    {
                        WriteSentryEventId(telemetry.ReportFailureOnce(
                            ex,
                            "controller.unhandled"));
                        startTransaction.Complete(succeeded: false);
                        throw;
                    }
                }
                state?.Transition("session-started");
                telemetry.AddBreadcrumb("session.started");
                // card 55 R3: the Rust route switch and its plan hop need the bridge, not the presentation stream.
                if (session.RustRouteEnabled)
                {
                    RoutePlanHop rustHop = new(new KernelPendingRoutePlanSource(), (args, token) => host.SacsRecorderAsync(args, token));
                    HostBridgeServer? rustBridge = (host as HeadlessSims3Host)?.Bridge;
                    await session.StartRustRouteAsync(rustHop, rustBridge == null ? null : handler => rustBridge.RoutePlanRequested += handler, shutdown.Token);
                }

                if (Environment.GetEnvironmentVariable("SIMSONLINE_RUST_ROAD_PLACE") == "1")
                {
                    HostBridgeServer? roadBridge = (host as HeadlessSims3Host)?.Bridge;
                    if (roadBridge != null)
                    {
                        SidecarRoadPlaceSource source = new();
                        RoadPlaceHop roadHop = new(source, (args, token) => host.SacsRecorderAsync(args, token));
                        roadBridge.RoadPlaceRequested += request => _ = roadHop.HandleAsync(request, shutdown.Token);
                        shutdown.Token.Register(source.Dispose);
                        await host.SacsRecorderAsync(new[] { "rust-road-place-on", "1" }, shutdown.Token);
                    }
                }

                if (config.ProbeSimLifecycle)
                {
                    return await RunSimLifecycleProbeAsync(host, session, shutdown.Token);
                }

                if (config.ProbeStateInjection)
                {
                    return await RunStateInjectionProbeAsync(host, session, config, shutdown.Token);
                }

                if (config.ProbeActorLookupParity)
                {
                    return await RunActorLookupParityProbeAsync(host, session, shutdown.Token);
                }

                if (config.ProbeRoleSwitch)
                {
                    return await RunRoleSwitchProbeAsync(host, session, config, shutdown.Token);
                }

                if (config.ProbeMotiveRoundtrip)
                {
                    return await RunMotiveRoundtripProbeAsync(host, config, shutdown.Token);
                }

                if (config.ProbeInteractionRoundtrip)
                {
                    return await RunInteractionRoundtripProbeAsync(host, config, shutdown.Token);
                }

                if (config.ProbeBridgeResponseLoss)
                {
                    return await RunBridgeResponseLossProbeAsync(host, config, shutdown.Token);
                }

                if (config.ProbeCashRoundtrip)
                {
                    return await RunCashRoundtripProbeAsync(host, config, shutdown.Token);
                }

                if (config.ProbeSkillRoundtrip)
                {
                    return await RunSkillRoundtripProbeAsync(host, config, shutdown.Token);
            }

            if (config.ProbeAutonomyRoundtrip)
            {
                return await RunAutonomyRoundtripProbeAsync(host, config, shutdown.Token);
                }

                if (config.SnapshotAndExit)
                {
                    SimulationSnapshot snapshot = await session.GetSnapshotAsync(shutdown.Token);
                    PrintSnapshot(snapshot);
                    return 0;
                }

                if (config.ProbeAutonomy)
                {
                    return await RunAutonomyProbeAsync(host, config, shutdown.Token);
                }

                if (config.GoHere)
                {
                    return await RunGoHereProbeAsync(session, config, shutdown.Token);
                }

                if (config.ProbeOnly)
                {
                    Console.WriteLine("Authoritative session probe succeeded.");
                    Console.Out.Flush();
                    return 0;
                }

                if (config.ProbeLiveSnapshot)
                {
                    return await RunLiveSnapshotProbeAsync(host, session, config, shutdown.Token);
                }

                if (config.ProbeClaimAutonomy)
                {
                    return await RunClaimAutonomyProbeAsync(host, session, config, shutdown.Token);
                }

                if (config.ProbeClientMirrorStartup)
                {
                    return await RunClientMirrorStartupProbeAsync(host, session, config, shutdown.Token);
                }

                if (config.ProbeClientMirrorPosition)
                {
                    return await RunClientMirrorPositionProbeAsync(host, session, config, shutdown.Token);
                }

                // on re-attach the controller's claim/role maps start empty while the mod
                // retains role + autonomy suppression from the previous session — surface that
                // state so the operator knows what to rebuild (re-claim via simctl, or
                // ENABLE_AUTONOMY reset for orphaned suppression).
                if (config.AttachExistingHost && host is HeadlessSims3Host headlessForDiag)
                {
                    state?.Transition("reattach-diag");
                    try
                    {
                        string diagReport = await headlessForDiag.DiagStateAsync(shutdown.Token);
                        Console.WriteLine($"Re-attach mod state: {diagReport}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"WARNING: DIAG_STATE failed after attach (older mod build?): {ex.Message}");
                        telemetry.LogWarning("reattach.diag_failed");
                    }
                }

                // P3.0 (M3 Two-Process Coordination bootstrap): start the public client TCP listener
                // and broadcast authoritative events from the host bridge to every connected client.
                await using ClientListenerServer listener = new(
                    session,
                    config.ListenEndpoint,
                    (exception, commandType) => WriteSentryEventId(telemetry.ReportFailureOnce(
                        exception,
                        "server.command_failed",
                        new Dictionary<string, string?>
                        {
                            ["command_type"] = commandType,
                            ["validation_gate"] = "authoritative-command",
                        })),
                    command => telemetry.StartTransaction(
                        "server.command",
                        "server.command",
                        command.SentryTrace,
                        command.SentryBaggage));
                if (host is HeadlessSims3Host headlessForListener)
                {
                    headlessForListener.Bridge.AuthoritativeEvent += evt =>
                    {
                        listener.TryQueueAuthoritativeEvent(evt, shutdown.Token);
                    };
                }

                try
                {
                    await listener.StartAsync(shutdown.Token);
                    Console.WriteLine($"Public client listener bound at {listener.BoundEndpoint}.");
                    if (listener.ExposureWarning is { } exposure)
                    {
                        Console.Error.WriteLine(exposure);
                    }
                    state?.Transition("listener-bound", $"{listener.BoundEndpoint}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to bind public client listener at {config.ListenEndpoint}: {ex.Message}");
                    state?.Transition("error", $"listener bind failed: {ex.Message}");
                    WriteSentryEventId(telemetry.ReportFailure(
                        ex,
                        "listener.bind_failed",
                        new Dictionary<string, string?> { ["validation_gate"] = "public-listener" }));
                    return 1;
                }
                telemetry.AddBreadcrumb("listener.bound");

                // bridgectl: loopback admin surface for raw bridge commands (DIAG_* live
                // inspection). Serve mode only; off unless --bridgectl-listen is passed.
                await using BridgeAdminListener? adminListener =
                    config.BridgectlEndpoint != null && host is HeadlessSims3Host headlessForAdmin
                        ? new BridgeAdminListener(headlessForAdmin.Bridge, config.BridgectlEndpoint,
                            (name, _) => name == "PRESENTATION_SENT" ? listener.PresentationSent.Describe() : null)
                        : null;
                if (adminListener != null)
                {
                    try
                    {
                        await adminListener.StartAsync(shutdown.Token);
                        Console.WriteLine($"bridgectl admin listener bound at {adminListener.BoundEndpoint}.");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"WARNING: bridgectl listener failed to bind at {config.BridgectlEndpoint}: {ex.Message}");
                        telemetry.LogWarning("bridgectl.bind_failed");
                    }
                }

                // TS3 loads into live mode PAUSED; without this, queued interactions sit (or fail)
                // until someone sets a speed. Disable with --start-speed none.
                if (config.StartGameSpeed is { } startSpeed)
                {
                    CommandResult startSpeedResult = await session.HandleCommandAsync(
                        new ClientCommand.SetGameSpeed(Guid.NewGuid(), 0, startSpeed), shutdown.Token);
                    Console.WriteLine(startSpeedResult.IsAccepted
                        ? $"Start game speed applied: {startSpeed}."
                        : $"WARNING: start game speed {startSpeed} rejected: {startSpeedResult.RejectionReason}");
                }

                await using SyncBroadcaster? syncBroadcaster =
                    SyncBroadcaster.TryStart(config, session, listener, shutdown.Token);
                if (syncBroadcaster != null)
                {
                    // P5.6: wire the broadcaster so AckSnapshot can clear the resync-in-flight
                    // latch BEFORE RecordSnapshotAck (LP-5).
                    session.SetSyncBroadcaster(syncBroadcaster);
                    Console.WriteLine($"Sync broadcaster enabled: interval={config.SyncBroadcastInterval!.Value.TotalSeconds}s.");
                }

                Console.WriteLine("Authoritative session is running. Press Ctrl+C to stop.");
                Console.Out.Flush();
                state?.Transition("ready");
                telemetry.AddBreadcrumb("session.ready");
                await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
            }
            finally
            {
                state?.Transition("stopping");
                telemetry.AddBreadcrumb("session.stopping");
                using Observability.SimsOnlineTelemetryTransaction stopTransaction =
                    telemetry.StartTransaction("session.stop", Observability.SentryTracesSampler.OpSessionStop);
                try
                {
                    await session.StopAsync(CancellationToken.None);
                    stopTransaction.Complete(succeeded: true);
                    state?.Transition("stopped");
                    telemetry.AddBreadcrumb("session.stopped");
                }
                catch (Exception ex)
                {
                    WriteSentryEventId(telemetry.ReportFailureOnce(
                        ex,
                        "controller.unhandled"));
                    stopTransaction.Complete(succeeded: false);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (shutdown.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            else
            {
                Console.Error.WriteLine("Operation canceled before shutdown was requested.");
                state?.Transition("error", "canceled before shutdown was requested");
                WriteSentryEventId(telemetry.ReportFailure(
                    new InvalidOperationException("Operation canceled before shutdown was requested."),
                    "controller.unexpected_cancellation"));
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            state?.Transition("error", ex.Message);
            WriteSentryEventId(telemetry.ReportFailureOnce(ex, "controller.unhandled"));
            return 1;
        }

        return 0;
    }

    private static void WriteSentryEventId(SentryId eventId)
    {
        if (eventId != SentryId.Empty)
        {
            Console.Error.WriteLine($"Sentry event ID: {eventId}");
        }
    }

    private static void WireProbeEvents(IAuthoritativeSimulationHost host)
    {
        if (host is HeadlessSims3Host headless)
        {
            headless.Bridge.AuthoritativeEvent += evt =>
            {
                // timestamped (UTC, date, ms) so logq can place events on the shared clock.
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] EVENT: {evt}");
            };
        }
    }

    /// <summary>
    /// --bridge-trace: log every bridge diagnostic line (per-frame protocol trace from
    /// ProcessMemoryBridge plus chunk cadence) with a timestamp, so response loss can be
    /// pinned to a specific frame write/consume/displacement.
    /// </summary>
    private static void WireBridgeTrace(IAuthoritativeSimulationHost host, SessionConfig config)
    {
        if (host is not HeadlessSims3Host headless)
        {
            return;
        }

        if (config.BridgeTrace)
        {
            headless.Bridge.Diagnostic += line =>
            {
                // UTC with date + ms (was local time — 10h off the client log on this machine).
                Console.WriteLine($"[bridge {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {line}");
            };
        }

        // --bridge-record: lossless frame capture (truncated traces are unrecoverable).
        // Append mode + shared read so logq can tail it live; one lock — frames arrive from the
        // reader thread and command threads concurrently.
        if (config.BridgeRecordPath != null)
        {
            StreamWriter recorder = new(
                new FileStream(config.BridgeRecordPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false))
            { AutoFlush = true };
            object recorderLock = new();
            headless.Bridge.FrameRecord += line =>
            {
                lock (recorderLock)
                {
                    recorder.WriteLine($"[bridge {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {line}");
                }
            };
            Console.WriteLine($"Bridge frame recording to {config.BridgeRecordPath}.");
        }
    }

    /// <summary>
    /// repro probe: QUEUE_INTERACTION responses used to vanish when a mod-initiated
    /// EVENT line (typically InteractionFailed ~1s after queueing) overwrote the unread OK
    /// frame. This probe fires several player-paced GoHere commands and asserts every single
    /// one delivers a response (accepted or rejected — delivery is what is being tested) within
    /// the player-command timeout. Run with --bridge-trace to capture the frame-level evidence.
    /// </summary>
    private static async Task<int> RunBridgeResponseLossProbeAsync(IAuthoritativeSimulationHost host, SessionConfig config, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-bridge-response-loss is only supported with the standard Sims 3 host.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId!.Value;
        Vector3Dto target = new(config.GoHereX!.Value, config.GoHereY!.Value, config.GoHereZ!.Value);

        int queuedEvents = 0;
        int failedEvents = 0;
        headless.Bridge.AuthoritativeEvent += evt =>
        {
            if (evt is ServerEvent.InteractionQueued)
            {
                Interlocked.Increment(ref queuedEvents);
            }
            else if (evt is ServerEvent.InteractionFailed)
            {
                Interlocked.Increment(ref failedEvents);
            }
        };

        // TS3 loads into live mode PAUSED; interactions need a running clock to start (and to
        // fail — the ~1s InteractionFailed after queueing is part of the repro).
        CommandResult speedResult = await headless.ApplyCommandAsync(
            new ClientCommand.SetGameSpeed(Guid.NewGuid(), 0, GameSpeed.Normal), cancellationToken);
        Console.WriteLine($"Set game speed Normal: accepted={speedResult.IsAccepted}");

        const int iterations = 5;
        int delivered = 0;
        for (int i = 1; i <= iterations; i++)
        {
            InteractionRequest request = new(simId, "Terrain.GoHere", null, target, new Dictionary<string, string>());
            ClientCommand.QueueInteraction command = new(Guid.NewGuid(), 0, request);
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                CommandResult result = await headless.ApplyCommandAsync(command, cancellationToken);
                stopwatch.Stop();
                delivered++;
                Console.WriteLine(FormattableString.Invariant(
                    $"[{i}/{iterations}] response delivered in {stopwatch.ElapsedMilliseconds}ms accepted={result.IsAccepted}{(result.IsAccepted ? "" : $" reason=\"{result.RejectionReason}\"")}"));
            }
            catch (TimeoutException ex)
            {
                stopwatch.Stop();
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"[{i}/{iterations}] RESPONSE LOST after {stopwatch.ElapsedMilliseconds}ms: {ex.Message}"));
            }

            // The old loss window was a spontaneous mod line landing while no command was in
            // flight (or just after the OK was written). Give the ~1s InteractionFailed spam a
            // chance to fire between iterations so the repro covers it.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        Console.WriteLine(FormattableString.Invariant(
            $"Bridge response-loss probe: delivered={delivered}/{iterations} interactionQueuedEvents={queuedEvents} interactionFailedEvents={failedEvents}"));

        if (delivered < iterations)
        {
            Console.Error.WriteLine("Bridge response-loss probe FAILED: at least one command response was lost or timed out.");
            return 1;
        }

        Console.WriteLine("Bridge response-loss probe PASSED.");
        Console.Out.Flush();
        return 0;
    }

    private static void PrintSnapshot(SimulationSnapshot snapshot)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"Snapshot: tick={snapshot.Tick} world=\"{snapshot.WorldName}\" save=\"{snapshot.SaveName}\" speed={snapshot.Speed} sims={snapshot.Sims.Count} objects={snapshot.Objects.Count}"));

        foreach (SimSnapshot sim in snapshot.Sims.Take(5))
        {
            Console.WriteLine(FormattableString.Invariant(
                $"SIM 0x{sim.ObjectId:X16} \"{sim.Name}\" household=0x{sim.HouseholdId:X16} pos=({sim.Position.X:R},{sim.Position.Y:R},{sim.Position.Z:R}) interaction={sim.CurrentInteractionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} owner={sim.OwningPlayerId?.ToString(CultureInfo.InvariantCulture) ?? "-"}"));
        }

        foreach (ObjectSnapshot obj in snapshot.Objects.Take(5))
        {
            Console.WriteLine(FormattableString.Invariant(
                $"OBJ 0x{obj.ObjectId:X16} {obj.ScriptClass} lot={(obj.LotId.HasValue ? $"0x{obj.LotId.Value:X16}" : "-")} pos=({obj.Position.X:R},{obj.Position.Y:R},{obj.Position.Z:R})"));
        }
    }

    private static async Task<int> RunObjectGuidStabilityProbeAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        Console.WriteLine($"ObjectGuid stability sample size: {config.ObjectGuidSampleSize}");
        Console.WriteLine("Pass 1: launching TS3, loading save, capturing baseline identities, and saving checkpoint...");
        SnapshotIdentitySelection baseline = await CaptureBaselineAndSaveInFreshHostAsync(config, cancellationToken);
        if (!baseline.HasRequiredSampleSize)
        {
            Console.Error.WriteLine(
                $"ObjectGuid stability probe failed: requested {baseline.RequestedSampleSize} Sims/objects but selected {baseline.Sims.Count} Sims and {baseline.Objects.Count} objects.");
            return 1;
        }

        Console.WriteLine("Pass 2: restarting TS3, reloading the checkpointed save, and comparing identities...");
        SimulationSnapshot reloadSnapshot = await LoadSnapshotInFreshHostAsync(config, cancellationToken);
        SnapshotIdentityComparison comparison = SnapshotIdentityProbe.Compare(baseline, reloadSnapshot);
        PrintIdentityComparison(comparison);

        if (!comparison.AllStable)
        {
            Console.Error.WriteLine("ObjectGuid stability probe failed: one or more selected identities changed or disappeared.");
            return 1;
        }

        Console.WriteLine("ObjectGuid stability probe passed.");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<int> RunSimLifecycleProbeAsync(IAuthoritativeSimulationHost host, AuthoritativeSession session, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-sim-lifecycle is only supported with the standard Sims 3 host.");
            return 1;
        }

        Console.WriteLine("Requesting first snapshot to capture first-snapshot lifecycle sample...");
        SimulationSnapshot snapshot = await session.GetSnapshotAsync(cancellationToken);
        Console.WriteLine(FormattableString.Invariant(
            $"First snapshot: tick={snapshot.Tick} sims={snapshot.Sims.Count} objects={snapshot.Objects.Count}"));

        string rawReport = await headless.ProbeSimLifecycleAsync(cancellationToken);
        Console.WriteLine($"SIM_LIFECYCLE raw: {rawReport}");

        SimLifecycleProbeReport report;
        try
        {
            report = SimLifecycleProbeReport.Parse(rawReport);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"Sim lifecycle probe failed: host report could not be parsed: {ex.Message}");
            return 1;
        }

        PrintSimLifecycleReport(report);

        if (!report.WorldLoad.Seen)
        {
            Console.Error.WriteLine("Sim lifecycle probe failed: OnWorldLoadFinished was not observed.");
            return 1;
        }

        if (!report.FirstSnapshotMatchesControllerSnapshot(snapshot.Sims.Count))
        {
            Console.Error.WriteLine("Sim lifecycle probe failed: host first-snapshot Sim count does not match the decoded controller snapshot.");
            return 1;
        }

        if (!report.ReadyHookMatchesFirstSnapshot)
        {
            Console.Error.WriteLine("Sim lifecycle probe failed: kLoadingScreenDisposed did not match the first snapshot Sim count.");
            return 1;
        }

        if (report.WorldLoad.SnapshotSims != report.FirstSnapshotRequest.SnapshotSims)
        {
            Console.WriteLine("OnWorldLoadFinished differs from first snapshot; prefer kLoadingScreenDisposed/first snapshot for authoritative snapshot readiness.");
        }

        Console.WriteLine("Sim lifecycle probe passed.");
        Console.Out.Flush();
        return 0;
    }

    private static void PrintSimLifecycleReport(SimLifecycleProbeReport report)
    {
        Console.WriteLine($"Enumeration API: {report.Api}");
        PrintSimLifecycleSample("OnWorldLoadFinished", report.WorldLoad);
        PrintSimLifecycleSample("kLoadingScreenDisposed", report.LoadingScreenDisposed);
        PrintSimLifecycleSample("FirstSnapshotRequest", report.FirstSnapshotRequest);
        PrintSimLifecycleSample("Current", report.Current);
    }

    private static void PrintSimLifecycleSample(string label, SimLifecycleSample sample)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"{label}: seen={sample.Seen} tick={sample.Tick} actors={sample.Actors} visible={sample.VisibleActors} snapshotSims={sample.SnapshotSims}"));
    }

    private static async Task<int> RunMotiveRoundtripProbeAsync(IAuthoritativeSimulationHost host, SessionConfig config, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-motive-roundtrip is only supported with the standard Sims 3 host.");
            return 1;
        }

        if (config.ProbeSimObjectId is null || config.MotiveName is null || config.MotiveValue is null)
        {
            Console.Error.WriteLine("--probe-motive-roundtrip requires --sim <objectId> --motive <name> --motive-value <float>.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId.Value;
        string motiveName = config.MotiveName;
        float targetValue = config.MotiveValue.Value;

        Console.WriteLine(FormattableString.Invariant($"Reading baseline motive {motiveName} for sim {simId} (0x{simId:X16})..."));
        float baseline = await headless.ProbeSimMotiveAsync(simId, motiveName, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"Baseline {motiveName}={baseline:F3}"));

        Console.WriteLine("Switching to Client role...");
        await headless.SetRoleAsync(HostRole.Client, cancellationToken);

        Console.WriteLine(FormattableString.Invariant($"Sending APPLY_MOTIVE {motiveName}={targetValue:F3}..."));
        await headless.ApplyMotiveAsync(simId, motiveName, targetValue, cancellationToken);

        Console.WriteLine("Switching back to Server role (cleanup)...");
        await headless.SetRoleAsync(HostRole.Server, cancellationToken);

        Console.WriteLine("Reading verification motive...");
        float verify = await headless.ProbeSimMotiveAsync(simId, motiveName, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"Verify {motiveName}={verify:F3}"));

        float distance = Math.Abs(verify - targetValue);
        Console.WriteLine(FormattableString.Invariant($"Distance from target: {distance:F3}"));

        const float MaxAcceptableDistance = 1.0f;
        if (distance > MaxAcceptableDistance)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"Motive roundtrip probe FAILED: verify value {verify:F3} differs from target {targetValue:F3} by {distance:F3} (max acceptable {MaxAcceptableDistance})."));
            return 1;
        }

        Console.WriteLine("Motive roundtrip probe PASSED.");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<int> RunInteractionRoundtripProbeAsync(IAuthoritativeSimulationHost host, SessionConfig config, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-interaction-roundtrip is only supported with the standard Sims 3 host.");
            return 1;
        }

        if (config.ProbeSimObjectId is null || config.GoHereX is null || config.GoHereY is null || config.GoHereZ is null)
        {
            Console.Error.WriteLine("--probe-interaction-roundtrip requires --sim <objectId> --x <x> --y <y> --z <z>.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId.Value;
        Vector3Dto target = new(config.GoHereX.Value, config.GoHereY.Value, config.GoHereZ.Value);

        Console.WriteLine(FormattableString.Invariant($"Reading baseline interaction queue for sim {simId} (0x{simId:X16})..."));
        InteractionQueueProbeReport baseline = await headless.ProbeSimInteractionQueueAsync(simId, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"Baseline: queueCount={baseline.QueueCount} current=\"{baseline.CurrentKey}\""));

        // S2 + assert role gate rejects APPLY_INTERACTION_QUEUE in default Server role.
        Console.WriteLine("S2 negative check: attempting APPLY in default Server role (expect REJECT)...");
        bool roleRejected = false;
        try
        {
            await headless.ApplyInteractionQueueAsync(simId, "Terrain.GoHere", null, target, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("role is 'server'", StringComparison.Ordinal))
        {
            roleRejected = true;
            Console.WriteLine("S2 PASS: APPLY rejected by role gate: " + ex.Message);
        }
        if (!roleRejected)
        {
            Console.Error.WriteLine("S2 FAILED: APPLY_INTERACTION_QUEUE was not rejected in Server role; role gate is broken.");
            return 1;
        }

        Console.WriteLine("Switching to Client role...");
        await headless.SetRoleAsync(HostRole.Client, cancellationToken);

        Console.WriteLine(FormattableString.Invariant($"Sending APPLY_INTERACTION_QUEUE Terrain.GoHere -> ({target.X:R}, {target.Y:R}, {target.Z:R})..."));
        await headless.ApplyInteractionQueueAsync(simId, "Terrain.GoHere", null, target, cancellationToken);

        Console.WriteLine("Switching back to Server role (cleanup)...");
        await headless.SetRoleAsync(HostRole.Server, cancellationToken);

        Console.WriteLine("Probing verification interaction queue...");
        InteractionQueueProbeReport verify = await headless.ProbeSimInteractionQueueAsync(simId, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"Verify: queueCount={verify.QueueCount} current=\"{verify.CurrentKey}\""));

        if (verify.QueueCount < 1)
        {
            Console.Error.WriteLine("Interaction roundtrip probe FAILED: no interaction queued after APPLY.");
            return 1;
        }

        if (!verify.HasQueuedInteraction("GoHere"))
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"Interaction roundtrip probe FAILED: current interaction is '{verify.CurrentKey}', expected to contain 'GoHere'."));
            return 1;
        }

        Console.WriteLine("Interaction roundtrip probe PASSED.");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<int> RunCashRoundtripProbeAsync(IAuthoritativeSimulationHost host, SessionConfig config, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-cash-roundtrip is only supported with the standard Sims 3 host.");
            return 1;
        }

        if (config.ProbeHouseholdId is null || config.CashAmount is null)
        {
            Console.Error.WriteLine("--probe-cash-roundtrip requires --household <id> --funds <int>.");
            return 1;
        }

        ulong householdId = config.ProbeHouseholdId.Value;
        int targetFunds = config.CashAmount.Value;

        Console.WriteLine(FormattableString.Invariant($"Reading baseline cash for household {householdId} (0x{householdId:X16})..."));
        HouseholdCashProbeReport baseline = await headless.ProbeHouseholdCashAsync(householdId, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"Baseline: household={baseline.HouseholdId} funds={baseline.Funds}"));

        // S2 + role gate must reject in default Server role.
        Console.WriteLine("S2 negative check: attempting APPLY in default Server role (expect REJECT)...");
        bool rejected = false;
        try
        {
            await headless.ApplyCashAsync(householdId, targetFunds, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("role is 'server'", StringComparison.Ordinal))
        {
            rejected = true;
            Console.WriteLine("S2 PASS: APPLY rejected by role gate: " + ex.Message);
        }
        if (!rejected)
        {
            Console.Error.WriteLine("S2 FAILED: APPLY_CASH was not rejected in Server role; role gate is broken.");
            return 1;
        }

        Console.WriteLine("Switching to Client role...");
        await headless.SetRoleAsync(HostRole.Client, cancellationToken);

        Console.WriteLine(FormattableString.Invariant($"Sending APPLY_CASH funds={targetFunds}..."));
        await headless.ApplyCashAsync(householdId, targetFunds, cancellationToken);

        Console.WriteLine("Switching back to Server role (cleanup)...");
        await headless.SetRoleAsync(HostRole.Server, cancellationToken);

        Console.WriteLine("Reading verification cash...");
        HouseholdCashProbeReport verify = await headless.ProbeHouseholdCashAsync(householdId, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"Verify: household={verify.HouseholdId} funds={verify.Funds}"));

        int distance = Math.Abs(verify.Funds - targetFunds);
        Console.WriteLine(FormattableString.Invariant($"Distance from target: {distance}"));

        const int MaxAcceptableDistance = 1;
        if (distance > MaxAcceptableDistance)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"Cash roundtrip probe FAILED: verify funds {verify.Funds} differs from target {targetFunds} by {distance} (max acceptable {MaxAcceptableDistance})."));
            return 1;
        }

        Console.WriteLine("Cash roundtrip probe PASSED.");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<int> RunSkillRoundtripProbeAsync(IAuthoritativeSimulationHost host, SessionConfig config, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-skill-roundtrip is only supported with the standard Sims 3 host.");
            return 1;
        }

        if (config.ProbeSimObjectId is null || config.SkillName is null || config.SkillLevel is null)
        {
            Console.Error.WriteLine("--probe-skill-roundtrip requires --sim <objectId> --skill <name> --skill-level <int>.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId.Value;
        string skillName = config.SkillName;
        int targetLevel = config.SkillLevel.Value;

        Console.WriteLine(FormattableString.Invariant($"Reading baseline skill {skillName} for sim {simId} (0x{simId:X16})..."));
        SimSkillProbeReport baseline = await headless.ProbeSimSkillAsync(simId, skillName, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"Baseline: sim={baseline.SimObjectId} skill={baseline.SkillName} level={baseline.Level} points={baseline.Points:F2}"));

        // S2 negative role-gate check
        Console.WriteLine("S2 negative check: attempting APPLY in default Server role (expect REJECT)...");
        bool rejected = false;
        try
        {
            await headless.ApplySkillAsync(simId, skillName, targetLevel, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("role is 'server'", StringComparison.Ordinal))
        {
            rejected = true;
            Console.WriteLine("S2 PASS: APPLY rejected by role gate: " + ex.Message);
        }
        if (!rejected)
        {
            Console.Error.WriteLine("S2 FAILED: APPLY_SKILL was not rejected in Server role; role gate is broken.");
            return 1;
        }

        Console.WriteLine("Switching to Client role...");
        await headless.SetRoleAsync(HostRole.Client, cancellationToken);

        Console.WriteLine(FormattableString.Invariant($"Sending APPLY_SKILL {skillName} level={targetLevel}..."));
        await headless.ApplySkillAsync(simId, skillName, targetLevel, cancellationToken);

        Console.WriteLine("Switching back to Server role (cleanup)...");
        await headless.SetRoleAsync(HostRole.Server, cancellationToken);

        Console.WriteLine("Reading verification skill...");
        SimSkillProbeReport verify = await headless.ProbeSimSkillAsync(simId, skillName, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"Verify: sim={verify.SimObjectId} skill={verify.SkillName} level={verify.Level} points={verify.Points:F2}"));

        if (verify.Level < targetLevel)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"Skill roundtrip probe FAILED: verify level {verify.Level} less than target {targetLevel}."));
            return 1;
        }

        Console.WriteLine("Skill roundtrip probe PASSED.");
        Console.Out.Flush();
        return 0;
    }

    // M4 P4.3: autonomy roundtrip orchestrator. Baseline probe -> S2 negative role-gate check
    // in default Server role -> switch to Client -> APPLY disabled=true -> verify enabled=false ->
    // APPLY disabled=false -> verify enabled=true -> switch back to Server -> exit 0.
    private static async Task<int> RunAutonomyRoundtripProbeAsync(IAuthoritativeSimulationHost host, SessionConfig config, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-autonomy-roundtrip is only supported with the standard Sims 3 host.");
            return 1;
        }

        if (config.ProbeSimObjectId is null)
        {
            Console.Error.WriteLine("--probe-autonomy-roundtrip requires --sim <objectId>.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId.Value;

        Console.WriteLine(FormattableString.Invariant($"Reading baseline autonomy for sim {simId} (0x{simId:X16})..."));
        SimAutonomyProbeReport baseline = await headless.ProbeSimAutonomyAsync(simId, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"Baseline: sim={baseline.SimObjectId} enabled={baseline.Enabled} disabledCount={baseline.DisabledCount}"));

        // S2 negative role-gate check
        Console.WriteLine("S2 negative check: attempting APPLY_AUTONOMY in default Server role (expect REJECT)...");
        bool rejected = false;
        try
        {
            await headless.ApplyAutonomyAsync(simId, true, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("role is 'server'", StringComparison.Ordinal))
        {
            rejected = true;
            Console.WriteLine("S2 PASS: APPLY rejected by role gate: " + ex.Message);
        }
        if (!rejected)
        {
            Console.Error.WriteLine("S2 FAILED: APPLY_AUTONOMY was not rejected in Server role; role gate is broken.");
            return 1;
        }

        Console.WriteLine("Switching to Client role...");
        await headless.SetRoleAsync(HostRole.Client, cancellationToken);

        // S1.1 APPLY disabled=true
        Console.WriteLine("Sending APPLY_AUTONOMY disabled=true...");
        await headless.ApplyAutonomyAsync(simId, true, cancellationToken);
        SimAutonomyProbeReport afterDisable = await headless.ProbeSimAutonomyAsync(simId, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"After disable: enabled={afterDisable.Enabled} disabledCount={afterDisable.DisabledCount}"));
        if (afterDisable.Enabled)
        {
            Console.Error.WriteLine("S1.1 FAILED: APPLY_AUTONOMY disabled=true did not flip enabled to false.");
            await headless.SetRoleAsync(HostRole.Server, cancellationToken);
            return 1;
        }

        // S1.2 APPLY disabled=false (restore)
        Console.WriteLine("Sending APPLY_AUTONOMY disabled=false (restore)...");
        await headless.ApplyAutonomyAsync(simId, false, cancellationToken);
        SimAutonomyProbeReport afterEnable = await headless.ProbeSimAutonomyAsync(simId, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"After enable: enabled={afterEnable.Enabled} disabledCount={afterEnable.DisabledCount}"));
        if (!afterEnable.Enabled)
        {
            Console.Error.WriteLine("S1.2 FAILED: APPLY_AUTONOMY disabled=false did not flip enabled back to true.");
            await headless.SetRoleAsync(HostRole.Server, cancellationToken);
            return 1;
        }

        Console.WriteLine("Switching back to Server role (cleanup)...");
        await headless.SetRoleAsync(HostRole.Server, cancellationToken);

        Console.WriteLine("Autonomy roundtrip probe PASSED (S1 happy + S2 negative role gate).");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<int> RunRoleSwitchProbeAsync(IAuthoritativeSimulationHost host, AuthoritativeSession session, SessionConfig config, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-role-switch is only supported with the standard Sims 3 host.");
            return 1;
        }

        if (config.ProbeSimObjectId is null || config.GoHereX is null || config.GoHereY is null || config.GoHereZ is null)
        {
            Console.Error.WriteLine("--probe-role-switch requires --sim <objectId> --x <x> --y <y> --z <z>.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId.Value;
        Vector3Dto target = new(config.GoHereX.Value, config.GoHereY.Value, config.GoHereZ.Value);

        Console.WriteLine("Capturing baseline snapshot to establish Sim's pre-APPLY position...");
        SimulationSnapshot baseline = await session.GetSnapshotAsync(cancellationToken);
        SimSnapshot? baselineSim = baseline.Sims.FirstOrDefault(s => s.ObjectId == simId);
        if (baselineSim is null)
        {
            Console.Error.WriteLine(FormattableString.Invariant($"Sim {simId} (0x{simId:X16}) not in baseline snapshot."));
            return 1;
        }
        Console.WriteLine(FormattableString.Invariant($"Baseline: sim={baselineSim.Name} pos=({baselineSim.Position.X:F2}, {baselineSim.Position.Y:F2}, {baselineSim.Position.Z:F2})"));

        Console.WriteLine("Switching script DLL role: Server -> Client...");
        await headless.SetRoleAsync(HostRole.Client, cancellationToken);
        Console.WriteLine("Role set to Client. Sending APPLY_POSITION...");

        await headless.ApplyPositionAsync(simId, target, cancellationToken);
        Console.WriteLine(FormattableString.Invariant($"APPLY_POSITION accepted. Target=({target.X:R}, {target.Y:R}, {target.Z:R})"));

        Console.WriteLine("Switching script DLL role back: Client -> Server (cleanup)...");
        await headless.SetRoleAsync(HostRole.Server, cancellationToken);

        Console.WriteLine("Capturing verification snapshot...");
        SimulationSnapshot verify = await session.GetSnapshotAsync(cancellationToken);
        SimSnapshot? verifySim = verify.Sims.FirstOrDefault(s => s.ObjectId == simId);
        if (verifySim is null)
        {
            Console.Error.WriteLine(FormattableString.Invariant($"Sim {simId} disappeared from verification snapshot."));
            return 1;
        }
        Console.WriteLine(FormattableString.Invariant($"Verify: pos=({verifySim.Position.X:F2}, {verifySim.Position.Y:F2}, {verifySim.Position.Z:F2})"));

        float dx = verifySim.Position.X - target.X;
        float dy = verifySim.Position.Y - target.Y;
        float dz = verifySim.Position.Z - target.Z;
        float distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        Console.WriteLine(FormattableString.Invariant($"Distance from target after APPLY: {distance:F3} units"));

        const float MaxAcceptableDistance = 3.0f;
        if (distance > MaxAcceptableDistance)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"Role switch probe FAILED: Sim is {distance:F3} units from target (max acceptable {MaxAcceptableDistance})."));
            return 1;
        }

        Console.WriteLine("Role switch probe PASSED.");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<int> RunActorLookupParityProbeAsync(IAuthoritativeSimulationHost host, AuthoritativeSession session, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-actor-lookup-parity is only supported with the standard Sims 3 host.");
            return 1;
        }

        Console.WriteLine("Requesting first snapshot to capture LotManager.Actors size for parity comparison...");
        SimulationSnapshot snapshot = await session.GetSnapshotAsync(cancellationToken);
        Console.WriteLine(FormattableString.Invariant(
            $"Snapshot: tick={snapshot.Tick} sims={snapshot.Sims.Count} objects={snapshot.Objects.Count}"));

        string rawReport = await headless.ProbeActorLookupParityAsync(cancellationToken);
        Console.WriteLine($"ACTOR_LOOKUP_PARITY raw: {rawReport}");

        ActorLookupParityReport report;
        try
        {
            report = ActorLookupParityReport.Parse(rawReport);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"Actor-lookup parity probe failed: host report could not be parsed: {ex.Message}");
            return 1;
        }

        Console.WriteLine(FormattableString.Invariant(
            $"LOOKUP_PARITY: total={report.Total} resolved={report.Resolved} failed={report.Failed}"));

        if (!report.IsParity)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"Actor-lookup parity probe FAILED: {report.Failed} of {report.Total} Sims could not be resolved by FindSim."));
            int show = Math.Min(report.FailingObjectIds.Count, 10);
            for (int i = 0; i < show; i++)
            {
                ulong id = report.FailingObjectIds[i];
                Console.Error.WriteLine(FormattableString.Invariant($"  failing[{i}] = {id} (0x{id:X16})"));
            }
            return 1;
        }

        if (report.Total != snapshot.Sims.Count)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"Note: host LotManager.Actors total ({report.Total}) differs from snapshot Sim count ({snapshot.Sims.Count}). Snapshot may have skipped some via per-iteration try/catch; the parity invariant still holds for what FindSim sees."));
        }

        Console.WriteLine("Actor-lookup parity probe PASSED.");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<int> RunStateInjectionProbeAsync(
        IAuthoritativeSimulationHost host,
        AuthoritativeSession session,
        SessionConfig config,
        CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-state-injection is only supported with the standard Sims 3 host.");
            return 1;
        }

        if (config.ProbeSimObjectId is null || config.GoHereX is null || config.GoHereY is null || config.GoHereZ is null)
        {
            Console.Error.WriteLine("--probe-state-injection requires --sim <objectId> --x <x> --y <y> --z <z>.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId.Value;
        Vector3Dto target = new(config.GoHereX.Value, config.GoHereY.Value, config.GoHereZ.Value);

        SimulationSnapshot beforeSnapshot = await session.GetSnapshotAsync(cancellationToken);
        SimSnapshot? beforeSim = FindSimSnapshot(beforeSnapshot, simId);
        if (beforeSim == null)
        {
            Console.Error.WriteLine($"State injection probe failed: sim 0x{simId:X16} was not present in the initial snapshot.");
            return 1;
        }

        double initialDistance = Distance(beforeSim.Position, target);
        Console.WriteLine(FormattableString.Invariant(
            $"State injection initial: sim=0x{simId:X16} name=\"{beforeSim.Name}\" pos={FormatVector(beforeSim.Position)} target={FormatVector(target)} distance={initialDistance:R} interaction={beforeSim.CurrentInteractionId?.ToString(CultureInfo.InvariantCulture) ?? "-"}"));
        if (initialDistance < 2.0d)
        {
            Console.Error.WriteLine("State injection probe failed: target is too close to the Sim's initial position to prove a correction.");
            return 1;
        }

        CommandResult speedResult = await session.HandleCommandAsync(new ClientCommand.SetGameSpeed(Guid.NewGuid(), 0, GameSpeed.Speed3), cancellationToken);
        if (!speedResult.IsAccepted)
        {
            Console.Error.WriteLine($"Could not set game speed to Speed3: {speedResult.RejectionReason}");
            return 1;
        }

        InteractionRequest request = new(simId, "Terrain.GoHere", null, target, new Dictionary<string, string>());
        CommandResult goHereResult = await session.HandleCommandAsync(new ClientCommand.QueueInteraction(Guid.NewGuid(), 0, request), cancellationToken);
        if (!goHereResult.IsAccepted)
        {
            Console.Error.WriteLine($"GoHere rejected before state injection: {goHereResult.RejectionReason}");
            return 1;
        }

        Console.WriteLine("GoHere accepted. Waiting 5 s before direct position correction...");
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        string rawReport = await headless.ProbeStateInjectionAsync(simId, target, cancellationToken);
        Console.WriteLine($"STATE_INJECTION raw: {rawReport}");

        StateInjectionProbeReport report;
        try
        {
            report = StateInjectionProbeReport.Parse(rawReport);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"State injection probe failed: host report could not be parsed: {ex.Message}");
            return 1;
        }

        PrintStateInjectionReport(report);
        if (report.SimObjectId != simId)
        {
            Console.Error.WriteLine("State injection probe failed: host report returned a different Sim id.");
            return 1;
        }

        if (!report.AfterPositionMatchesTarget(2.0f))
        {
            Console.Error.WriteLine("State injection probe failed: immediate host position did not land within 2 m of the correction target.");
            return 1;
        }

        Console.WriteLine("Waiting 10 s after injection to observe route/engine survival...");
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

        SimulationSnapshot postSnapshot = await session.GetSnapshotAsync(cancellationToken);
        SimSnapshot? postSim = FindSimSnapshot(postSnapshot, simId);
        if (postSim == null)
        {
            Console.Error.WriteLine("State injection probe failed: post-injection snapshot no longer contains the Sim.");
            return 1;
        }

        double postDistance = Distance(postSim.Position, target);
        Console.WriteLine(FormattableString.Invariant(
            $"State injection post-snapshot: tick={postSnapshot.Tick} sim=0x{simId:X16} pos={FormatVector(postSim.Position)} distance={postDistance:R} interaction={postSim.CurrentInteractionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} sims={postSnapshot.Sims.Count} objects={postSnapshot.Objects.Count}"));
        if (postDistance > 2.0d)
        {
            Console.Error.WriteLine("State injection probe failed: post-observation snapshot drifted away from the injected target.");
            return 1;
        }

        Console.WriteLine("State injection probe passed: direct correction landed, TS3 stayed alive, and post-injection snapshot still contains the Sim.");
        Console.Out.Flush();
        return 0;
    }

    private static void PrintStateInjectionReport(StateInjectionProbeReport report)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"State injection report: method={report.Method} sim=0x{report.SimObjectId:X16} before={FormatVector(report.Before)} target={FormatVector(report.Target)} after={FormatVector(report.After)} distance={report.DistanceToTarget:R} beforeInteraction={report.BeforeInteractionId} afterInteraction={report.AfterInteractionId}"));
    }

    private static SimSnapshot? FindSimSnapshot(SimulationSnapshot snapshot, ulong simId) =>
        snapshot.Sims.FirstOrDefault(sim => sim.ObjectId == simId);

    private static double Distance(Vector3Dto left, Vector3Dto right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        double z = left.Z - right.Z;
        return Math.Sqrt((x * x) + (y * y) + (z * z));
    }

    private static string FormatVector(Vector3Dto value) =>
        FormattableString.Invariant($"({value.X:R},{value.Y:R},{value.Z:R})");

    private static async Task<SnapshotIdentitySelection> CaptureBaselineAndSaveInFreshHostAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        await using IAuthoritativeSimulationHost host = new HeadlessSims3Host(config);
        AuthoritativeSession session = new(host);
        try
        {
            await session.StartAsync(config.SaveName, cancellationToken);
            SimulationSnapshot baselineSnapshot = await session.GetSnapshotAsync(cancellationToken);
            SnapshotIdentitySelection baseline = SnapshotIdentityProbe.Select(baselineSnapshot, config.ObjectGuidSampleSize);
            PrintIdentitySelection("Baseline", baseline);
            if (!baseline.HasRequiredSampleSize)
            {
                return baseline;
            }

            Console.WriteLine($"SAVE_GAME: saving checkpoint \"{config.SaveName}\"...");
            await session.SaveGameAsync(config.SaveName, cancellationToken);
            Console.WriteLine("SAVE_GAME accepted/completed.");
            return baseline;
        }
        finally
        {
            await session.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<SimulationSnapshot> LoadSnapshotInFreshHostAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        await using IAuthoritativeSimulationHost host = new HeadlessSims3Host(config);
        AuthoritativeSession session = new(host);
        try
        {
            await session.StartAsync(config.SaveName, cancellationToken);
            return await session.GetSnapshotAsync(cancellationToken);
        }
        finally
        {
            await session.StopAsync(CancellationToken.None);
        }
    }

    private static void PrintIdentitySelection(string label, SnapshotIdentitySelection selection)
    {
        Console.WriteLine($"{label}: selected sims={selection.Sims.Count} objects={selection.Objects.Count}");
        foreach (SimIdentity sim in selection.Sims)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"BASE SIM 0x{sim.ObjectId:X16} \"{sim.Name}\" household=0x{sim.HouseholdId:X16} pos=({sim.Position.X:R},{sim.Position.Y:R},{sim.Position.Z:R})"));
        }

        foreach (ObjectIdentity obj in selection.Objects)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"BASE OBJ 0x{obj.ObjectId:X16} {obj.Label}"));
        }
    }

    private static void PrintIdentityComparison(SnapshotIdentityComparison comparison)
    {
        foreach (IdentityMatch sim in comparison.Sims)
        {
            PrintIdentityMatch("SIM", sim);
        }

        foreach (IdentityMatch obj in comparison.Objects)
        {
            PrintIdentityMatch("OBJ", obj);
        }
    }

    private static void PrintIdentityMatch(string kind, IdentityMatch match)
    {
        string current = match.CurrentObjectId?.ToString("X16", CultureInfo.InvariantCulture) ?? "MISSING";
        string status = match.IsStable ? "STABLE" : match.IsMissing ? "MISSING" : "CHANGED";
        Console.WriteLine(FormattableString.Invariant(
            $"{kind} {status}: \"{match.Label}\" baseline=0x{match.BaselineObjectId:X16} current={(match.IsMissing ? current : "0x" + current)}"));
    }

    private static async Task<int> RunLoadPathComparisonProbeAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Load path comparison: comparing direct (force=true) vs menu (LoadAndTransitionToSaveGame) for \"{config.SaveName}\".");

        Console.WriteLine("PATH A (direct): launching fresh TS3 host and loading via direct GameStates.TransitionToGameStateToInWorld...");
        LoadPathOutcome direct = await RunSingleLoadPathAsync(config, useMenu: false, "direct", cancellationToken);
        PrintLoadPathOutcome(direct);

        Console.WriteLine("PATH B (menu): launching fresh TS3 host and loading via Responder.MainMenuModel.LoadAndTransitionToSaveGame...");
        LoadPathOutcome menu = await RunSingleLoadPathAsync(config, useMenu: true, "menu", cancellationToken);
        PrintLoadPathOutcome(menu);

        LoadPathComparisonReport report = new(direct, menu);
        PrintLoadPathComparisonReport(report);

        if (!report.IsEquivalent)
        {
            Console.Error.WriteLine("Load path comparison probe FAILED: paths produced divergent outcomes.");
            foreach (string divergence in report.Divergences())
            {
                Console.Error.WriteLine($"  - {divergence}");
            }

            return 1;
        }

        Console.WriteLine("Load path comparison probe PASSED: both paths reached SESSION_READY with equivalent world state.");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<LoadPathOutcome> RunSingleLoadPathAsync(SessionConfig config, bool useMenu, string label, CancellationToken cancellationToken)
    {
        await using IAuthoritativeSimulationHost host = new HeadlessSims3Host(config);
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await host.StartAsync(cancellationToken);
            if (useMenu)
            {
                await host.LoadSaveViaMenuAsync(config.SaveName, cancellationToken);
            }
            else
            {
                await host.LoadSaveAsync(config.SaveName, cancellationToken);
            }

            stopwatch.Stop();
            SimulationSnapshot snapshot = await host.GetSnapshotAsync(cancellationToken);
            return LoadPathOutcome.FromSnapshot(label, snapshot, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return LoadPathOutcome.Failure(label, ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            catch
            {
                // Cleanup failure should not mask the primary outcome.
            }
        }
    }

    private static void PrintLoadPathOutcome(LoadPathOutcome outcome)
    {
        if (outcome.Success)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"PATH {outcome.Label} OK: elapsed={outcome.Elapsed.TotalSeconds:F2}s tick={outcome.Tick} sims={outcome.SimCount} objects={outcome.ObjectCount} world=\"{outcome.WorldName}\" save=\"{outcome.SaveName}\""));
        }
        else
        {
            Console.WriteLine(FormattableString.Invariant(
                $"PATH {outcome.Label} FAIL: elapsed={outcome.Elapsed.TotalSeconds:F2}s error=\"{outcome.Error}\""));
        }
    }

    private static void PrintLoadPathComparisonReport(LoadPathComparisonReport report)
    {
        Console.WriteLine($"Comparison: {report.ElapsedReport()}");
        if (report.IsEquivalent)
        {
            Console.WriteLine("Comparison: EQUIVALENT (sim/object counts, world name, save name match between paths).");
        }
        else
        {
            Console.WriteLine("Comparison: DIVERGED");
            foreach (string divergence in report.Divergences())
            {
                Console.WriteLine($"  - {divergence}");
            }
        }
    }

    private static async Task<int> RunAutonomyProbeAsync(IAuthoritativeSimulationHost host, SessionConfig config, CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-autonomy is only supported with the standard Sims 3 host.");
            return 1;
        }

        if (config.ProbeSimObjectId is null)
        {
            Console.Error.WriteLine("--probe-autonomy requires --sim <objectId>.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId.Value;
        CommandResult speedResult = await headless.ApplyCommandAsync(new ClientCommand.SetGameSpeed(Guid.NewGuid(), 0, GameSpeed.Normal), cancellationToken);
        if (!speedResult.IsAccepted)
        {
            Console.Error.WriteLine($"Could not set game speed to Normal: {speedResult.RejectionReason}");
            return 1;
        }

        Console.WriteLine("Game speed set to Normal for autonomy probe.");
        Console.WriteLine($"Probing autonomy disable on sim 0x{simId:X16}...");
        string disableReport = await headless.ProbeDisableAutonomyAsync(simId, cancellationToken);
        Console.WriteLine($"DISABLE result: {disableReport}");

        Console.WriteLine("Waiting 30 s to observe whether autonomy still queues interactions...");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (OperationCanceledException) { }

        Console.WriteLine("Re-enabling autonomy...");
        string enableReport = await headless.ProbeEnableAutonomyAsync(simId, cancellationToken);
        Console.WriteLine($"ENABLE result: {enableReport}");

        Console.WriteLine("Autonomy probe complete. Check the EVENT log above for any interactions queued during the disabled window.");
        Console.Out.Flush();
        return 0;
    }

    private static async Task<int> RunGoHereProbeAsync(AuthoritativeSession session, SessionConfig config, CancellationToken cancellationToken)
    {
        if (config.ProbeSimObjectId is null || config.GoHereX is null || config.GoHereY is null || config.GoHereZ is null)
        {
            Console.Error.WriteLine("--go-here requires --sim <objectId> --x <x> --y <y> --z <z>.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId.Value;
        Vector3Dto target = new(config.GoHereX.Value, config.GoHereY.Value, config.GoHereZ.Value);

        // M4 prep: P3.1 introduced ownership gates that require JoinSession + ClaimSim
        // before QueueInteraction. The single-process --go-here probe must satisfy them.
        const int probePlayerId = 1;
        ClientCommand.JoinSession joinCommand = new(Guid.NewGuid(), probePlayerId, "go-here-probe");
        CommandResult joinResult = await session.HandleCommandAsync(joinCommand, cancellationToken);
        if (!joinResult.IsAccepted)
        {
            Console.Error.WriteLine($"JoinSession rejected for go-here probe: {joinResult.RejectionReason}");
            return 1;
        }

        ClientCommand.ClaimSim claimCommand = new(Guid.NewGuid(), probePlayerId, simId);
        CommandResult claimResult = await session.HandleCommandAsync(claimCommand, cancellationToken);
        if (!claimResult.IsAccepted)
        {
            Console.Error.WriteLine($"ClaimSim rejected for go-here probe: {claimResult.RejectionReason}");
            return 1;
        }

        ClientCommand.SetGameSpeed speedCommand = new(Guid.NewGuid(), probePlayerId, GameSpeed.Speed3);
        CommandResult speedResult = await session.HandleCommandAsync(speedCommand, cancellationToken);
        if (!speedResult.IsAccepted)
        {
            Console.Error.WriteLine($"Could not set game speed to Speed3: {speedResult.RejectionReason}");
            return 1;
        }

        Console.WriteLine("Game speed set to Speed3 for GoHere probe.");
        InteractionRequest request = new(simId, "Terrain.GoHere", null, target, new Dictionary<string, string>());

        ClientCommand.QueueInteraction cmd = new(Guid.NewGuid(), probePlayerId, request);
        Console.WriteLine($"Sending GoHere: sim 0x{simId:X16} → ({target.X:R}, {target.Y:R}, {target.Z:R})");
        CommandResult result = await session.HandleCommandAsync(cmd, cancellationToken);
        if (!result.IsAccepted)
        {
            Console.Error.WriteLine($"GoHere rejected: {result.RejectionReason}");
            return 1;
        }

        Console.WriteLine("GoHere accepted. Waiting up to 60 s for InteractionCompleted or InteractionFailed...");
        // Caller observes EVENT lines on stdout via the bridge event handler wired in Main.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
        }
        catch (OperationCanceledException) { }

        Console.WriteLine("GoHere observation window elapsed.");
        Console.Out.Flush();
        return 0;
    }

    // M4 P4.2.2.2.4.a: live client-mirror startup probe. Server-side TS3 is already at SESSION_READY
    // when this method is invoked (Main called session.StartAsync(saveName) before dispatching here). The probe
    // opens the public TCP listener, spawns SimsOnline.Client as a subprocess with --enable-mirror + --client-ts3 +
    // --client-save (so SimsOnline.Client launches a SECOND TS3 in client role via ClientMirrorHostFactory), and waits
    // for the subprocess to exit after observing both `client_ts3_session_ready` (puppet booted via factory) and
    // `client_role_changed` (TCP+protocol+role wired). NO position-propagation verification yet — that's the .4.b slice.
    private static async Task<int> RunClientMirrorStartupProbeAsync(
        IAuthoritativeSimulationHost host,
        AuthoritativeSession session,
        SessionConfig config,
        CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-client-mirror-startup is only supported with the standard Sims 3 host.");
            return 1;
        }

        string clientBin = config.ClientMirrorBinaryPath!;
        if (!File.Exists(clientBin))
        {
            Console.Error.WriteLine($"--client-mirror-binary not found: {clientBin}");
            return 1;
        }

        await using ClientListenerServer listener = new(session, config.ListenEndpoint);
        headless.Bridge.AuthoritativeEvent += evt =>
        {
            listener.TryQueueAuthoritativeEvent(evt, cancellationToken);
        };

        try
        {
            await listener.StartAsync(cancellationToken);
            Console.WriteLine($"Public client listener bound at {listener.BoundEndpoint}.");
            if (listener.ExposureWarning is { } exposure)
            {
                Console.Error.WriteLine(exposure);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to bind public client listener at {config.ListenEndpoint}: {ex.Message}");
            return 1;
        }

        bool useDotnet = clientBin.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        string fileName = useDotnet ? "dotnet" : clientBin;
        IReadOnlyList<string> subprocessArgList = BuildClientMirrorStartupSubprocessArgumentList(clientBin, listener.BoundEndpoint.ToString(), config, useDotnet);
        string subprocessArgs = FormatArgumentsForLog(subprocessArgList);

        Console.WriteLine($"Spawning SimsOnline.Client subprocess: {fileName} {subprocessArgs}");

        ProcessStartInfo clientStartInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(clientBin) ?? Environment.CurrentDirectory,
        };
        foreach (string arg in subprocessArgList)
        {
            clientStartInfo.ArgumentList.Add(arg);
        }

        using Process proc = new()
        {
            StartInfo = clientStartInfo
        };

        bool sawClientTs3Ready = false;
        bool sawClientRoleChanged = false;
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Console.WriteLine($"[client] {e.Data}");
            if (e.Data.Contains("\"client_ts3_session_ready\"", StringComparison.Ordinal))
            {
                sawClientTs3Ready = true;
            }
            if (e.Data.Contains("client_role_changed", StringComparison.Ordinal)
                || e.Data.Contains("exit_after_role_changed", StringComparison.Ordinal))
            {
                sawClientRoleChanged = true;
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Console.Error.WriteLine($"[client ERR] {e.Data}");
        };

        if (!proc.Start())
        {
            Console.Error.WriteLine("Failed to start SimsOnline.Client subprocess.");
            return 1;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        TimeSpan timeout = TimeSpan.FromSeconds(200);
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"SimsOnline.Client subprocess timed out after {timeout.TotalSeconds:N0} seconds.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Shutdown requested during probe; killing subprocess.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        Console.WriteLine($"SimsOnline.Client subprocess exited with code {proc.ExitCode}.");
        Console.WriteLine($"Saw client_ts3_session_ready: {sawClientTs3Ready}");
        Console.WriteLine($"Saw client_role_changed: {sawClientRoleChanged}");

        if (proc.ExitCode != 0)
        {
            Console.Error.WriteLine("Probe failed: SimsOnline.Client subprocess exited non-zero.");
            return 1;
        }
        if (!sawClientTs3Ready)
        {
            Console.Error.WriteLine("Probe failed: client_ts3_session_ready event was not observed in subprocess stdout.");
            return 1;
        }
        if (!sawClientRoleChanged)
        {
            Console.Error.WriteLine("Probe failed: client_role_changed event was not observed in subprocess stdout.");
            return 1;
        }

        Console.WriteLine("Client mirror startup probe PASSED.");
        Console.Out.Flush();
        return 0;
    }

    private static string BuildClientMirrorStartupSubprocessArgs(string clientBin, string boundEndpoint, SessionConfig config, bool useDotnet)
    {
        return FormatArgumentsForLog(BuildClientMirrorStartupSubprocessArgumentList(clientBin, boundEndpoint, config, useDotnet));
    }

    // measure full-snapshot bridge throughput under live simulation. The historical
    // baseline is ~37s with the game paused; under Normal speed the same request blew past the
    // 600s timeout during P4.4 validation. This probe captures chunk cadence (via the bridge
    // Diagnostic event) for a paused snapshot, then an unpaused one, and reports both.
    private static async Task<int> RunLiveSnapshotProbeAsync(
        IAuthoritativeSimulationHost host,
        AuthoritativeSession session,
        SessionConfig config,
        CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-live-snapshot is only supported with the standard Sims 3 host.");
            return 1;
        }

        headless.Bridge.Diagnostic += line => Console.WriteLine($"[bridge] {line}");

        async Task<(bool ok, double seconds, string detail)> TimedSnapshotAsync(string label)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                SimulationSnapshot snapshot = await session.GetSnapshotAsync(cancellationToken);
                stopwatch.Stop();
                string detail = $"sims={snapshot.Sims.Count} objects={snapshot.Objects.Count}";
                Console.WriteLine($"{label}: OK in {stopwatch.Elapsed.TotalSeconds:F1}s ({detail})");
                return (true, stopwatch.Elapsed.TotalSeconds, detail);
            }
            catch (TimeoutException ex)
            {
                stopwatch.Stop();
                Console.WriteLine($"{label}: TIMEOUT after {stopwatch.Elapsed.TotalSeconds:F1}s — {ex.Message}");
                return (false, stopwatch.Elapsed.TotalSeconds, ex.Message);
            }
        }

        // S1: paused baseline (TS3 loads live mode paused; serve-mode start speed is skipped in probe mode).
        Console.WriteLine("S1: full snapshot with simulation PAUSED (baseline)...");
        (bool pausedOk, double pausedSeconds, _) = await TimedSnapshotAsync("S1 paused snapshot");

        // S2: unpause to Normal, let the simulation get busy, then snapshot again.
        Console.WriteLine("S2: setting game speed Normal and letting the simulation run 15s...");
        CommandResult speedResult = await session.HandleCommandAsync(
            new ClientCommand.SetGameSpeed(Guid.NewGuid(), 0, GameSpeed.Normal), cancellationToken);
        if (!speedResult.IsAccepted)
        {
            Console.Error.WriteLine($"SetGameSpeed Normal rejected: {speedResult.RejectionReason}");
            return 1;
        }
        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);

        (bool liveOk, double liveSeconds, string liveDetail) = await TimedSnapshotAsync("S2 live snapshot");

        Console.WriteLine($"RESULT paused={pausedSeconds:F1}s live={(liveOk ? liveSeconds.ToString("F1") + "s" : "TIMEOUT")} " +
            $"ratio={(pausedOk && liveOk && pausedSeconds > 0 ? (liveSeconds / pausedSeconds).ToString("F1") : "n/a")}");

        if (pausedOk && liveOk && liveSeconds < 120)
        {
            Console.WriteLine("Live snapshot probe PASSED.");
            return 0;
        }

        Console.Error.WriteLine(liveOk
            ? $"Live snapshot probe FAILED: live snapshot took {liveSeconds:F1}s (threshold 120s)."
            : $"Live snapshot probe FAILED: live snapshot timed out. {liveDetail}");
        return 1;
    }

    // live proof that claiming a Sim suppresses its autonomy on the AUTHORITATIVE TS3
    // (and releasing restores it) while the simulation runs at Normal speed. S1 asserts the
    // baseline (autonomy enabled), S2 claims through the real session path and expects
    // enabled=false, S3 releases and expects enabled=true again.
    private static async Task<int> RunClaimAutonomyProbeAsync(
        IAuthoritativeSimulationHost host,
        AuthoritativeSession session,
        SessionConfig config,
        CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-claim-autonomy is only supported with the standard Sims 3 host.");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId!.Value;
        const int probePlayerId = 77;

        CommandResult speedResult = await session.HandleCommandAsync(
            new ClientCommand.SetGameSpeed(Guid.NewGuid(), 0, GameSpeed.Normal), cancellationToken);
        Console.WriteLine($"Speed Normal: {(speedResult.IsAccepted ? "ok" : speedResult.RejectionReason)}");

        SimAutonomyProbeReport baseline = await headless.ProbeSimAutonomyAsync(simId, cancellationToken);
        Console.WriteLine($"S1 baseline: enabled={baseline.Enabled}");
        if (!baseline.Enabled)
        {
            Console.Error.WriteLine("Claim autonomy probe FAILED: baseline autonomy already disabled.");
            return 1;
        }

        CommandResult joinResult = await session.HandleCommandAsync(
            new ClientCommand.JoinSession(Guid.NewGuid(), probePlayerId, "claim-autonomy-probe"), cancellationToken);
        CommandResult claimResult = await session.HandleCommandAsync(
            new ClientCommand.ClaimSim(Guid.NewGuid(), probePlayerId, simId), cancellationToken);
        if (!joinResult.IsAccepted || !claimResult.IsAccepted)
        {
            Console.Error.WriteLine($"Claim autonomy probe FAILED: join/claim rejected ({claimResult.RejectionReason}).");
            return 1;
        }

        SimAutonomyProbeReport afterClaim = await headless.ProbeSimAutonomyAsync(simId, cancellationToken);
        Console.WriteLine($"S2 after claim: enabled={afterClaim.Enabled}");

        CommandResult releaseResult = await session.HandleCommandAsync(
            new ClientCommand.ReleaseSim(Guid.NewGuid(), probePlayerId, simId), cancellationToken);
        SimAutonomyProbeReport afterRelease = await headless.ProbeSimAutonomyAsync(simId, cancellationToken);
        Console.WriteLine($"S3 after release: enabled={afterRelease.Enabled} (release {(releaseResult.IsAccepted ? "ok" : releaseResult.RejectionReason)})");

        bool pass = !afterClaim.Enabled && afterRelease.Enabled;
        Console.WriteLine($"RESULT baseline=enabled afterClaim={(afterClaim.Enabled ? "ENABLED(!)" : "disabled")} afterRelease={(afterRelease.Enabled ? "enabled" : "DISABLED(!)")}");
        Console.WriteLine(pass ? "Claim autonomy probe PASSED." : "Claim autonomy probe FAILED.");
        return pass ? 0 : 1;
    }

    private static IReadOnlyList<string> BuildClientMirrorStartupSubprocessArgumentList(string clientBin, string boundEndpoint, SessionConfig config, bool useDotnet)
    {
        List<string> args = new();
        if (useDotnet)
        {
            args.Add(clientBin);
        }

        AddOption(args, "--endpoint", boundEndpoint);
        AddOption(args, "--player", "1");
        AddOption(args, "--display", "p4.2.2.2.4.a-mirror-probe");
        AddOption(args, "--role", "client");
        args.Add("--enable-mirror");
        AddOption(args, "--client-ts3", config.ClientMirrorTs3Path!);
        AddOption(args, "--client-save", config.ClientMirrorSaveName!);
        if (!string.IsNullOrEmpty(config.ClientMirrorSandboxieBoxName))
        {
            AddOption(args, "--client-ts3-sandboxie-box", config.ClientMirrorSandboxieBoxName!);
        }
        if (!string.IsNullOrEmpty(config.ClientMirrorSandboxieStartPath))
        {
            AddOption(args, "--client-ts3-sandboxie-start", config.ClientMirrorSandboxieStartPath!);
        }
        args.Add("--exit-after-role-changed");
        args.Add("--json-stdout");
        AddOption(args, "--connect-timeout", "60");
        return args;
    }

    private static void AddOption(List<string> args, string option, string value)
    {
        args.Add(option);
        args.Add(value);
    }

    private static string FormatArgumentsForLog(IReadOnlyList<string> args)
    {
        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        StringBuilder builder = new(value.Length + 2);
        builder.Append('"');
        builder.Append(value.Replace("\"", "\\\"", StringComparison.Ordinal));
        builder.Append('"');
        return builder.ToString();
    }

    private static async Task<int> RunClientMirrorPositionProbeAsync(
        IAuthoritativeSimulationHost host,
        AuthoritativeSession session,
        SessionConfig config,
        CancellationToken cancellationToken)
    {
        if (host is not HeadlessSims3Host headless)
        {
            Console.Error.WriteLine("--probe-client-mirror-position is only supported with the standard Sims 3 host.");
            return 1;
        }

        string clientBin = config.ClientMirrorBinaryPath!;
        if (!File.Exists(clientBin))
        {
            Console.Error.WriteLine($"--client-mirror-binary not found: {clientBin}");
            return 1;
        }

        ulong simId = config.ProbeSimObjectId!.Value;
        Vector3Dto target = new(config.GoHereX!.Value, config.GoHereY!.Value, config.GoHereZ!.Value);

        await using ClientListenerServer listener = new(session, config.ListenEndpoint);
        headless.Bridge.AuthoritativeEvent += evt =>
        {
            listener.TryQueueAuthoritativeEvent(evt, cancellationToken);
        };

        try
        {
            await listener.StartAsync(cancellationToken);
            Console.WriteLine($"Public client listener bound at {listener.BoundEndpoint}.");
            if (listener.ExposureWarning is { } exposure)
            {
                Console.Error.WriteLine(exposure);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to bind public client listener at {config.ListenEndpoint}: {ex.Message}");
            return 1;
        }

        // We claim the Sim as player 1 on the server side to satisfy ownership rules.
        const int serverPlayerId = 1;
        Console.WriteLine($"Server: JoinSession for player {serverPlayerId}...");
        await session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), serverPlayerId, "position-probe-server"), cancellationToken);
        Console.WriteLine($"Server: ClaimSim 0x{simId:X16} for player {serverPlayerId}...");
        await session.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), serverPlayerId, simId), cancellationToken);

        // Spawn client subprocess as player 2
        const int clientPlayerId = 2;
        bool useDotnet = clientBin.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        string fileName = useDotnet ? "dotnet" : clientBin;
        
        List<string> args = new();
        if (useDotnet)
        {
            args.Add(clientBin);
        }
        AddOption(args, "--endpoint", listener.BoundEndpoint.ToString());
        AddOption(args, "--player", clientPlayerId.ToString(CultureInfo.InvariantCulture));
        AddOption(args, "--display", "p4.2.2.2.4.b-position-probe");
        AddOption(args, "--role", "client");
        args.Add("--enable-mirror");
        AddOption(args, "--client-ts3", config.ClientMirrorTs3Path!);
        AddOption(args, "--client-save", config.ClientMirrorSaveName!);
        if (!string.IsNullOrEmpty(config.ClientMirrorSandboxieBoxName))
        {
            AddOption(args, "--client-ts3-sandboxie-box", config.ClientMirrorSandboxieBoxName!);
        }
        if (!string.IsNullOrEmpty(config.ClientMirrorSandboxieStartPath))
        {
            AddOption(args, "--client-ts3-sandboxie-start", config.ClientMirrorSandboxieStartPath!);
        }
        // Tell client to verify and exit after Sim position reaches target
        AddOption(args, "--exit-after-position-mirror", $"{simId}:{target.X.ToString(CultureInfo.InvariantCulture)},{target.Y.ToString(CultureInfo.InvariantCulture)},{target.Z.ToString(CultureInfo.InvariantCulture)}");
        args.Add("--json-stdout");
        AddOption(args, "--connect-timeout", "30");
        AddOption(args, "--client-host-timeout", "300");

        string subprocessArgs = FormatArgumentsForLog(args);
        Console.WriteLine($"Spawning SimsOnline.Client subprocess: {fileName} {subprocessArgs}");

        ProcessStartInfo clientStartInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(clientBin) ?? Environment.CurrentDirectory,
        };
        foreach (string arg in args)
        {
            clientStartInfo.ArgumentList.Add(arg);
        }

        using Process proc = new()
        {
            StartInfo = clientStartInfo
        };

        bool sawClientTs3Ready = false;
        bool sawClientRoleChanged = false;
        bool sawPositionVerified = false;

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Console.WriteLine($"[client] {e.Data}");
            if (e.Data.Contains("\"client_ts3_session_ready\"", StringComparison.Ordinal))
            {
                sawClientTs3Ready = true;
            }
            if (e.Data.Contains("client_role_changed", StringComparison.Ordinal))
            {
                sawClientRoleChanged = true;
            }
            if (e.Data.Contains("\"client_mirror_position_verified\"", StringComparison.Ordinal))
            {
                sawPositionVerified = true;
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Console.Error.WriteLine($"[client ERR] {e.Data}");
        };

        if (!proc.Start())
        {
            Console.Error.WriteLine("Failed to start SimsOnline.Client subprocess.");
            return 1;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Wait for client to connect and be ready
        Console.WriteLine("Waiting for client to connect and load save...");
        TimeSpan startupTimeout = TimeSpan.FromSeconds(300);
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < startupTimeout && (!sawClientTs3Ready || !sawClientRoleChanged))
        {
            await Task.Delay(250, cancellationToken);
        }

        if (!sawClientTs3Ready || !sawClientRoleChanged)
        {
            Console.Error.WriteLine("Client subprocess failed to connect or load save in time.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 1;
        }

        // Client is connected and mirroring! Now trigger the movement/position update on the server side
        Console.WriteLine($"Server: Moving Sim 0x{simId:X16} to target ({target.X}, {target.Y}, {target.Z})...");
        
        // Switch script DLL role to Client temporarily to set position directly on the server TS3
        await headless.SetRoleAsync(HostRole.Client, cancellationToken);
        await headless.ApplyPositionAsync(simId, target, cancellationToken);
        await headless.SetRoleAsync(HostRole.Server, cancellationToken);

        // Force a snapshot delta so the server broadcasts the new position immediately
        Console.WriteLine("Server: Requesting snapshot to trigger broadcast...");
        SimulationSnapshot forcedSnap = await session.GetSnapshotAsync(cancellationToken);
        await listener.BroadcastEventAsync(new ServerEvent.SnapshotDelta(forcedSnap), cancellationToken);

        Console.WriteLine("Waiting for client subprocess to verify position propagation...");
        TimeSpan verifyTimeout = TimeSpan.FromSeconds(300);
        sw.Restart();
        while (sw.Elapsed < verifyTimeout && !sawPositionVerified)
        {
            await Task.Delay(250, cancellationToken);
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync(cancellationToken);
            }
        }
        catch { }

        Console.WriteLine($"Saw client_ts3_session_ready: {sawClientTs3Ready}");
        Console.WriteLine($"Saw client_role_changed: {sawClientRoleChanged}");
        Console.WriteLine($"Saw client_mirror_position_verified: {sawPositionVerified}");

        if (!sawPositionVerified)
        {
            Console.Error.WriteLine("Probe failed: client_mirror_position_verified event was not observed.");
            return 1;
        }

        Console.WriteLine("Client mirror position propagation probe PASSED.");
        Console.Out.Flush();
        return 0;
    }
}
