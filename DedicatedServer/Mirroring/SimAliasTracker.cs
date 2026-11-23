using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// W2 Sim identity (<c>library/engine/sim-identity-and-npc-creation.md</c>): only Sims in the world at
/// load share an ObjectId between the server and a client; a Sim instantiated after load gets a
/// different one on each TS3. The server snapshot carries each Sim's SimDescriptionId; this tracker
/// turns the pairs the client TS3 has not been told yet into SET_SIM_ALIASES commands, so the mod
/// resolves every later command for that server ObjectId to its own instance of the Sim.
/// A FullSnapshot is an authoritative reset: the whole table is resent with reset on its first chunk.
/// </summary>
public sealed class SimAliasTracker
{
    private readonly Dictionary<ulong, ulong> _sent = new();

    public int Count => _sent.Count;

    /// <summary>The client mod dropped this alias (DESPAWN_SIM); send it again if the id comes back.</summary>
    public void Forget(ulong serverObjectId) => _sent.Remove(serverObjectId);

    public IReadOnlyList<BridgeCommand> OnSnapshot(IReadOnlyList<SimSnapshot> sims, bool fullSnapshot)
    {
        bool hadAny = _sent.Count > 0;
        if (fullSnapshot)
        {
            _sent.Clear();
        }

        List<SimAlias> pending = new();
        foreach (SimSnapshot sim in sims)
        {
            if (sim.SimDescriptionId is not ulong descriptionId || descriptionId == 0)
            {
                continue;
            }

            if (_sent.TryGetValue(sim.ObjectId, out ulong known) && known == descriptionId)
            {
                continue;
            }

            _sent[sim.ObjectId] = descriptionId;
            pending.Add(new SimAlias(sim.ObjectId, descriptionId));
        }

        bool reset = fullSnapshot && (hadAny || pending.Count > 0);
        if (pending.Count == 0 && !reset)
        {
            return Array.Empty<BridgeCommand>();
        }

        List<BridgeCommand> commands = new();
        int max = SimsOnlineServerHost.SimAliasWire.MaxPairsPerCommand;
        for (int start = 0; start < pending.Count || (start == 0 && reset); start += max)
        {
            List<SimAlias> chunk = pending.GetRange(start, Math.Min(max, pending.Count - start));
            commands.Add(new BridgeCommand.SetSimAliases(Guid.NewGuid(), chunk, Reset: reset && start == 0));
        }

        return commands;
    }
}
