using SimsOnline.DedicatedServer.HostBridge;

namespace SimsOnline.DedicatedServer.Authority;

/// <summary>Where a walk's plan comes from: the daemon through the sidecar. Null means no plan (the mod walks natively).</summary>
public interface IRoutePlanSource
{
    Task<float[]?> PlanAsync(RoutePlanRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The kernel's NavigateAStar answer has no geometry yet and no world-to-cell mapping exists for a live lot
/// (docs/card55-route-report.md section C), so the real source declines every request. It is the seam a real source
/// replaces; until then each walk waits for the decline, not the timeout, and takes the native search.
/// </summary>
public sealed class KernelPendingRoutePlanSource : IRoutePlanSource
{
    public Task<float[]?> PlanAsync(RoutePlanRequest request, CancellationToken cancellationToken) => Task.FromResult<float[]?>(null);
}

/// <summary>
/// card 55 R3, the controller hop: receive the mod's request, ask the source (the daemon through the sidecar), send
/// the answer back down as SACS_RECORDER <c>rust-plan</c> (the mod's <c>RustRoutePlan.Offer</c>) or <c>rust-plan-none</c>.
/// Every request gets exactly one answer, so a waiting Sim ends on the answer, not the timeout; a source fault is a decline.
/// </summary>
public sealed class RoutePlanHop
{
    private readonly IRoutePlanSource _source;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<string?>> _sacsRecorder;
    private long _planned;
    private long _declined;
    private long _sendFaults;

    public RoutePlanHop(IRoutePlanSource source, Func<IReadOnlyList<string>, CancellationToken, Task<string?>> sacsRecorder)
    {
        _source = source;
        _sacsRecorder = sacsRecorder;
    }

    public long Planned => Interlocked.Read(ref _planned);

    public long Declined => Interlocked.Read(ref _declined);

    /// <summary>Answers the bridge refused or timed out on; the waiting Sim then ends on its own timeout.</summary>
    public long SendFaults => Interlocked.Read(ref _sendFaults);

    public async Task HandleAsync(RoutePlanRequest request, CancellationToken cancellationToken)
    {
        float[]? plan = null;
        try
        {
            plan = await _source.PlanAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[rust-route] plan source fault for sim {request.Sim} (declined): {ex.Message}");
        }

        string sim = request.Sim.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (IsWhole(plan))
        {
            Interlocked.Increment(ref _planned);
            await SendAsync(request.Corr == 0
                ? new[] { "rust-plan", sim, RoutePlanRequest.FormatPoints(plan!) }
                : new[] { "rust-plan", sim, RoutePlanRequest.FormatPoints(plan!), request.Corr.ToString(System.Globalization.CultureInfo.InvariantCulture) }, request, cancellationToken);
            return;
        }

        Interlocked.Increment(ref _declined);
        await SendAsync(request.Corr == 0 ? new[] { "rust-plan-none", sim }
            : new[] { "rust-plan-none", sim, request.Corr.ToString(System.Globalization.CultureInfo.InvariantCulture) }, request, cancellationToken);
    }

    // The hop runs fire-and-forget off the bridge reader, so a send fault would otherwise be an unobserved task exception.
    private async Task SendAsync(string[] args, RoutePlanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _sacsRecorder(args, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _sendFaults);
            Console.Error.WriteLine($"[rust-route] {args[0]} for sim {request.Sim} not delivered: {ex.Message}");
        }
    }

    private static bool IsWhole(float[]? xyz)
    {
        if (xyz == null || xyz.Length < 6 || xyz.Length % 3 != 0)
        {
            return false;
        }

        foreach (float v in xyz)
        {
            if (!float.IsFinite(v))
            {
                return false;
            }
        }

        return true;
    }
}
