// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.

namespace SimsOnlineServerHost
{
    // why the mod selects a Sim. ServerHostMain.SelectSim is the one place the mod moves the plumb bob
    // (the test SelectionGuard.ModSelectsOnlyThroughTheChokePoint keeps it so), and it asks KeepsSelection first.
    internal enum SelectCause
    {
        // The player's own select: the controller's SET_ACTIVE_SIM (the claim's select and its re-assert) and
        // INJECT_CLICK, which stands in for the player's click.
        Player,

        // Applying the server's interaction to its actor: APPLY_INTERACTION_QUEUE, APPLY_INTERACTIONS, INJECT_READ_INTENT.
        Replay
    }

    internal static class SelectionGuard
    {
        // On the client role the selected Sim is the player's, so a replay never moves it: the interaction is queued on
        // its actor without the select. Replay used to select every actor it applied to, so a rejoin read back another
        // household's Sim (internal documentation, cause 1). The server role never selected for a replay
        // (APPLY_INTERACTION_QUEUE selected only on the client; INJECT_READ_INTENT is client-only), so a replay keeps the
        // selection on either role. The player's own selects go through.
        internal static bool KeepsSelection(SelectCause cause)
        {
            return cause == SelectCause.Replay;
        }
    }
}
