using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    private static ulong PlacementCorr(string line)
    {
        Assert(RoadPlaceRequest.TryParse(line, out RoadPlaceRequest request), "The mod's line parses: " + line);
        return request.Corr;
    }

    private static void ResetPlacement()
    {
        RustRoutePlan.Reset();
        RustRoadPlace.Reset();
        RustRoadPlace.Enabled = true;
    }

    private static RustRoadPlace.Answer? Place(FakeWaitEnv env, int option = 0, double seconds = 0.75) =>
        RustRoadPlace.RequestAndWait(7, 1.25f, -0.5f, 3f, option, 10f, 50, "none", seconds, env);

    [Test("RustRoadPlace.SwitchAndOrderedFallbacks")]
    private static void RustRoadPlaceSwitchAndOrderedFallbacks()
    {
        ResetPlacement();
        int sends = 0;
        RustRoadPlace.RequestSender = _ => sends++;
        FakeWaitEnv env = new() { CanYieldNow = false };
        RustRoadPlace.Enabled = false;
        RustRoutePlan.InBridgePump = true;
        Assert(Place(env, -1) == null && RustRoadPlace.Counter("off") == 1, "Off wins over option, pump and yield checks.");
        RustRoadPlace.Enabled = true;
        Assert(Place(env, 6) == null && RustRoadPlace.Counter("fallback.unmappedOption") == 1, "Unknown option wins over pump.");
        Assert(Place(env) == null && RustRoadPlace.Counter("fallback.inPump") == 1, "Pump wins over CanYield.");
        RustRoutePlan.InBridgePump = false;
        for (int option = 0; option <= 5; option++) Assert(Place(env, option) == null, "Non-yielding native fallback.");
        Assert(RustRoadPlace.Counter("fallback.notYielding.road") == 1
            && RustRoadPlace.Counter("fallback.notYielding.onFoot") == 5, "Road and on-foot census remain separate.");
        Assert(sends == 0 && env.Yields == 0, "None of these paths requests or yields.");
        RustRoadPlace.Reset();
    }

    [Test("RustRoadPlace.ServedAllSelectorsAndHeadingConversion")]
    private static void RustRoadPlaceServedAllSelectorsAndHeadingConversion()
    {
        ResetPlacement();
        for (int option = 0; option <= 5; option++)
        {
            ulong corr = 0;
            RustRoadPlace.RequestSender = line =>
            {
                corr = PlacementCorr(line);
                Assert(line.EndsWith($"|{option}|1000|50|none", StringComparison.Ordinal), "All six selectors reach the kernel.");
            };
            FakeWaitEnv env = new() { OnYield = _ =>
            {
                string[] reply = [corr.ToString(CultureInfo.InvariantCulture), "1234", "-50", "300", "90000"];
                Assert(RustRoadPlace.TryParseAnswer(reply, 0, out ulong got, out RustRoadPlace.Answer parsed)
                    && got == corr && RustRoadPlace.Offer(got, parsed), "Offer the compact integer answer.");
            } };
            RustRoadPlace.Answer? a = Place(env, option);
            Assert(a != null && a.Found && a.X == 12.34f && a.Y == -0.5f && a.Z == 3f, "Centimetres become metres.");
            Assert(a!.Qx == 0 && a.Qz == 0 && Math.Abs(a.Qy - Math.Sqrt(0.5)) < 0.000001
                && Math.Abs(a.Qw - Math.Sqrt(0.5)) < 0.000001 && env.Yields == 1, "Yaw about +Y becomes a quaternion.");
        }
        Assert(RustRoadPlace.Counter("served") == 6 && RustRoadPlace.Counter("servedNone") == 0, "Each accepted placement counted once.");
        RustRoadPlace.Reset();
    }

    [Test("RustRoadPlace.AcceptedNoneNeverFallsBack")]
    private static void RustRoadPlaceAcceptedNoneNeverFallsBack()
    {
        ResetPlacement();
        ulong corr = 0;
        RustRoadPlace.RequestSender = line => corr = PlacementCorr(line);
        FakeWaitEnv env = new() { OnYield = _ => Assert(RustRoadPlace.OfferNone(corr, "f84841652498787d"), "Accept none.") };
        RustRoadPlace.Answer? a = Place(env);
        int nativeCalls = 0;
        if (a == null) nativeCalls++; // the boundary invokes native only for null, not Found=false
        Assert(a != null && !a.Found && nativeCalls == 0 && env.Yields == 1, "found=0 ends the wait without native fallback.");
        Assert(RustRoadPlace.Counter("servedNone") == 1 && RustRoadPlace.DescribeStatus().Contains("lastHash:f84841652498787d"),
            "Accepted-none is served and its hash survives.");
        RustRoadPlace.Reset();
    }

    [Test("RustRoadPlace.TransportTimeoutStaleAndRefusal")]
    private static void RustRoadPlaceTransportTimeoutStaleAndRefusal()
    {
        ResetPlacement();
        Assert(Place(new()) == null && RustRoadPlace.Counter("fallback.noDaemon") == 1, "No sender.");
        RustRoadPlace.RequestSender = _ => throw new IOException("offline");
        Assert(Place(new()) == null && RustRoadPlace.Counter("fallback.noDaemon") == 2, "Send fault.");
        ulong expired = 0, next = 0;
        RustRoadPlace.RequestSender = line => expired = PlacementCorr(line);
        FakeWaitEnv timeout = new() { OnYield = n =>
        {
            if (n == 3) Assert(!RustRoadPlace.OfferNone(expired, null), "An answer at the deadline is stale even before the caller resumes.");
        } };
        Assert(Place(timeout, seconds: 0.3) == null && timeout.Yields == 3 && RustRoadPlace.Counter("fallback.timeout") == 1, "Exact fake-clock deadline.");
        Assert(!RustRoadPlace.OfferNone(expired, null) && !RustRoadPlace.Decline(expired, "BadRequest"), "Late replies cannot resurrect an expired request.");
        RustRoadPlace.RequestSender = line => next = PlacementCorr(line);
        FakeWaitEnv fresh = new() { OnYield = _ =>
        {
            Assert(next > expired && !RustRoadPlace.OfferNone(expired, null), "Fresh request rejects the old corr too.");
            Assert(RustRoadPlace.OfferNone(next, null), "Only its own answer is accepted.");
        } };
        Assert(Place(fresh) != null, "The new wait is unaffected.");
        foreach (string reason in new[] { "RoadDataMissing", "LaneKindUnsupported", "BadRequest", "noDaemon" })
        {
            ulong corr = 0;
            RustRoadPlace.RequestSender = line => corr = PlacementCorr(line);
            FakeWaitEnv env = new() { OnYield = _ => Assert(RustRoadPlace.Decline(corr, reason), "Named decline.") };
            Assert(Place(env) == null && env.Yields == 1, "A refusal ends at once.");
            Assert(RustRoadPlace.Counter("fallback." + reason) > 0, "Exact refusal name counted.");
        }
        Assert(RustBridgeWait.BoundSeconds(-1) == 0.05 && RustBridgeWait.BoundSeconds(99) == 5
            && RustBridgeWait.BoundSeconds(double.NaN) == 0.75, "Both waits are bounded.");
        RustRoadPlace.Reset();
    }

    private sealed class TaskWaitEnv : RustRoutePlan.IWaitEnv, RustRoutePlan.IWaitCaller
    {
        public ulong CallerTask { get; init; }
        public bool CanYield => true;
        public DateTime UtcNow => Inner.UtcNow;
        public readonly FakeWaitEnv Inner = new();
        public void Yield() => Inner.Yield();
    }

    [Test("RustRoadPlace.CorrelationsIsolateRoutePlacementAndCallerTasks")]
    private static void RustRoadPlaceCorrelationsIsolateRoutePlacementAndCallerTasks()
    {
        ResetPlacement();
        RustRoutePlan.Enabled = true;
        ulong road = 0, route = 0;
        RustRoadPlace.RequestSender = line => road = PlacementCorr(line);
        RustRoutePlan.RequestSender = line =>
        {
            Assert(RoutePlanRequest.TryParse(line, out RoutePlanRequest r), "Parse corr route request.");
            route = r.Corr;
        };
        TaskWaitEnv env = new() { CallerTask = 12 };
        env.Inner.OnYield = _ =>
        {
            Assert(RustRoadPlace.RequestAndWait(7, 0, 0, 0, 0, 10, 50, "none", 0.75, env) == null,
                "A caller cannot start a second outstanding request.");
            TaskWaitEnv routeEnv = new() { CallerTask = 13 };
            routeEnv.Inner.OnYield = _ =>
            {
                Assert(route > road && !RustRoadPlace.OfferNone(route, null) && !RustRoutePlan.OfferWait(road, WaitPlan),
                    "Even with both requests active, answer channels cannot consume each other's correlations.");
                Assert(RustRoadPlace.OfferNone(road, null) && RustRoutePlan.OfferWait(route, WaitPlan), "Each owner receives its own answer.");
            };
            Assert(RustRoutePlan.RequestAndWait(9, 0, 0, 0, 4, 0, 2, 0.75, routeEnv)!.SequenceEqual(WaitPlan), "Route answer.");
        };
        Assert(RustRoadPlace.RequestAndWait(7, 0, 0, 0, 0, 10, 50, "none", 0.75, env) is { Found: false }, "Placement answer.");
        Assert(!RustRoadPlace.OfferNone(road, null) && !RustRoutePlan.OfferWait(route, WaitPlan), "Completed replies cannot be replayed.");
        RustRoadPlace.Reset();
        RustRoutePlan.Reset();
    }

    [Test("RustRoadPlace.ResetAndAbortPropagateAndCleanPending")]
    private static void RustRoadPlaceResetAndAbortPropagateAndCleanPending()
    {
        foreach (Exception fault in new Exception[] { new ResetException(), AbortException() })
        {
            ResetPlacement();
            ulong corr = 0;
            RustRoadPlace.RequestSender = line => corr = PlacementCorr(line);
            FakeWaitEnv env = new() { OnYield = _ => throw fault };
            bool threw = false;
            try { Place(env); } catch (Exception ex) { threw = ReferenceEquals(ex, fault); }
            Assert(threw && !RustRoadPlace.OfferNone(corr, null), "Reset/abort propagates and removes pending correlation.");
        }
        RustRoadPlace.Reset();
    }

    [Test("RustRoadPlace.WireViewHashAndHopRoundTrip")]
    private static void RustRoadPlaceWireViewHashAndHopRoundTrip()
    {
        ResetPlacement();
        string view = "0,0,0,0,0,1000000,0,1000000,0,90000,1000000,0,10000";
        string line = RustRoadPlace.BridgeLine(77, 0, 1.25f, -0.5f, 3, 4, 10, 50, view);
        Assert(BridgeLineCodec.TryParseRoadPlaceRequest(line, out RoadPlaceRequest r) && r.Corr == 77 && r.RadiusCm == 0 && r.View == view,
            "Null object forces zero radius; server view round-trips.");
        Assert(RustAuthoritySidecar.PlaceOnRoadOffScreenLine(r) == $"CMD|77|PlaceOnRoadOffScreen|0|125|-50|300|4|1000|0|{view}\n",
            "Exact planning-channel bytes.");
        Assert(!RoadPlaceRequest.TryParse(line + "|extra", out _)
            && !RoadPlaceRequest.TryParse(line.Replace("|125|", "|1e2|"), out _)
            && !RoadPlaceRequest.ValidView(view.Replace("90000", "180000"))
            && !RoadPlaceRequest.ValidView(view.Replace("0,1000000,0", "0,0,1000000")), "Malformed request or view is rejected.");
        RoadPlaceResult found = RoadPlaceResult.Parse(["Rp|77|0|1|1000|0|0|90000|1|0|7825e298d97065c8"], r);
        Assert(found.Found && found.Hash == found.AnswerHash(), "Independent sealed hash vector.");
        RoadPlaceResult none = RoadPlaceResult.Parse(["Rp|77|0|0|0|0|0|0|0|0|f84841652498787d"], r);
        Assert(!none.Found && none.Refusal == null, "found=0 is not a refusal.");
        Assert(RoadPlaceResult.Parse(["Rp|77|0|1|1000|0|0|90000|1|0|0000000000000000"], r).Refusal == "BadRequest", "Corrupt hash is refused.");
        foreach (string name in new[] { "RoadDataMissing", "LaneKindUnsupported", "BadRequest" })
            Assert(RoadPlaceResult.Parse([$"ERR|77|PlaceOnRoadOffScreen|{name}"], r).Refusal == name, "Named kernel refusal preserved.");
        Assert(RoadPlaceResult.Parse(["Rp|78|0|0|0|0|0|0|0|0|f84841652498787d"], r).Refusal == "noDaemon", "Wrong corr ignored.");

        foreach (RoadPlaceResult result in new[] { found, none, RoadPlaceResult.Declined("RoadDataMissing") })
        {
            RoadPlaceHop hop = new(new PlaceDouble(result), (args, _) =>
            {
                string[] p = args.ToArray();
                ulong corr = ulong.Parse(p[1], CultureInfo.InvariantCulture);
                if (p[0] == "rust-road-place")
                    Assert(RustRoadPlace.TryParseAnswer(p, 1, out corr, out RustRoadPlace.Answer a) && RustRoadPlace.Offer(corr, a), "Quaternion reply parsed.");
                else if (p[0] == "rust-road-place-none") Assert(RustRoadPlace.OfferNone(corr, p[2]), "None reply.");
                else Assert(RustRoadPlace.Decline(corr, p[2]), "Named decline reply.");
                return Task.FromResult<string?>("ok");
            });
            RustRoadPlace.RequestSender = request =>
            {
                Assert(RoadPlaceRequest.TryParse(request, out RoadPlaceRequest parsed), "Parse source request.");
                hop.HandleAsync(parsed, CancellationToken.None).GetAwaiter().GetResult();
            };
            RustRoadPlace.Answer? a = Place(new());
            Assert(result.Refusal != null ? a == null : a != null && a.Found == result.Found, "Hop and pure seam agree.");
            Assert(hop.Served + hop.ServedNone + hop.Declined == 1 && hop.SendFaults == 0, "Exactly one decision and reply.");
        }
        RustRoadPlace.Reset();
    }

    [Test("RustRoadPlace.HopFaultsAlwaysDeclineAndCountSendFailure")]
    private static void RustRoadPlaceHopFaultsAlwaysDeclineAndCountSendFailure()
    {
        RoadPlaceHop hop = new(new PlaceDouble(null), (args, _) =>
        {
            Assert(args.SequenceEqual(new[] { "rust-road-place-decline", "88", "noDaemon" }), "A source exception is a decline, not found=0.");
            throw new IOException("bridge fault");
        });
        hop.HandleAsync(new(88, 0, 0, 0, 0, 0, 1000, 0, "none"), CancellationToken.None).GetAwaiter().GetResult();
        Assert(hop.Declined == 1 && hop.SendFaults == 1, "Both faults observed and counted.");
    }

    private sealed class PlaceDouble(RoadPlaceResult? result) : IRoadPlaceSource
    {
        public Task<RoadPlaceResult> PlaceAsync(RoadPlaceRequest request, CancellationToken cancellationToken) =>
            result == null ? throw new IOException("daemon fault") : Task.FromResult(result);
    }

    [Test("RustRoadPlace.BridgeRequestsNeverReachClients")]
    private static void RustRoadPlaceBridgeRequestsNeverReachClients()
    {
        HostBridgeServer bridge = new("memory:TS3W");
        List<RoadPlaceRequest> requests = new();
        int events = 0;
        bridge.RoadPlaceRequested += requests.Add;
        bridge.AuthoritativeEvent += _ => events++;
        bridge.ProcessResponseLine(RustRoadPlace.BridgeLine(91, 0, 1, 0, 2, 4, 10, 0, "none"));
        bridge.ProcessResponseLine("EVENT|RoadPlaceRequest|91|bad");
        Assert(requests.Count == 1 && requests[0].LaneKind == 4 && events == 0, "Only valid bridge-only requests reach the hop.");
        bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Test("RustRoadPlace.SidecarHelloFenceAndUlongCorrRoundTrip")]
    private static void RustRoadPlaceSidecarHelloFenceAndUlongCorrRoundTrip() =>
        RunRoadPlaceSocketTestAsync().GetAwaiter().GetResult();

    private static async Task RunRoadPlaceSocketTestAsync()
    {
        string? oldPort = Environment.GetEnvironmentVariable(RustAuthoritySidecar.EnvPortVar);
        string? oldSidecar = Environment.GetEnvironmentVariable(RustAuthoritySidecar.EnvVar);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvPortVar, ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvVar, "0");
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(deadline.Token);
            using StreamReader reader = new(client.GetStream());
            using StreamWriter writer = new(client.GetStream()) { AutoFlush = true, NewLine = "\n" };
            bool hello = false;
            int requests = 0;
            while (true)
            {
                string? line = await reader.ReadLineAsync(deadline.Token);
                Assert(line != null, "Connection remains open through the final fence.");
                string[] p = line!.Split('|');
                if (p[0] == "HELLO")
                {
                    hello = true;
                    await writer.WriteLineAsync("WELCOME|1|1|17|15");
                }
                else if (p[0] == "CONNECT")
                {
                    Assert(hello, "Existing sidecar hello ordering.");
                    await writer.WriteLineAsync("WELCOME|0|1|17|15");
                }
                else if (p[0] == "CMD")
                {
                    Assert(hello && p.Length == 11 && p[2] == "PlaceOnRoadOffScreen", "Planning uses the greeted existing channel.");
                    requests++;
                    Assert(p[1] == (ulong.MaxValue - (ulong)(2 - requests)).ToString(CultureInfo.InvariantCulture),
                        "Full u64 correlation survives the socket; requests are serialized.");
                    await writer.WriteLineAsync(requests == 1
                        ? $"Rp|{p[1]}|0|1|1000|0|0|90000|1|0|7825e298d97065c8"
                        : $"Rp|{p[1]}|0|0|0|0|0|0|0|0|f84841652498787d");
                }
                else if (p[0] == "PING")
                {
                    await writer.WriteLineAsync("PONG|" + p[1]);
                    if (requests == 2) return;
                }
                else Assert(false, "Unexpected planning-channel verb: " + line);
            }
        }, deadline.Token);
        try
        {
            using SidecarRoadPlaceSource source = new();
            RoadPlaceRequest request = new(ulong.MaxValue - 1, 0, 0, 0, 0, 0, 1000, 0, "none");
            Task<RoadPlaceResult> first = source.PlaceAsync(request, deadline.Token);
            Task<RoadPlaceResult> second = source.PlaceAsync(request with { Corr = ulong.MaxValue }, deadline.Token);
            RoadPlaceResult a = await first.WaitAsync(deadline.Token);
            RoadPlaceResult b = await second.WaitAsync(deadline.Token);
            Assert(a.Found && a.Refusal == null && !b.Found && b.Refusal == null, "Both fenced answers retain their meaning.");
            await server.WaitAsync(deadline.Token);
        }
        finally
        {
            deadline.Cancel();
            Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvPortVar, oldPort);
            Environment.SetEnvironmentVariable(RustAuthoritySidecar.EnvVar, oldSidecar);
        }
    }
}
