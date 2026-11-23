using SimsOnlineServerHost;
#if OMO_FORK_HOOKS
using SimsOnline.Hooks;
#endif

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
#if OMO_FORK_HOOKS
    [Test("ClosedTesting.AutonomyScoringRespectsAuthorityMode")]
    private static void AutonomyScoringRespectsAuthorityMode()
    {
        try
        {
            foreach (int mode in new[] { AuthorityHook.ModeOff, AuthorityHook.ModeObserve, AuthorityHook.ModeEnforce })
            {
                AuthorityHook.SetMode(mode);
                Assert(AuthorityHook.SkipAutonomy() == (mode == AuthorityHook.ModeEnforce),
                    "Only enforcing clients skip scoring; the server and observation retain execution.");
                Assert(AuthorityHook.AllowInsert(20, 1, 4, 0, "AutonomousAction")
                    == (mode != AuthorityHook.ModeEnforce), "Autonomous queue insertion must obey the same mode.");
            }
        }
        finally
        {
            AuthorityHook.SetMode(AuthorityHook.ModeOff);
        }
    }

    [Test("ClosedTesting.AutonomyCannotUseOwnedExemption")]
    private static void AutonomyCannotUseOwnedExemption()
    {
        AuthorityHook.SetMode(AuthorityHook.ModeEnforce);
        AuthorityHook.SetOwnedSims(new ulong[] { 17 });
        try
        {
            Assert(!AuthorityHook.AllowInsert(17, 1, 1, 0, "AutonomousAction"),
                "Client autonomous work must be blocked even on a native/owned Sim.");
        }
        finally
        {
            AuthorityHook.SetOwnedSims(new ulong[0]);
            AuthorityHook.SetMode(AuthorityHook.ModeOff);
        }
    }

    [Test("ClosedTesting.AutonomyCannotUseServerContinuationExemption")]
    private static void AutonomyCannotUseServerContinuationExemption()
    {
        AuthorityHook.SetMode(AuthorityHook.ModeEnforce);
        AuthorityHook.BeginServerApply();
        try
        {
            Assert(AuthorityHook.AllowInsert(18, 0, 2, 987, "ServerAction"),
                "The server-applied group must be registered.");
        }
        finally
        {
            AuthorityHook.EndServerApply();
        }
        try
        {
            Assert(!AuthorityHook.AllowInsert(18, 1 | 8, 3, 987, "AutonomousContinuation"),
                "A local autonomous continuation must not inherit server authority.");
        }
        finally
        {
            AuthorityHook.SetMode(AuthorityHook.ModeOff);
        }
    }
#endif

    [Test("ClosedTesting.OffscopeConversationEndStillReachesClient")]
    private static void OffscopeConversationEndStillReachesClient()
    {
        ConversationPublisher publisher = new();
        publisher.Pass(new List<ConversationState> { Conv(true, 11, 12) }, false);
        List<string> ended = publisher.Pass(new List<ConversationState>(), false);
        Assert(ended.Count == 1 && ConversationState.Parse(("9|" + ended[0]).Split('|'), out var state)
            && state.ShouldForwardTo(new Dictionary<ulong, bool> { { 99, true } }),
            "A conversation end must reach copies after every participant leaves the current stream scope.");
    }

    [Test("ClosedTesting.ConversationStartsAndAbsenceRemainScoped")]
    private static void ConversationStartsAndAbsenceRemainScoped()
    {
        Dictionary<ulong, bool> targets = new() { { 22, true } };
        Assert(!Conv(true, 11, 12).ShouldForwardTo(targets)
            && !ConversationState.Absent(11).ShouldForwardTo(targets),
            "Out-of-scope conversation starts and absence snapshots must not be broadcast.");
        Assert(Conv(true, 11, 22).ShouldForwardTo(targets)
            && ConversationState.Absent(22).ShouldForwardTo(targets),
            "Starts and absence snapshots still reach a current member's client.");
    }
}
