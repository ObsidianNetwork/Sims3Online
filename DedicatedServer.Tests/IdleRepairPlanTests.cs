using System.Collections.Generic;
using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// B2: what RepairOwnMachines enters on a puppet's dead standing idle machine. The stream's latest state for a
/// server idle driver borrowed onto that machine, or standing_idle without one; the engine's creation entry (standing)
/// when the stream carries no idle for the Sim or does not own it.
/// </summary>
internal static partial class Program
{
    [Test("IdleRepairPlan.StreamedIdleEntersServerState")]
    private static void IdleRepairPlanStreamedIdleEntersServerState()
    {
        Assert(IdleRepairPlan.Decide(true, true, true, true, true) == IdleRepairPlan.ServerState,
            "A dead machine, the server's Sim standing, the stream owning the idle, a mapped server idle driver with a known state: that state.");
        Assert(IdleRepairPlan.Kind(IdleRepairPlan.ServerState) == "/stream", "Its label kind: repair:<caller>/stream.");
    }

    [Test("IdleRepairPlan.StreamedIdleWithoutStateEntersStandingIdle")]
    private static void IdleRepairPlanStreamedIdleWithoutStateEntersStandingIdle()
    {
        Assert(IdleRepairPlan.Decide(true, true, true, true, false) == IdleRepairPlan.StandingIdle,
            "A mapped server idle driver whose state the stream never gave: standing_idle.");
        Assert(IdleRepairPlan.StandingIdleState == "standing_idle" && IdleRepairPlan.Kind(IdleRepairPlan.StandingIdle) == "/stream",
            "The engine's own recovery state, under the stream label.");
    }

    [Test("IdleRepairPlan.NoStreamedIdleKeepsEngineEntry")]
    private static void IdleRepairPlanNoStreamedIdleKeepsEngineEntry()
    {
        Assert(IdleRepairPlan.Decide(true, true, true, false, false) == IdleRepairPlan.EngineDefault,
            "No server idle driver on the dead machine: the engine's own creation (StartHighLODIdle).");
        Assert(IdleRepairPlan.Kind(IdleRepairPlan.EngineDefault) == "/default", "Its label kind: repair:<caller>/default.");
    }

    [Test("IdleRepairPlan.NothingToDo")]
    private static void IdleRepairPlanNothingToDo()
    {
        Assert(IdleRepairPlan.Decide(false, true, true, true, true) == IdleRepairPlan.Nothing, "A live machine: nothing.");
        Assert(IdleRepairPlan.Decide(true, false, true, true, true) == IdleRepairPlan.Nothing, "The server's Sim not standing: nothing.");
        Assert(IdleRepairPlan.Decide(true, true, false, true, true) == IdleRepairPlan.EngineDefault,
            "Presenter off or a Sim the stream does not own: today's repair, the engine's creation.");
    }

    [Test("ReplayStateCache.KeepsLatestEnteredOrRequestedState")]
    private static void ReplayStateCacheKeepsLatestEnteredOrRequestedState()
    {
        ReplayStateCache cache = new ReplayStateCache();
        cache.Set(5, 84696423, GateStanding);     // an E
        cache.Set(5, 84696423, GateStandingIdle); // then a request the replay applied
        Assert(cache.TryGet(5, out uint actor, out uint state) && actor == 84696423 && state == GateStandingIdle,
            "The latest of an entry and an applied request.");
        cache.Set(6, 84696423, GateStanding);
        Assert(cache.Latest(new List<uint> { 5, 6 }, out uint server, out _, out state) && server == 6 && state == GateStanding,
            "Of several drivers, the one set last.");
        Assert(!cache.Latest(new List<uint> { 9 }, out _, out _, out _), "A driver it never saw: none.");
        cache.Forget(6);
        Assert(!cache.TryGet(6, out _, out _) && cache.Count == 1, "A release forgets it.");

        ReplayStateCache bounded = new ReplayStateCache();
        for (uint driver = 1; driver <= ReplayStateCache.Capacity + 1; driver++)
        {
            bounded.Set(driver, 84696423, GateStandingIdle);
        }

        Assert(bounded.Count == ReplayStateCache.Capacity && !bounded.TryGet(1, out _, out _) && bounded.TryGet(2, out _, out _),
            "Bounded: the least recently set goes first.");
    }

    [Test("ReplayDriverMap.FindsServerDriversBorrowedOntoHandle")]
    private static void ReplayDriverMapFindsServerDriversBorrowedOntoHandle()
    {
        Dictionary<uint, uint> drivers = new Dictionary<uint, uint> { { 5, 7 }, { 6, 7 }, { 9, 8 }, { 10, 7 } };
        Dictionary<uint, bool> borrowed = new Dictionary<uint, bool> { { 5, true }, { 6, true }, { 9, true } };
        List<uint> found = IdleRepairPlan.ServerDriversOn(7, drivers, borrowed);
        Assert(found.Count == 2 && found.Contains(5) && found.Contains(6),
            "The server drivers borrowed onto the handle; one the replay acquired with the same value is not borrowed.");
        Assert(IdleRepairPlan.ServerDriversOn(0, drivers, borrowed).Count == 0, "No handle: none.");
    }
}
