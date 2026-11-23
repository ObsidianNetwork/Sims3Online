using SimsOnlineServerHost;

namespace SimsOnline.Presentation
{
    // The client presentation module (SimsOnlineServerHostMod/PresentationModules.cs): the stream kinds it handles, its
    // teardown and its counters. Packaged as a second script assembly (SimsOnlinePresentation) and reloadable in a running
    // client with scripts/presentation_reload.py. Code moves here kind by kind; whatever is not registered here stays
    // with the shell's built-in replay. The module's empty catches are not in the shell's DIAG_STATE swallowed= count:
    // calling a new shell member would break reloading the module into an older shell. Each says why it swallows.
    public static class Module
    {
        public static void Start()
        {
            PresentationModules.Register("Bf", new PresentationLineHandler(Moodlets.Replay));
            PresentationModules.Register("Iv", new PresentationLineHandler(Inventories.Replay));
            PresentationModules.Register("Rf", new PresentationLineHandler(Refusals.Replay));
            PresentationModules.Register("Us", new PresentationLineHandler(Uses.Replay));
            PresentationModules.Register("Ne", new PresentationLineHandler(Walks.ReplayNavEpoch));
            PresentationModules.Register("Rt", new PresentationLineHandler(Walks.ReplayRoute));
            PresentationModules.Register("Vp", new PresentationLineHandler(Walks.ReplayRoutePoint));
            PresentationModules.Register("Rs", new PresentationLineHandler(Walks.ReplayRoute));
            PresentationModules.Register("Rte", new PresentationLineHandler(Walks.ReplayRouteEnd));
            PresentationModules.Register("Rx", new PresentationLineHandler(Walks.ReplayRouteEnd));
            PresentationModules.Register("Rh", new PresentationLineHandler(Walks.ReplayRouteHalt));
            PresentationModules.Register("Ru", new PresentationLineHandler(Walks.ReplayRouteStyle));
            PresentationModules.Register("Pz", new PresentationLineHandler(Walks.ReplayStreamPosition));
            PresentationModules.OnTick(new PresentationTick(Walks.Tick));
            PresentationModules.OnWalkState(new PresentationWalkState(Walks.State));
            PresentationModules.OnReset(new PresentationReset(Reset));
            PresentationModules.OnReport(new PresentationReport(Report));
        }

        private static void Reset()
        {
            ClientBoundaryRoute.Receipts.Clear();
            Moodlets.Reset();
            Inventories.Reset();
            Refusals.Reset();
            Uses.Reset();
            Walks.Reset();
        }

        // Inside the shell's SACS_REPLAY 00 report: ";" starts the module's own fields (positions=, routes=).
        private static string Report()
        {
            return "moodlets:" + Moodlets.Report() + ",inventories:" + Inventories.Report() + ",refusals:" + Refusals.Report() + ",uses:" + Uses.Report() + ";" + Walks.Report();
        }
    }
}
