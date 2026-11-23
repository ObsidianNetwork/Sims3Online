using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// card 55 R3: a Plan* call posts a request over the bridge and yields until the plan is offered, declined or the
/// timeout passes (RustRoutePlan.RequestAndWait, option b); the controller hop (RoutePlanHop) answers every request with a
/// rust-plan or rust-plan-none verb. The daemon is a test double.
/// </summary>
internal static partial class Program
{
    private sealed class FakeWaitEnv : RustRoutePlan.IWaitEnv
    {
        public bool CanYieldNow = true;
        public int Yields;
        public Action<int>? OnYield;
        public DateTime Now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        public bool CanYield => CanYieldNow;
        public DateTime UtcNow => Now;

        public void Yield()
        {
            Yields++;
            Now = Now.AddMilliseconds(100);
            OnYield?.Invoke(Yields);
        }
    }

    private static readonly float[] WaitPlan = { 0f, 0f, 0f, 2f, 0f, 1f, 4f, 0f, 2f };

    [Test("RustRouteWait.PlanOfferedWhileWaitingIsTaken")]
    private static void RustRouteWaitPlanOfferedWhileWaitingIsTaken()
    {
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        List<string> sent = new();
        RustRoutePlan.RequestSender = sent.Add;
        FakeWaitEnv env = new() { OnYield = n => { if (n == 3) { RoutePlanRequest.TryParse(sent[0], out RoutePlanRequest request); RustRoutePlan.OfferWait(request.Corr, WaitPlan); } } };
        float[]? plan = RustRoutePlan.RequestAndWait(9uL, 0f, 0f, 0f, 4f, 0f, 2f, 0.75, env);
        Assert(plan != null && plan.Length == 9, "The plan offered on the third pass is the answer.");
        Assert(env.Yields == 3, "One Sleep(0) per pass until it arrived. Got " + env.Yields);
        Assert(sent.Count == 1 && RoutePlanRequest.TryParse(sent[0], out RoutePlanRequest request) && request.Corr != 0
            && sent[0].StartsWith("EVENT|RoutePlanRequest|9|0|0|0|4|0|2|"), "One correlated request on the bridge. Got " + string.Join(";", sent));
        Assert(RustRoutePlan.Report().Contains("asked:1"), "Counted. " + RustRoutePlan.Report());
        RustRoutePlan.Reset();
    }

    [Test("RustRouteWait.TimeoutFallsBackToNative")]
    private static void RustRouteWaitTimeoutFallsBackToNative()
    {
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        RustRoutePlan.RequestSender = _ => { };
        FakeWaitEnv env = new();
        float[]? plan = RustRoutePlan.RequestAndWait(9uL, 0f, 0f, 0f, 4f, 0f, 2f, 0.5, env);
        Assert(plan == null, "No plan arrived: null, the caller runs the native search.");
        Assert(env.Yields == 5, "Yields until 0.5 s of clock passed (100 ms a pass). Got " + env.Yields);
        Assert(RustRoutePlan.Report().Contains("waitTimeout:1"), "Counted. " + RustRoutePlan.Report());
        Assert(RustRoutePlan.Decide(true, true, true, false, plan) == RustRoutePlan.FallbackNoPlan, "A timeout is a counted noPlan.");
        RustRoutePlan.Reset();
    }

    [Test("RustRouteWait.DeclineEndsTheWaitEarly")]
    private static void RustRouteWaitDeclineEndsTheWaitEarly()
    {
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        ulong corr = 0;
        RustRoutePlan.RequestSender = line => { RoutePlanRequest.TryParse(line, out RoutePlanRequest r); corr = r.Corr; };
        FakeWaitEnv env = new() { OnYield = n => { if (n == 1) { RustRoutePlan.DeclineWait(corr); } } };
        Assert(RustRoutePlan.RequestAndWait(9uL, 0f, 0f, 0f, 4f, 0f, 2f, 5.0, env) == null, "Declined: null.");
        Assert(env.Yields == 1, "It stopped on the decline, not the 5 s timeout. Got " + env.Yields);
        Assert(RustRoutePlan.Report().Contains("waitDeclined:1"), "Counted. " + RustRoutePlan.Report());
        RustRoutePlan.Reset();
    }

    [Test("RustRouteWait.NoYieldingContextNeverSleepsOrAsks")]
    private static void RustRouteWaitNoYieldingContextNeverSleepsOrAsks()
    {
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        List<string> sent = new();
        RustRoutePlan.RequestSender = sent.Add;
        FakeWaitEnv env = new() { CanYieldNow = false };
        Assert(RustRoutePlan.RequestAndWait(9uL, 0f, 0f, 0f, 4f, 0f, 2f, 0.75, env) == null, "Off a yielding thread: null at once.");
        Assert(env.Yields == 0 && sent.Count == 0, "No Sleep (it would throw) and no request.");
        Assert(RustRoutePlan.Report().Contains("waitNoYield:1"), "Counted. " + RustRoutePlan.Report());
        RustRoutePlan.Enabled = false;
        RustRoutePlan.RequestSender = sent.Add;
        FakeWaitEnv on = new();
        Assert(RustRoutePlan.RequestAndWait(9uL, 0f, 0f, 0f, 4f, 0f, 2f, 0.75, on) == null && sent.Count == 0 && on.Yields == 0, "Switch off: nothing.");
        RustRoutePlan.Reset();
    }

    [Test("RustRouteWait.RequestLineRoundTripsToTheController")]
    private static void RustRouteWaitRequestLineRoundTripsToTheController()
    {
        string line = RustRoutePlan.BridgeLine(123456789012uL, 1.5f, 0.25f, -3f, 10.125f, 0f, 7f);
        Assert(RoutePlanRequest.IsRequest(line), "The controller recognises the mod's line.");
        Assert(RoutePlanRequest.TryParse(line, out RoutePlanRequest r)
            && r.Sim == 123456789012uL && r.StartX == 1.5f && r.StartZ == -3f && r.GoalX == 10.125f && r.GoalZ == 7f, "And parses every field.");
        Assert(!RoutePlanRequest.TryParse("EVENT|RoutePlanRequest|5|1|2|3", out _), "A short line is refused.");
        Assert(!RoutePlanRequest.TryParse("EVENT|RoutePlanRequest|5|1|2|3|4|NaN|6", out _), "A non-finite field is refused.");
        Assert(!RoutePlanRequest.IsRequest("EVENT|CancelIntent|1|2"), "Other events are not requests.");
    }

    [Test("RustRouteWait.HopAnswersEveryRequestOnce")]
    private static void RustRouteWaitHopAnswersEveryRequestOnce()
    {
        RoutePlanRequest request = new(42uL, 0f, 0f, 0f, 4f, 0f, 2f);
        List<string[]> verbs = new();
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>> sacs = (args, _) =>
        {
            verbs.Add(args.ToArray());
            return Task.FromResult<string?>("ok");
        };

        RoutePlanHop planned = new(new DoubleSource(WaitPlan), sacs);
        planned.HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(verbs.Count == 1 && verbs[0][0] == "rust-plan" && verbs[0][1] == "42", "A plan goes down as rust-plan.");
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        Assert(RustRoutePlan.TryParsePoints(verbs[0][2], out float[] points) && RustRoutePlan.Offer(42uL, points), "The mod parses and keeps what the hop sent.");
        Assert(RustRoutePlan.Take(42uL, 4f, 2f)!.SequenceEqual(WaitPlan), "Byte for byte the points the source gave.");
        RustRoutePlan.Reset();

        verbs.Clear();
        new RoutePlanHop(new DoubleSource(null), sacs).HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(verbs.Count == 1 && verbs[0][0] == "rust-plan-none" && verbs[0][1] == "42", "No plan: rust-plan-none, so the wait ends now.");
        verbs.Clear();
        new RoutePlanHop(new DoubleSource(new float[] { 1f, 2f }), sacs).HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(verbs.Count == 1 && verbs[0][0] == "rust-plan-none", "A plan that is not whole triples is a decline.");
        verbs.Clear();
        RoutePlanHop faulting = new(new DoubleSource(null, fault: true), sacs);
        faulting.HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(verbs.Count == 1 && verbs[0][0] == "rust-plan-none" && faulting.Declined == 1, "A source fault is a decline, never silence.");
        verbs.Clear();
        new RoutePlanHop(new KernelPendingRoutePlanSource(), sacs).HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(verbs.Count == 1 && verbs[0][0] == "rust-plan-none", "The real source declines until the kernel sends geometry.");
    }

    private sealed class ResetException : Exception
    {
    }

    // ThreadAbortException has no public constructor.
    private static Exception AbortException() =>
        (Exception)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(System.Threading.ThreadAbortException));

    [Test("RustRouteWait.PumpNeverWaitsAndLateOffersAreStale")]
    private static void RustRouteWaitPumpNeverWaitsAndLateOffersAreStale()
    {
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        List<string> sent = new();
        RustRoutePlan.RequestSender = sent.Add;
        RustRoutePlan.InBridgePump = true;
        FakeWaitEnv env = new();
        Assert(RustRoutePlan.RequestAndWait(9uL, 0f, 0f, 0f, 4f, 0f, 2f, 0.75, env) == null && env.Yields == 0 && sent.Count == 0,
            "Inside the bridge pump: no request, no Sleep, null at once.");
        Assert(RustRoutePlan.Report().Contains("waitInPump:1"), "Counted. " + RustRoutePlan.Report());
        RustRoutePlan.InBridgePump = false;

        Assert(RustRoutePlan.RequestAndWait(9uL, 0f, 0f, 0f, 4f, 0f, 2f, 0.5, env) == null, "Timed out.");
        Assert(RoutePlanRequest.TryParse(sent[0], out RoutePlanRequest expired), "The timed-out request has its own corr.");
        Assert(!RustRoutePlan.OfferWait(expired.Corr, WaitPlan), "An offer after the timeout is stale and refused.");
        Assert(RustRoutePlan.Take(9uL, 4f, 2f) == null, "So the next walk never takes it.");
        Assert(!RustRoutePlan.OfferWait(expired.Corr, WaitPlan), "A duplicate stale offer is also refused.");
        Assert(RustRoutePlan.Offer(9uL, WaitPlan), "Independent front-door plans remain available.");
        RustRoutePlan.Reset();
    }

    [Test("RustRouteWait.ResetAndAbortAreNotSwallowed")]
    private static void RustRouteWaitResetAndAbortAreNotSwallowed()
    {
        Assert(RustRoutePlan.MustRethrow(new ResetException()), "A Sim reset out of Sleep goes up (matched by type name).");
        Assert(RustRoutePlan.MustRethrow(AbortException()), "A thread abort goes up.");
        Assert(!RustRoutePlan.MustRethrow(new InvalidOperationException("x")) && !RustRoutePlan.MustRethrow(null), "Anything else is the seam's own fault: native fallback.");
        RustRoutePlan.Reset();
        RustRoutePlan.Enabled = true;
        RustRoutePlan.RequestSender = _ => { };
        FakeWaitEnv env = new() { OnYield = _ => throw new ResetException() };
        bool thrown = false;
        try
        {
            RustRoutePlan.RequestAndWait(9uL, 0f, 0f, 0f, 4f, 0f, 2f, 0.75, env);
        }
        catch (ResetException)
        {
            thrown = true;
        }

        Assert(thrown, "The wait lets a reset raised by the yield through.");
        RustRoutePlan.Reset();
    }

    [Test("RustRouteWait.HopLogsAndCountsSendFaults")]
    private static void RustRouteWaitHopLogsAndCountsSendFaults()
    {
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>> down = (args, _) => throw new InvalidOperationException("bridge down");
        RoutePlanHop hop = new(new DoubleSource(WaitPlan), down);
        hop.HandleAsync(new RoutePlanRequest(42uL, 0f, 0f, 0f, 4f, 0f, 2f), CancellationToken.None).GetAwaiter().GetResult();
        Assert(hop.SendFaults == 1, "A failed send is counted (and written to stderr), not an unobserved task fault.");
    }

    private sealed class DoubleSource : IRoutePlanSource
    {
        private readonly float[]? _plan;
        private readonly bool _fault;

        public DoubleSource(float[]? plan, bool fault = false)
        {
            _plan = plan;
            _fault = fault;
        }

        public Task<float[]?> PlanAsync(RoutePlanRequest request, CancellationToken cancellationToken) =>
            _fault ? throw new InvalidOperationException("daemon down") : Task.FromResult(_plan);
    }
}
