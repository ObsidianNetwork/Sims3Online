using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;
using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// card 55: the RouteCtor seam's Rust plan (RustRoutePlan, linked from the mod). Off by default, every plan call is
/// native and uncounted; on, a Sim's route takes the plan offered for its goal, once, and every other plan call is a named,
/// counted native fallback (the planner-entry census card 55 E1 asks for).
/// </summary>
internal static partial class Program
{
    [Test("RustRoute.OffByDefaultIsNativeAndUncounted")]
    private static void RustRouteOffByDefaultIsNativeAndUncounted()
    {
        RustRoutePlan.Reset();
        Assert(!RustRoutePlan.Enabled, "The mod loads with the switch off.");
        float[] plan = { 0f, 0f, 0f, 3f, 0f, 0f };
        Assert(RustRoutePlan.Decide(false, true, true, false, plan) == RustRoutePlan.Native, "Off: native even with a plan.");
        Assert(!RustRoutePlan.Offer(7uL, plan), "Off: a plan is not kept.");
        Assert(RustRoutePlan.Take(7uL, 3f, 0f) == null, "Off: nothing to take.");
        RustRoutePlan.Count(RustRoutePlan.EntryPlan, RustRoutePlan.Native);
        Assert(RustRoutePlan.Report() == "rustRoute:off", "Off: the report adds nothing but the switch. Got " + RustRoutePlan.Report());
        RustRoutePlan.Reset();
    }

    [Test("RustRoute.DecideNamesEveryFallback")]
    private static void RustRouteDecideNamesEveryFallback()
    {
        float[] plan = { 0f, 0f, 0f, 3f, 0f, 0f };
        Assert(RustRoutePlan.Decide(true, true, true, false, plan) == RustRoutePlan.UseRust, "A Sim, a point goal, no path yet, a plan: Rust.");
        Assert(RustRoutePlan.Decide(true, false, true, false, plan) == RustRoutePlan.FallbackNotSim, "Not a Sim's route.");
        Assert(RustRoutePlan.Decide(true, true, false, false, plan) == RustRoutePlan.FallbackNoGoal, "A lot or slot goal: nothing to match.");
        Assert(RustRoutePlan.Decide(true, true, true, true, plan) == RustRoutePlan.FallbackHasPath, "A replan of a route that has a path.");
        Assert(RustRoutePlan.Decide(true, true, true, false, null) == RustRoutePlan.FallbackNoPlan, "No plan for this goal.");
        Assert(RustRoutePlan.NameOf(RustRoutePlan.FallbackNoPlan) == "noPlan" && RustRoutePlan.NameOf(99) == "unknown", "Names.");
    }

    [Test("RustRoute.PlanIsTakenOnceAndOnlyForItsGoal")]
    private static void RustRoutePlanIsTakenOnceAndOnlyForItsGoal()
    {
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        float[] plan = { 1f, 0.5f, 2f, 2.5f, 0.5f, 2f, 4f, 0.5f, 2f };
        Assert(RustRoutePlan.Offer(42uL, plan), "A whole plan is kept.");
        plan[6] = 99f;
        Assert(RustRoutePlan.Take(42uL, 10f, 10f) == null, "Another goal: not this route's plan.");
        Assert(RustRoutePlan.Take(7uL, 4f, 2f) == null, "Another Sim: nothing.");
        float[] taken = RustRoutePlan.Take(42uL, 4.05f, 2.05f);
        Assert(taken != null && taken.Length == 9 && taken[6] == 4f, "Within GoalTolerance of the last point, the kept copy (not the caller's array).");
        Assert(RustRoutePlan.Take(42uL, 4f, 2f) == null, "Taken once.");
        Assert(RustRoutePlan.Offer(42uL, new float[] { 0f, 0f, 0f, 4f, 0f, 2f }) && RustRoutePlan.Take(42uL, 4f + 0.2f, 2f) == null,
            "0.2 m off is outside the card's 0.10 m.");
        RustRoutePlan.Enabled = false;
        Assert(RustRoutePlan.Take(42uL, 4f, 2f) == null, "Turning the switch drops plans not taken.");
        RustRoutePlan.Reset();
    }

    [Test("RustRoute.OfferRefusesBrokenPlans")]
    private static void RustRouteOfferRefusesBrokenPlans()
    {
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        Assert(!RustRoutePlan.Offer(1uL, null), "No points.");
        Assert(!RustRoutePlan.Offer(1uL, new float[] { 0f, 0f, 0f }), "One point is not a walk.");
        Assert(!RustRoutePlan.Offer(1uL, new float[] { 0f, 0f, 0f, 1f, 0f }), "Not whole triples.");
        Assert(!RustRoutePlan.Offer(1uL, new float[] { 0f, 0f, 0f, float.NaN, 0f, 0f }), "Not finite.");
        Assert(!RustRoutePlan.Offer(0uL, new float[] { 0f, 0f, 0f, 1f, 0f, 0f }), "No Sim.");
        Assert(RustRoutePlan.Report().EndsWith(",offered:0,refused:5,pending:0,asked:0,waitTimeout:0,waitDeclined:0,waitNoYield:0,waitInPump:0", StringComparison.Ordinal), "Refusals counted. Got " + RustRoutePlan.Report());
        RustRoutePlan.Reset();
    }

    // The census: entries by kind and decisions by reason; every counted fallback is a native search E1 must count.
    [Test("RustRoute.CensusCountsEntriesAndFallbacks")]
    private static void RustRouteCensusCountsEntriesAndFallbacks()
    {
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        RustRoutePlan.Count(RustRoutePlan.EntryPlan, RustRoutePlan.UseRust);
        RustRoutePlan.Count(RustRoutePlan.EntryPlanFromPoint, RustRoutePlan.FallbackNoPlan);
        RustRoutePlan.Count(RustRoutePlan.EntryReplan, RustRoutePlan.FallbackHasPath);
        RustRoutePlan.Count(RustRoutePlan.EntryReplanFromPoint, RustRoutePlan.FallbackInsertFailed);
        RustRoutePlan.Count(RustRoutePlan.EntryPlan, RustRoutePlan.FallbackNotSim);
        RustRoutePlan.Count(RustRoutePlan.EntryPlan, RustRoutePlan.Native);
        RustRoutePlan.Count(9, RustRoutePlan.UseRust);
        Assert(RustRoutePlan.Fallbacks() == 4L, "Four native searches let through. Got " + RustRoutePlan.Fallbacks());
        string expected = "rustRoute:on,plan:2,planFrom:1,replan:1,replanFrom:1,rust:1,notSim:1,noGoal:0,hasPath:1,noPlan:1,insertFailed:1"
            + ",fallbacks:4,offered:0,refused:0,pending:0,asked:0,waitTimeout:0,waitDeclined:0,waitNoYield:0,waitInPump:0";
        Assert(RustRoutePlan.Report() == expected, "Report. Got " + RustRoutePlan.Report());
        RustRoutePlan.Reset();
    }

    // Production path (Program.cs): StartRustRouteAsync right after session.StartAsync, ring off. It turns the seam on once and
    // subscribes the hop; the ring-on path (PublishPresentationAsync) no longer sends rust-route-on at all.
    [Test("AuthoritativeSession.StartsRustRouteWithoutTheRingAndAnswersRequests")]
    private static void AuthoritativeSessionStartsRustRouteWithoutTheRingAndAnswersRequests()
    {
        InMemoryAuthoritativeHost off = new InMemoryAuthoritativeHost();
        AuthoritativeSession offSession = new AuthoritativeSession(off);
        offSession.StartRustRouteAsync(null, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert(off.SacsRecorderCalls.Count == 0, "Off by default: nothing sent. Got " + string.Join(" / ", off.SacsRecorderCalls));

        InMemoryAuthoritativeHost host = new InMemoryAuthoritativeHost();
        AuthoritativeSession session = new AuthoritativeSession(host);
        session.ConfigureRustRoute(true);
        TaskCompletionSource<bool> answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SacsRecorderHandler = args =>
        {
            if (args[0] == "rust-plan-none" && args[1] == "42")
            {
                answered.TrySetResult(true);
            }

            return "ok";
        };
        Action<RoutePlanRequest>? raise = null;
        RoutePlanHop hop = new(new KernelPendingRoutePlanSource(), (args, token) => host.SacsRecorderAsync(args, token));
        session.StartRustRouteAsync(hop, handler => raise = handler, CancellationToken.None).GetAwaiter().GetResult();
        session.StartRustRouteAsync(hop, handler => raise = handler, CancellationToken.None).GetAwaiter().GetResult();
        Assert(host.SacsRecorderCalls.Count(c => c == "rust-route-on") == 1 && !host.SacsRecorderCalls.Contains("ring-on"),
            "rust-route-on once, with the ring off. Got " + string.Join(" / ", host.SacsRecorderCalls));
        Assert(raise != null, "The hop is subscribed to the bridge's requests.");
        raise!(new RoutePlanRequest(42uL, 0f, 0f, 0f, 4f, 0f, 2f));
        Assert(answered.Task.Wait(TimeSpan.FromSeconds(10)), "A request reaches the hop, which answers rust-plan-none (the pending source).");

        host.SacsRecorderHandler = args => args[0] switch
        {
            "ring-on" => "recorder=on;ring=on",
            "dump" => "seq=10;more=0\n",
            _ => null
        };
        session.PublishPresentationAsync((events, _) => Task.FromResult(true), CancellationToken.None).GetAwaiter().GetResult();
        Assert(host.SacsRecorderCalls.Count(c => c == "rust-route-on") == 1, "ring-on does not send it again.");
    }

    // The bridge reader hands a mod walk's plan request to the hop and keeps it off the client event path.
    [Test("HostBridgeServer.RoutesPlanRequestsToTheHopNotToClients")]
    private static void HostBridgeServerRoutesPlanRequestsToTheHopNotToClients()
    {
        HostBridgeServer bridge = new("memory:TS3W");
        List<RoutePlanRequest> requests = new();
        List<ServerEvent> events = new();
        bridge.RoutePlanRequested += requests.Add;
        bridge.AuthoritativeEvent += events.Add;
        bridge.ProcessResponseLine(RustRoutePlan.BridgeLine(42uL, 1f, 0f, 2f, 4f, 0f, 2f));
        Assert(requests.Count == 1 && requests[0].Sim == 42uL && requests[0].GoalX == 4f, "The request reaches the hop.");
        Assert(events.Count == 0, "And is not a client event.");
        bridge.ProcessResponseLine("EVENT|RoutePlanRequest|42|bad");
        Assert(requests.Count == 1 && events.Count == 0, "A malformed request is dropped, not raised as an event.");
        bridge.ProcessResponseLine("EVENT|CancelIntent|1|2");
        Assert(requests.Count == 1 && events.Count == 1, "Other events still reach clients.");
    }
}
