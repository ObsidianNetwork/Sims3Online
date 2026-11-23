using System.Globalization;
using SimsOnline.DedicatedServer.HostBridge;

namespace SimsOnline.DedicatedServer.Authority;

public interface IRoadPlaceSource
{
    Task<RoadPlaceResult> PlaceAsync(RoadPlaceRequest request, CancellationToken cancellationToken);
}

/// <summary>The daemon's answer, keeping accepted found=0 separate from a named refusal.</summary>
public sealed record RoadPlaceResult(bool Found, int X, int Y, int Z, int Heading, uint LaneId, bool Onscreen,
    string? Hash, string? Refusal)
{
    public static RoadPlaceResult Declined(string reason) => new(false, 0, 0, 0, 0, 0, false, null, reason);

    public static RoadPlaceResult Parse(IReadOnlyList<string> frames, RoadPlaceRequest request)
    {
        string corr = request.Corr.ToString(CultureInfo.InvariantCulture);
        foreach (string frame in frames)
        {
            string[] p = frame.Split('|');
            if (p.Length == 4 && p[0] == "ERR" && p[1] == corr && p[2] == "PlaceOnRoadOffScreen"
                && p[3] is "RoadDataMissing" or "LaneKindUnsupported" or "BadRequest")
                return Declined(p[3]);
            if (p.Length != 11 || p[0] != "Rp" || p[1] != corr
                || p[2] != request.ObjectId.ToString(CultureInfo.InvariantCulture))
                continue;
            if ((p[3] != "0" && p[3] != "1") || (p[9] != "0" && p[9] != "1")
                || !int.TryParse(p[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(p[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y)
                || !int.TryParse(p[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z)
                || !int.TryParse(p[7], NumberStyles.None, CultureInfo.InvariantCulture, out int heading)
                || !uint.TryParse(p[8], NumberStyles.None, CultureInfo.InvariantCulture, out uint lane)
                || heading >= 360000 || Math.Abs((long)x) > 1000000 || Math.Abs((long)y) > 1000000 || Math.Abs((long)z) > 1000000
                || p[10].Length != 16 || !p[10].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                || (p[3] == "0" && (x != 0 || y != 0 || z != 0 || heading != 0 || lane != 0 || p[9] != "0"))
                || (p[3] == "1" && lane == 0))
                return Declined("BadRequest");
            RoadPlaceResult result = new(p[3] == "1", x, y, z, heading, lane, p[9] == "1", p[10], null);
            return result.AnswerHash() == p[10] ? result : Declined("BadRequest");
        }
        return Declined("noDaemon");
    }

    public string AnswerHash()
    {
        byte[] bytes = new byte[22];
        bytes[0] = Found ? (byte)1 : (byte)0;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), X);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), Y);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9), Z);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(13), Heading);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(17), LaneId);
        bytes[21] = Onscreen ? (byte)1 : (byte)0;
        ulong hash = 0xcbf29ce484222325;
        foreach (byte b in bytes) hash = unchecked((hash ^ b) * 0x100000001b3);
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    public string[] Reply(ulong corr)
    {
        string id = corr.ToString(CultureInfo.InvariantCulture);
        if (Refusal != null) return ["rust-road-place-decline", id, Refusal];
        if (!Found) return ["rust-road-place-none", id, Hash ?? AnswerHash()];
        double halfYaw = Heading * Math.PI / 360000.0;
        return ["rust-road-place", id, (X / 100f).ToString("R", CultureInfo.InvariantCulture),
            (Y / 100f).ToString("R", CultureInfo.InvariantCulture), (Z / 100f).ToString("R", CultureInfo.InvariantCulture),
            "0", ((float)Math.Sin(halfYaw)).ToString("R", CultureInfo.InvariantCulture), "0",
            ((float)Math.Cos(halfYaw)).ToString("R", CultureInfo.InvariantCulture),
            LaneId.ToString(CultureInfo.InvariantCulture), Onscreen ? "1" : "0", Hash ?? AnswerHash()];
    }
}

/// <summary>A dedicated, serialized planning attachment. No gameplay command authority is exposed.</summary>
public sealed class SidecarRoadPlaceSource : IRoadPlaceSource, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private RustAuthoritySidecar? _sidecar;

    public async Task<RoadPlaceResult> PlaceAsync(RoadPlaceRequest request, CancellationToken cancellationToken)
    {
        // Acquire before scheduling blocking socket IO so bridge arrival order also preserves monotonic corr order.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                _sidecar ??= RustAuthoritySidecar.OpenRoadPlanning();
                return _sidecar.PlaceOnRoadOffScreen(request);
            }, cancellationToken);
        }
        catch
        {
            _sidecar?.Dispose();
            _sidecar = null;
            throw;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _sidecar?.Dispose();
}

/// <summary>Every placement request gets one accepted answer or named decline, never an implicit found=0.</summary>
public sealed class RoadPlaceHop(IRoadPlaceSource source, Func<IReadOnlyList<string>, CancellationToken, Task<string?>> sacsRecorder)
{
    private long _served, _servedNone, _declined, _sendFaults;
    public long Served => Interlocked.Read(ref _served);
    public long ServedNone => Interlocked.Read(ref _servedNone);
    public long Declined => Interlocked.Read(ref _declined);
    public long SendFaults => Interlocked.Read(ref _sendFaults);

    public async Task HandleAsync(RoadPlaceRequest request, CancellationToken cancellationToken)
    {
        RoadPlaceResult result;
        try { result = await source.PlaceAsync(request, cancellationToken); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rust-road-place] corr {request.Corr} declined: {ex.Message}");
            result = RoadPlaceResult.Declined("noDaemon");
        }
        if (result.Refusal != null) Interlocked.Increment(ref _declined);
        else if (result.Found) Interlocked.Increment(ref _served);
        else Interlocked.Increment(ref _servedNone);
        try { await sacsRecorder(result.Reply(request.Corr), cancellationToken); }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendFaults);
            Console.Error.WriteLine($"[rust-road-place] corr {request.Corr} answer not delivered: {ex.Message}");
        }
    }
}
