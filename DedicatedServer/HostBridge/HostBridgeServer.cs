using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SimsOnline.DedicatedServer.Protocol;
using SimsOnline.DedicatedServer.Observability;

namespace SimsOnline.DedicatedServer.HostBridge;

public sealed class HostBridgeServer : IAsyncDisposable
{
    private const int MaxSnapshotChunks = 4096;

    private readonly string _endpoint;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<BridgeResponse>> _pending = new();
    private readonly ConcurrentDictionary<Guid, ChunkBuffer> _chunks = new();
    private readonly ConcurrentDictionary<Guid, BulkPartBuffer> _bulkParts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // Cancelled first thing in DisposeAsync; wakes every send parked on _writeLock (EnterWriteLockAsync).
    private readonly CancellationTokenSource _disposeCancellation = new();
    private int _beaconPingInFlight;
    private ProcessMemoryBridge? _memoryBridge;
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;

    /// <summary>
    /// Fires when the script DLL pushes an asynchronous EVENT line (interaction lifecycle, errors, etc).
    /// Subscribers should be cheap and non-blocking; heavy work belongs on a separate task.
    /// contract: handlers run on the bridge reader thread and MUST NOT synchronously
    /// wait on SendAsync — the reader is the only thing that can resolve an in-flight command,
    /// so a blocking re-entrant send deadlocks the bridge. Fire-and-forget instead.
    /// </summary>
    public event Action<ServerEvent>? AuthoritativeEvent;

    /// <summary> card 55 R3: a server-mod walk asks for a Rust plan. Bridge-only: never forwarded to clients.</summary>
    public event Action<RoutePlanRequest>? RoutePlanRequested;
    public event Action<RoadPlaceRequest>? RoadPlaceRequested;

    /// <summary>
    /// instrumentation: low-volume diagnostic lines about bridge internals (chunk arrival
    /// cadence, stalls, completion timing). Intended for console logging during probes/serve.
    /// </summary>
    public event Action<string>? Diagnostic;

    /// <summary> --bridge-record: lossless per-frame capture.</summary>
    public event Action<string>? FrameRecord;

    /// <summary>build stamp from the mod's HELLO line ({gitSha}@{builtUtc}), if present.</summary>
    public string? ModBuildStamp { get; private set; }

    public HostBridgeServer(string endpoint)
    {
        if (UnsupportedEndpointError(endpoint) is { } error)
        {
            throw new ArgumentException(error);
        }

        _endpoint = endpoint;
    }

    /// <summary>
    /// Why a host-bridge endpoint cannot be used, or null. The mod talks only through process memory: its file
    /// bridge was deleted (2026-10-21) and its System.dll has no sockets, so a file: or address:port endpoint
    /// would wait for a HELLO that never comes. Both controllers check their bridge argument with this before
    /// anything starts (an argument error, not a crash report); the constructor checks it again.
    /// </summary>
    public static string? UnsupportedEndpointError(string endpoint) =>
        endpoint.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? $"The file: host bridge endpoint is no longer supported: the mod talks only over the process-memory bridge. Use memory:<process> (default memory:TS3W). Value: {endpoint}"
            : !endpoint.StartsWith("memory:", StringComparison.OrdinalIgnoreCase)
                ? $"Unsupported host bridge endpoint: the mod talks only over the process-memory bridge, and the game has no sockets, so an address:port endpoint has nothing to connect to. Use memory:<process> (default memory:TS3W). Value: {endpoint}"
                : null;

    public bool IsConnected => _memoryBridge?.IsConnected == true;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _memoryBridge = new ProcessMemoryBridge(ParseMemoryEndpoint(_endpoint));
        // --bridge-trace: surface per-frame protocol traces through the existing
        // Diagnostic channel so probe/serve modes can log them.
        _memoryBridge.Trace += line => Diagnostic?.Invoke(line);
        // --bridge-record: lossless frame capture for replay/forensics.
        _memoryBridge.FrameRecord += line => FrameRecord?.Invoke(line);
        return Task.CompletedTask;
    }

    public async Task WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken, int? hostProcessId = null)
    {
        if (_memoryBridge == null)
        {
            throw new InvalidOperationException("Process-memory bridge has not been started.");
        }

        string memoryHello = await _memoryBridge.ConnectAsync(hostProcessId, timeout, cancellationToken);
        if (!BridgeLineCodec.IsHello(memoryHello))
        {
            SimsOnlineTelemetry.Current?.EmitBridgeHelloMissing(bridgeEndpoint: null, protocolVersion: BridgeLineCodec.ProtocolVersion);
            throw new InvalidOperationException("Sims 3 process-memory bridge connected but did not return a valid HELLO line.");
        }

        CaptureModBuildStamp(memoryHello);

        _readerCancellation = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReadMemoryLoopAsync(_readerCancellation.Token), CancellationToken.None);
    }

    private void CaptureModBuildStamp(string helloLine)
    {
        if (!BridgeLineCodec.TryParseHello(helloLine, out BridgeLineCodec.HelloParseResult parsed))
        {
            return;
        }

        ModBuildStamp = parsed.BuildStamp;
        if (!string.IsNullOrWhiteSpace(parsed.Caps))
        {
            Diagnostic?.Invoke($"mod_caps={parsed.Caps}");
        }
        if (!string.IsNullOrWhiteSpace(parsed.BuildStamp))
        {
            Diagnostic?.Invoke($"mod_build={parsed.BuildStamp}");
        }
    }

    public async Task<BridgeResponse> SendAsync(BridgeCommand command, CancellationToken cancellationToken)
    {
        if (_memoryBridge == null || !_memoryBridge.IsConnected)
        {
            throw new InvalidOperationException("Sims 3 process-memory bridge is not connected.");
        }

        // responses correlate through _pending. The reader loop is the only frame
        // consumer; the old in-place response polling raced it, and a response consumed by the
        // wrong party was dropped on the floor while the command waited out its full timeout.
        TaskCompletionSource<BridgeResponse> memoryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(command.CorrelationId, memoryCompletion))
        {
            throw new InvalidOperationException($"Duplicate bridge command correlation id {command.CorrelationId}.");
        }

        try
        {
            // The write-lock is held until the response arrives: the single 4 KB frame slot
            // supports exactly one in-flight command. A timeout cancels the await, releases
            // the lock in seconds (see SessionConfig.EffectivePlayerCommandTimeout), and the
            // late response is consumed by the reader loop and dropped with a diagnostic
            // instead of wedging every queued command behind a dead one.
            await EnterWriteLockAsync(cancellationToken);
            try
            {
                _memoryBridge.WriteCommand(BridgeLineCodec.EncodeCommand(command), out string? displacedHostLine);
                if (displacedHostLine != null && !BridgeLineCodec.IsHello(displacedHostLine))
                {
                    SafeProcessResponseLine(displacedHostLine);
                }

                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    if (_pending.TryRemove(command.CorrelationId, out TaskCompletionSource<BridgeResponse>? pending))
                    {
                        pending.TrySetCanceled(cancellationToken);
                    }
                });

                return await memoryCompletion.Task;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _pending.TryRemove(command.CorrelationId, out _);
        }
    }

    /// <summary>
    /// memory-bridge send that always writes compat slot 0 (beacon-refutation PING).
    /// </summary>
    private async Task<BridgeResponse> SendCompatAsync(BridgeCommand command, CancellationToken cancellationToken)
    {
        if (_memoryBridge == null || !_memoryBridge.IsConnected)
        {
            throw new InvalidOperationException("Sims 3 process-memory bridge is not connected.");
        }

        TaskCompletionSource<BridgeResponse> memoryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(command.CorrelationId, memoryCompletion))
        {
            throw new InvalidOperationException($"Duplicate bridge command correlation id {command.CorrelationId}.");
        }

        try
        {
            await EnterWriteLockAsync(cancellationToken);
            try
            {
                _memoryBridge.WriteCompatCommand(BridgeLineCodec.EncodeCommand(command), out string? displacedHostLine);
                if (displacedHostLine != null && !BridgeLineCodec.IsHello(displacedHostLine))
                {
                    SafeProcessResponseLine(displacedHostLine);
                }

                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    if (_pending.TryRemove(command.CorrelationId, out TaskCompletionSource<BridgeResponse>? pending))
                    {
                        pending.TrySetCanceled(cancellationToken);
                    }
                });

                return await memoryCompletion.Task;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _pending.TryRemove(command.CorrelationId, out _);
        }
    }

    /// <summary>
    /// Takes <see cref="_writeLock"/>, or throws <see cref="OperationCanceledException"/> once DisposeAsync has
    /// started, so a send parked here with a token that never cancels neither waits forever nor writes to a
    /// bridge being torn down. Callers add their command to _pending before calling this: DisposeAsync cancels
    /// the token before it cancels _pending, so a send either sees the token or has its completion cancelled.
    /// </summary>
    private async Task EnterWriteLockAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
        await _writeLock.WaitAsync(waitCancellation.Token);
        if (_disposeCancellation.IsCancellationRequested)
        {
            // SemaphoreSlim can still hand the lock to a waiter whose token was cancelled a moment earlier.
            _writeLock.Release();
            throw new OperationCanceledException(_disposeCancellation.Token);
        }
    }

    /// <summary> Phase 2a inbound: DMA-write a ctl bulk slot, then BULK_REF probe-upload on the control ring.</summary>
    public async Task<BridgeResponse> UploadInboundBlobAsync(string blobId, byte[] payload, CancellationToken cancellationToken)
    {
        if (_memoryBridge == null || !_memoryBridge.IsConnected || !_memoryBridge.LayoutV11)
        {
            return BridgeResponse.Rejected(Guid.NewGuid(), "bulk upload requires a connected v11 memory bridge");
        }

        if (payload.Length <= 0 || payload.Length > BridgeSharedRegionLayout.BulkSlotPayloadMax)
        {
            return BridgeResponse.Rejected(Guid.NewGuid(), "payload must fit a single 512 KiB bulk slot");
        }

        Guid correlationId = Guid.NewGuid();
        if (!_memoryBridge.TryWriteCtlBulkPayload(correlationId, payload, out int slotIndex, out uint crc32))
        {
            return BridgeResponse.Rejected(correlationId, "ctl bulk slot write failed");
        }

        return await SendAsync(new BridgeCommand.Raw(
            correlationId,
            "BULK_REF",
            new[]
            {
                "probe-upload",
                slotIndex.ToString(CultureInfo.InvariantCulture),
                payload.Length.ToString(CultureInfo.InvariantCulture),
                crc32.ToString("X8", CultureInfo.InvariantCulture),
                BridgeLineCodec.Text(blobId),
            }), cancellationToken);
    }

    public HostBulkReadResult ReadHostBulkPayload(
        Guid expectedCorrelationId,
        int slotIndex,
        int expectedLength,
        uint expectedCrc32)
    {
        if (_memoryBridge == null)
        {
            return HostBulkReadResult.Failure(
                HouseholdExportTransportError.BridgeUnavailable,
                "Household exports require the process-memory bridge BULK region.");
        }

        return _memoryBridge.ReadHostBulkPayload(
            expectedCorrelationId,
            slotIndex,
            expectedLength,
            expectedCrc32);
    }

    /// <summary>
    /// follow-up (2026-07-03): tell the mod this controller session is over.
    /// Fire-and-forget — the controller is exiting and will not consume a response, so this
    /// bypasses _pending and the write lock's response-hold. Failure is logged and swallowed: a crash-killed controller never
    /// sends this either, and the mod's 10s evidence window still covers that path.
    /// </summary>
    public void SendDisconnectBestEffort()
    {
        if (_memoryBridge == null || !_memoryBridge.IsConnected)
        {
            return;
        }

        try
        {
            _memoryBridge.WriteCommand(
                BridgeLineCodec.EncodeCommand(new BridgeCommand.Disconnect(Guid.NewGuid())),
                out _);
            Diagnostic?.Invoke("DISCONNECT sent (graceful controller shutdown)");
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"DISCONNECT send failed (best-effort): {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Order matters: wake every waiting send and stop the reader first, and close the memory bridge only once
        // the reader has stopped reading it (it reads a frame and then stamps it consumed) and no send is writing.
        _disposeCancellation.Cancel();
        _readerCancellation?.Cancel();

        foreach (TaskCompletionSource<BridgeResponse> pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }

        if (_readerTask != null)
        {
            try
            {
                await _readerTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Cancelled above, the reader returns on its own. A fault here (a host that died mid-read) only
                // ends a session that is already over, so log it and go on.
                Diagnostic?.Invoke($"bridge reader fault during shutdown (ignored): {ex.Message}");
            }
        }

        // A send that took the lock just before the token was cancelled may still be writing its frame. Its
        // completion was cancelled above, so it leaves the lock as soon as the write returns.
        await _writeLock.WaitAsync(CancellationToken.None);
        try
        {
            _memoryBridge?.Dispose();
        }
        finally
        {
            _writeLock.Release();
        }

        // _writeLock is not disposed: a waiting send that SemaphoreSlim hands the lock to as its wait is cancelled
        // still releases it in EnterWriteLockAsync, possibly after this returns, and releasing a disposed
        // SemaphoreSlim throws ObjectDisposedException over the caller's cancellation. Nothing uses its wait handle.
    }

    private async Task ReadMemoryLoopAsync(CancellationToken cancellationToken)
    {
        // single consumer for every post-connect host frame (events, responses,
        // snapshot chunks). No write-lock gate — commands resolve through _pending, so the
        // reader must keep consuming while a command is in flight. Greedy inner drain keeps
        // chunked snapshot throughput at the consumed-stamp ping-pong rate. The drain checks the token per
        // frame: DisposeAsync waits for this loop before closing the bridge, so a long host stream must not hold it.
        DateTime lastBeaconPingUtc = DateTime.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            while (!cancellationToken.IsCancellationRequested && _memoryBridge != null && _memoryBridge.TryConsumeHostLine(out string? line) && line != null)
            {
                if (!BridgeLineCodec.IsHello(line))
                {
                    SafeProcessResponseLine(line);
                }
            }

            // the mod re-arms its discovery beacon after ~10s without controller
            // evidence (it cannot tell a command-idle controller from a dead one). Refute it
            // with a PING so the mod's queued outbound lines resume; a genuinely dead
            // controller obviously never does this, leaving the beacon for re-attach.
            // v11 sessions route beacon PING through ctl slots — compat0 payloads
            // stomp bytes 32-63 of the control header. Pre-v11 still uses compat0.
            if (_memoryBridge != null && _writeLock.CurrentCount > 0 && _memoryBridge.IsFindMarkerVisible()
                && (DateTime.UtcNow - lastBeaconPingUtc).TotalSeconds >= 5
                && Interlocked.CompareExchange(ref _beaconPingInFlight, 1, 0) == 0)
            {
                lastBeaconPingUtc = DateTime.UtcNow;
                bool v11 = _memoryBridge.LayoutV11;
                Diagnostic?.Invoke(v11
                    ? "mod discovery beacon detected; sending liveness PING (ctl slot)"
                    : "mod discovery beacon detected; sending liveness PING (compat0)");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using CancellationTokenSource pingTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        pingTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                        BridgeCommand.Ping ping = new(Guid.NewGuid());
                        if (v11)
                        {
                            await SendAsync(ping, pingTimeout.Token);
                        }
                        else
                        {
                            await SendCompatAsync(ping, pingTimeout.Token);
                        }
                    }
                    catch (Exception)
                    {
                        // Liveness ping is best-effort; a failed ping just leaves the beacon up.
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _beaconPingInFlight, 0);
                    }
                }, CancellationToken.None);
            }

            // Host death must fault waiting commands promptly — they no longer poll the
            // process themselves, and e.g. a TS3 crash mid-LoadSave would otherwise sit
            // silently until the full command timeout.
            try
            {
                _memoryBridge?.ThrowIfHostProcessExited();
            }
            catch (InvalidOperationException ex)
            {
                foreach (Guid correlationId in _pending.Keys)
                {
                    if (_pending.TryRemove(correlationId, out TaskCompletionSource<BridgeResponse>? pending))
                    {
                        pending.TrySetException(new InvalidOperationException(ex.Message, ex));
                    }
                }
            }

            try
            {
                await Task.Delay(10, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Process an incoming line from the host. Routes EVENT lines to the event channel,
    /// reassembles SNAPSHOT_CHUNK frames, and resolves pending request/response correlations.
    /// </summary>
    private void SafeProcessResponseLine(string line)
    {
        try
        {
            ProcessResponseLine(line);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"response line fault (ignored): {ex.Message} line={(line.Length > 96 ? line[..96] + "…" : line)}");
        }
    }

    internal void ProcessResponseLine(string line)
    {
        if (BridgeLineCodec.IsRoadPlaceRequest(line))
        {
            if (BridgeLineCodec.TryParseRoadPlaceRequest(line, out RoadPlaceRequest placement))
                RoadPlaceRequested?.Invoke(placement);
            return;
        }
        // 0) A walk's plan request goes to the controller hop, not to clients.
        if (RoutePlanRequest.IsRequest(line))
        {
            if (RoutePlanRequest.TryParse(line, out RoutePlanRequest planRequest))
            {
                RoutePlanRequested?.Invoke(planRequest);
            }

            return;
        }

        // 1) EVENT lines push asynchronously, never correlate to a pending request.
        if (BridgeLineCodec.IsEvent(line))
        {
            ServerEvent? evt = BridgeLineCodec.TryDecodeEvent(line);
            if (evt != null)
            {
                AuthoritativeEvent?.Invoke(evt);
            }
            return;
        }

        BridgeResponse? response = ProcessLineForResponse(line);
        if (response == null)
        {
            return;
        }

        if (_pending.TryRemove(response.CorrelationId, out TaskCompletionSource<BridgeResponse>? pending))
        {
            pending.TrySetResult(response);
        }
        else
        {
            // a response with no waiter is usually the late answer to a command that
            // already timed out. Dropping it here is safe; logging it is how --bridge-trace
            // distinguishes "response lost" from "response late".
            Diagnostic?.Invoke($"unmatched response dropped corr={response.CorrelationId:N} accepted={response.IsAccepted}");
        }
    }

    /// <summary>
    /// Try to materialize a BridgeResponse from a single incoming line.
    /// Handles chunked snapshots: returns null until the final chunk completes the assembly.
    /// </summary>
    private BridgeResponse? ProcessLineForResponse(string line)
    {
        if (BridgeLineCodec.IsBulkRef(line))
        {
            return ProcessBulkRefLine(line);
        }

        if (BridgeLineCodec.IsSnapshotChunk(line))
        {
            var decoded = BridgeLineCodec.TryDecodeChunk(line);
            if (decoded == null)
            {
                return null;
            }

            var (corrId, idx, count, payload) = decoded.Value;
            if (count <= 0 || count > MaxSnapshotChunks)
            {
                return null;
            }

            ChunkBuffer buffer = _chunks.GetOrAdd(corrId, _ => new ChunkBuffer(count));
            if (buffer.Total != count || !buffer.Add(idx, payload, out bool complete))
            {
                _chunks.TryRemove(corrId, out _);
                return null;
            }

            // instrumentation: chunk cadence. First chunk, every 50th, any >1s inter-chunk
            // stall, and completion. Decides between the chunk-loss vs tick-starvation hypotheses.
            double gapMs = buffer.MarkArrival(DateTime.UtcNow);
            if (Diagnostic != null && (buffer.Received == 1 || buffer.Received % 50 == 0 || gapMs > 1000))
            {
                Diagnostic.Invoke($"chunk corr={corrId:N} idx={idx} recv={buffer.Received}/{count} gap={gapMs:F0}ms elapsed={buffer.ElapsedSeconds:F1}s");
            }

            if (!complete)
            {
                return null; // not yet complete
            }

            _chunks.TryRemove(corrId, out _);
            Diagnostic?.Invoke($"chunks complete corr={corrId:N} total={count} duration={buffer.ElapsedSeconds:F1}s avg={(buffer.ElapsedSeconds * 1000 / Math.Max(1, count)):F0}ms/chunk");
            string assembled = buffer.Assemble();
            // Assembled chunk is a complete SNAPSHOT|... line — decode it directly.
            return BridgeLineCodec.TryDecodeResponse(assembled);
        }

        return BridgeLineCodec.TryDecodeResponse(line);
    }

    private BridgeResponse? ProcessBulkRefLine(string line)
    {
        BridgeLineCodec.BulkRefParseResult? parsed = BridgeLineCodec.TryParseBulkRef(line);
        if (parsed == null || _memoryBridge == null)
        {
            return null;
        }

        BridgeLineCodec.BulkRefParseResult bulk = parsed.Value;
        if (!_memoryBridge.TryReadHostBulkPayload(bulk.SlotIndex, bulk.Crc32, out byte[]? payload) || payload == null)
        {
            Diagnostic?.Invoke($"bulk read failed corr={bulk.CorrelationId:N} slot={bulk.SlotIndex}");
            return null;
        }

        if (bulk.Kind == "snapshot-part" && bulk.PartIndex.HasValue && bulk.PartCount.HasValue)
        {
            BulkPartBuffer buffer = _bulkParts.GetOrAdd(bulk.CorrelationId, _ => new BulkPartBuffer(bulk.PartCount.Value, bulk.TotalLength));
            if (!buffer.Add(bulk.PartIndex.Value, payload, out bool complete))
            {
                _bulkParts.TryRemove(bulk.CorrelationId, out _);
                return null;
            }

            if (!complete)
            {
                return null;
            }

            _bulkParts.TryRemove(bulk.CorrelationId, out _);
            payload = buffer.Assemble();
            Diagnostic?.Invoke($"bulk parts complete corr={bulk.CorrelationId:N} parts={bulk.PartCount.Value} bytes={payload.Length}");
        }
        else
        {
            Diagnostic?.Invoke($"bulk complete corr={bulk.CorrelationId:N} slot={bulk.SlotIndex} bytes={payload.Length}");
        }

        // Phase 2b: detect SNAPSHOT2 binary by magic header (SPS2 = 0x32505353).
        // If found, decode binary directly — never build a UTF-8 string intermediary.
        if (Snapshot2BinaryReader.IsSnapshot2(payload))
        {
            SimulationSnapshot? snapshot = Snapshot2BinaryReader.TryDecode(payload);
            if (snapshot == null)
            {
                Diagnostic?.Invoke($"snapshot2 decode failed corr={bulk.CorrelationId:N} bytes={payload.Length}");
                return null;
            }

            Diagnostic?.Invoke($"snapshot2 decoded corr={bulk.CorrelationId:N} sims={snapshot.Sims.Count} objects={snapshot.Objects.Count}");
            return BridgeResponse.Accepted(bulk.CorrelationId, snapshot);
        }

        // UTF-8 SNAPSHOT| text path (Phase 2a fallback / v10 compat).
        string assembled = Encoding.UTF8.GetString(payload);
        return BridgeLineCodec.TryDecodeResponse(assembled);
    }

    private static string ParseMemoryEndpoint(string endpoint)
    {
        string processName = endpoint.Substring("memory:".Length);
        return string.IsNullOrWhiteSpace(processName) ? "TS3W" : processName;
    }

    /// <summary>
    /// Reassembly buffer for SNAPSHOT_CHUNK frames keyed by correlation id.
    /// Thread-safety: each instance is reachable only via _chunks (ConcurrentDictionary). Add can
    /// run on the reader loop OR the SendAsync displaced-line path, but never both for
    /// the same correlation id: _writeLock allows one in-flight command, so a chunk stream's
    /// frames are consumed by exactly one party at a time. If commands are ever pipelined, this
    /// class needs locking.
    /// </summary>
    private sealed class ChunkBuffer
    {
        private readonly string[] _parts;
        private int _received;
        private DateTime _firstArrivalUtc;
        private DateTime _lastArrivalUtc;

        public int Total => _parts.Length;

        public int Received => _received;

        public double ElapsedSeconds => _firstArrivalUtc == default ? 0 : (_lastArrivalUtc - _firstArrivalUtc).TotalSeconds;

        public ChunkBuffer(int total)
        {
            _parts = new string[Math.Max(1, total)];
        }

        public bool Add(int index, string payload, out bool complete)
        {
            complete = false;
            if (index < 0 || index >= _parts.Length) return false;
            if (_parts[index] != null) return false;

            _parts[index] = payload;
            _received++;
            complete = _received == _parts.Length;
            return true;
        }

        /// <summary> instrumentation: record an arrival, returning ms since the previous one (0 for the first).</summary>
        public double MarkArrival(DateTime utcNow)
        {
            double gap = _lastArrivalUtc == default ? 0 : (utcNow - _lastArrivalUtc).TotalMilliseconds;
            if (_firstArrivalUtc == default)
            {
                _firstArrivalUtc = utcNow;
            }
            _lastArrivalUtc = utcNow;
            return gap;
        }

        public string Assemble() => string.Concat(_parts);

        /// <summary> instrumentation: first N missing chunk indices (loss fingerprint).</summary>
        public string MissingIndices(int max)
        {
            List<int> missing = new();
            for (int i = 0; i < _parts.Length && missing.Count < max; i++)
            {
                if (_parts[i] == null) missing.Add(i);
            }
            return missing.Count == 0 ? "(none)" : string.Join(",", missing);
        }
    }

    private sealed class BulkPartBuffer
    {
        private readonly byte[][] _parts;
        private readonly int _expectedTotalLength;
        private int _received;

        public BulkPartBuffer(int partCount, int expectedTotalLength)
        {
            _parts = new byte[Math.Max(1, partCount)][];
            _expectedTotalLength = expectedTotalLength;
        }

        public bool Add(int index, byte[] payload, out bool complete)
        {
            complete = false;
            if (index < 0 || index >= _parts.Length || payload == null)
            {
                return false;
            }

            if (_parts[index] != null)
            {
                return false;
            }

            _parts[index] = payload;
            _received++;
            complete = _received == _parts.Length;
            return true;
        }

        public byte[] Assemble()
        {
            int length = 0;
            for (int i = 0; i < _parts.Length; i++)
            {
                length += _parts[i]?.Length ?? 0;
            }

            byte[] assembled = new byte[length];
            int offset = 0;
            for (int i = 0; i < _parts.Length; i++)
            {
                byte[] part = _parts[i] ?? Array.Empty<byte>();
                Array.Copy(part, 0, assembled, offset, part.Length);
                offset += part.Length;
            }

            if (assembled.Length != _expectedTotalLength)
            {
                throw new InvalidOperationException($"Bulk part assembly length mismatch: got {assembled.Length}, expected {_expectedTotalLength}.");
            }

            return assembled;
        }
    }

    /// <summary>
    /// instrumentation: describe the reassembly state for a correlation id (used to enrich
    /// command-timeout errors — "received=120/500" means tick starvation, "received=499/500
    /// missing=37" means chunk loss). Null when no chunk stream exists for the id.
    /// </summary>
    public string? DescribeChunkState(Guid correlationId)
    {
        if (!_chunks.TryGetValue(correlationId, out ChunkBuffer? buffer))
        {
            return null;
        }

        return $"chunks received={buffer.Received}/{buffer.Total} elapsed={buffer.ElapsedSeconds:F1}s missing={buffer.MissingIndices(10)}";
    }
}
