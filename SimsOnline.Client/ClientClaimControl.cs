using System.Globalization;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.Client;

// Loopback reset control uses the normal public command and waits for the server's ownership event.
internal sealed class ClientClaimControl
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly HashSet<ulong> _owned = new();
    private TaskCompletionSource<bool>? _pending;
    private ulong _requested;

    internal Func<ClientCommand, Task>? Send { get; set; }

    internal void Observe(ServerEvent evt, int player)
    {
        lock (_sync)
        {
            if (evt is ServerEvent.SimClaimed claimed && claimed.PlayerId == player)
            {
                _owned.Add(claimed.SimObjectId);
                if (claimed.SimObjectId == _requested)
                    _pending?.TrySetResult(true);
            }
            else if (evt is ServerEvent.SimReleased released && released.PlayerId == player)
                _owned.Remove(released.SimObjectId);
            else if (evt is ServerEvent.CommandRejected rejected && rejected.CommandId == _command)
                _pending?.TrySetException(new InvalidOperationException(rejected.Reason));
        }
    }

    private Guid _command;

    internal string? Handle(string name, string[] args, int player)
    {
        if (name == "CLIENT_CLAIMS")
        {
            lock (_sync)
                return "claims=" + string.Join(",", _owned.OrderBy(id => id));
        }
        if (name != "CLIENT_CLAIM") return null;
        if (args.Length != 1 || !ulong.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong sim) || sim == 0)
            return "claim_error=invalid_sim";
        return ClaimAsync(player, sim).GetAwaiter().GetResult();
    }

    private async Task<string> ClaimAsync(int player, ulong sim)
    {
        await _requests.WaitAsync();
        try
        {
            Func<ClientCommand, Task>? send = Send;
            if (send == null) return "claim_error=not_connected";
            TaskCompletionSource<bool> acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Guid command = Guid.NewGuid();
            lock (_sync)
            {
                _pending = acknowledgement;
                _requested = sim;
                _command = command;
            }
            await send(new ClientCommand.ClaimSim(command, player, sim));
            await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return Handle("CLIENT_CLAIMS", Array.Empty<string>(), player)!;
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or IOException)
        {
            return "claim_error=" + error.Message;
        }
        finally
        {
            lock (_sync) _pending = null;
            _requests.Release();
        }
    }
}
