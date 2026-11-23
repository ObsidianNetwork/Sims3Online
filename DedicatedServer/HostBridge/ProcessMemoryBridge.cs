using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SimsOnline.DedicatedServer.HostBridge;

internal sealed class ProcessMemoryBridge : IDisposable
{
    private const int CompatSlotOffset = 0;
    private const int SequenceOffset = 8;
    private const int LengthOffset = 12;
    private const int PayloadOffset = BridgeSharedRegionLayout.SlotHeaderSize;

    private static readonly byte[] FindMarker = BuildFindMarker();
    private static readonly byte[] BulkFindMarker = BuildBulkFindMarker();

    // Platform seam: Windows VirtualQueryEx/ReadProcessMemory/... on Win32, /proc + process_vm_*
    // on Linux/Wine. Stateless w.r.t. _processHandle, so test fixtures can inject a self handle.
    private readonly INativeProcessMemory _native = NativeProcessMemory.Instance;

    private readonly string _processName;
    private IntPtr _processHandle;
    private IntPtr _bufferAddress;
    private Process? _process;
    private int _sequence;
    private bool _layoutV11;
    private bool _layoutV11FromHelloCaps;
    private int _ctlWriteCursor;
    // serializes frame consumption (read + consumed-stamp) and command writes.
    private readonly object _frameLock = new();

    /// <summary>
    /// --bridge-trace: per-frame protocol trace (command writes, host-line consumption,
    /// displaced frames, mid-write waits). Subscribers must be cheap and non-blocking.
    /// Payloads are TRUNCATED for console readability — use <see cref="FrameRecord"/> for
    /// lossless capture.
    /// </summary>
    public event Action<string>? Trace;

    /// <summary>
    /// --bridge-record: lossless per-frame capture ("dir seq=N &lt;full line&gt;").
    /// Truncated traces are unrecoverable — this is the input for replay/golden tooling.
    /// </summary>
    public event Action<string>? FrameRecord;

    public ProcessMemoryBridge(string processName)
    {
        _processName = string.IsNullOrWhiteSpace(processName) ? "TS3W" : processName;
        // the mod's frame dedupe is equality-based against the previous session's last
        // sequence. A fixed start of 1 meant a re-attaching controller's CONNECT was silently
        // swallowed whenever the prior session died right after its own CONNECT. Randomize.
        _sequence = Random.Shared.Next(1024, int.MaxValue / 2);
    }

    public bool IsConnected => _processHandle != IntPtr.Zero && _bufferAddress != IntPtr.Zero;

    public bool LayoutV11 => _layoutV11;

    private static bool AcceptConnectResponse(string line)
    {
        if (BridgeLineCodec.IsConnectHandshakeHello(line))
        {
            return true;
        }

        line = line.TrimStart('\uFEFF');
        if (!line.StartsWith("HELLO|", StringComparison.Ordinal))
        {
            return false;
        }

        int versionStart = "HELLO|".Length;
        int versionEnd = line.IndexOf('|', versionStart);
        return versionEnd > versionStart
            && int.TryParse(line.AsSpan(versionStart, versionEnd - versionStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out int wireVersion)
            && wireVersion >= BridgeSharedRegionLayout.HelloWireVersion;
    }

    private void RefreshLayoutMode()
    {
        if (_layoutV11FromHelloCaps)
        {
            _layoutV11 = true;
            return;
        }

        _layoutV11 = TryReadLayoutVersion() == BridgeSharedRegionLayout.LayoutVersion;
        if (_layoutV11)
        {
            _ctlWriteCursor = 0;
        }
    }

    /// <summary>
    /// lock ctl-slot routing from HELLO|6 caps when the control header is not yet readable.
    /// </summary>
    internal void ApplyConnectHello(string helloLine)
    {
        if (BridgeLineCodec.TryParseHello(helloLine, out BridgeLineCodec.HelloParseResult hello)
            && hello.WireVersion >= BridgeSharedRegionLayout.HelloWireVersion
            && BridgeLineCodec.CapsIndicateLayoutV11(hello.Caps))
        {
            _layoutV11FromHelloCaps = true;
            _layoutV11 = true;
            _ctlWriteCursor = 0;
            return;
        }

        RefreshLayoutMode();
    }

    private int TryReadLayoutVersion()
    {
        if (_bufferAddress == IntPtr.Zero)
        {
            return 0;
        }

        byte[] header = new byte[4];
        IntPtr address = _bufferAddress + BridgeSharedRegionLayout.LayoutVersionOffset;
        if (!ReadProcessMemory(_processHandle, address, header, header.Length, out IntPtr bytesReadPointer)
            || bytesReadPointer.ToInt32() < header.Length)
        {
            return 0;
        }

        int version = BitConverter.ToInt32(header, 0);
        if (version == BridgeSharedRegionLayout.LayoutVersion)
        {
            return version;
        }

        // Compat-slot payloads overlap bytes 32-63; infer v11 from the bulk sub-marker when
        // the control header was stomped but the region is otherwise intact (re-attach path).
        if (TryReadBulkMarkerAt(_bufferAddress, out bool bulkMatches) && bulkMatches)
        {
            return BridgeSharedRegionLayout.LayoutVersion;
        }

        return version;
    }

    private static int SlotByteOffset(int slotIndex) => BridgeSharedRegionLayout.SlotByteOffset(slotIndex);

    private IntPtr SlotAddress(int byteOffset) => _bufferAddress + byteOffset;

    public async Task<string> ConnectAsync(int? processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        while (!timeoutSource.IsCancellationRequested)
        {
            Process? process = processId.HasValue
                ? GetPinnedProcessOrThrow(processId.Value)
                : NativeProcessMemory.GetProcessesByWindowsName(_processName).FirstOrDefault();

            if (process != null)
            {
                EnsureProcessOpen(process);
                Trace?.Invoke($"attach scan pid={process.Id}");
                _bufferAddress = FindBridgeBuffer(timeoutSource.Token);
                if (_bufferAddress != IntPtr.Zero)
                {
                    Trace?.Invoke($"attach candidate=0x{_bufferAddress.ToInt64():X}");
                    SeedSequenceAboveRegionMax();
                    string hello = await SendRawAsync(
                        $"CONNECT|00000000000000000000000000000000|{BridgeSharedRegionLayout.ProtocolVersion}",
                        AcceptConnectResponse,
                        timeoutSource.Token);
                    Trace?.Invoke($"attach hello={hello}");
                    ApplyConnectHello(hello);
                    DrainOrphanHostFramesAfterConnect();
                    return hello;
                }

                Trace?.Invoke($"attach scan_miss pid={process.Id}");
            }

            try
            {
                await Task.Delay(250, timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting {timeout.TotalSeconds:N0} seconds for the Sims 3 process-memory bridge marker.");
            }
        }

        throw new TimeoutException($"Timed out waiting {timeout.TotalSeconds:N0} seconds for the Sims 3 process-memory bridge marker.");
    }

    private Process GetPinnedProcessOrThrow(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException ex)
        {
            throw BuildPinnedProcessExitedException(processId, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw BuildPinnedProcessExitedException(processId, ex);
        }
    }

    private InvalidOperationException BuildPinnedProcessExitedException(int processId, Exception inner)
    {
        string peerSummary = DescribeRunningPeers(processId);
        string message = $"The Sims 3 process-memory bridge was pinned to {_processName}.exe PID {processId}, but that process exited before the bridge marker appeared. Refusing to scan by process name because another {_processName}.exe may be a different server/client Sims instance.{peerSummary} If this happened during client mirroring, Sims 3 likely rejected or redirected the second TS3W.exe launch while another Sims instance was already running; close other TS3W.exe processes or run the client mirror with --client-mirror-sandboxie-box <box> so the second Sims instance is isolated.";
        return new InvalidOperationException(message, inner);
    }

    private string DescribeRunningPeers(int vanishedProcessId)
    {
        try
        {
            string[] peerIds = NativeProcessMemory.GetProcessesByWindowsName(_processName)
                .Where(process => process.Id != vanishedProcessId)
                .Select(process => process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            return peerIds.Length == 0
                ? $" No other {_processName}.exe processes are currently visible."
                : $" Other visible {_processName}.exe PID(s): {string.Join(", ", peerIds)}.";
        }
        catch
        {
            return $" Could not enumerate other {_processName}.exe processes.";
        }
    }

    /// <summary>
    /// single-writer command path. Writes the command frame and returns immediately;
    /// the response is consumed by the HostBridgeServer reader loop and correlated by id
    /// (the old in-place response polling raced the reader loop for frames and lost).
    /// If an unread host frame still occupies the slot it is decoded and handed back via
    /// <paramref name="displacedHostLine"/> so the caller can route it instead of destroying it.
    /// </summary>
    /// <summary>
    /// write a command to compat slot 0 regardless of v11 ctl routing. Used for
    /// beacon-refutation PING so the mod always sees liveness traffic (compat-only read path
    /// after idle downgrade, and multi-slot ReadServerLine prefers compat when armed).
    /// </summary>
    public void WriteCompatCommand(string line, out string? displacedHostLine)
    {
        displacedHostLine = null;
        if (!IsConnected)
        {
            throw new InvalidOperationException("The Sims 3 process-memory bridge is not connected.");
        }

        RefreshLayoutMode();
        int commandByteOffset = CompatSlotOffset;
        WriteCommandAtOffset(line, commandByteOffset, ctlSlotIndex: 0, advanceCtlCursor: false, out displacedHostLine);
    }

    public void WriteCommand(string line, out string? displacedHostLine)
    {
        displacedHostLine = null;
        if (!IsConnected)
        {
            throw new InvalidOperationException("The Sims 3 process-memory bridge is not connected.");
        }

        RefreshLayoutMode();
        int commandByteOffset = ResolveCommandByteOffset(out int ctlSlotIndex);
        WriteCommandAtOffset(line, commandByteOffset, ctlSlotIndex, advanceCtlCursor: _layoutV11, out displacedHostLine);
    }

    private void WriteCommandAtOffset(string line, int commandByteOffset, int ctlSlotIndex, bool advanceCtlCursor, out string? displacedHostLine)
    {
        displacedHostLine = null;
        for (int attempt = 0; ; attempt++)
        {
            WaitWhileHostMidWrite(commandByteOffset);

            lock (_frameLock)
            {
                if (attempt < 50 && TryReadSenderId(commandByteOffset, out ulong senderId) && senderId == BridgeSharedRegionLayout.HostWritingSenderId)
                {
                }
                else
                {
                    displacedHostLine = TryPeekDisplacedHostLine();
                    int sequence = ++_sequence;
                    WriteFrame(commandByteOffset, sequence, BridgeSharedRegionLayout.ServerSenderId, line);
                    if (advanceCtlCursor)
                    {
                        _ctlWriteCursor = (ctlSlotIndex + 1) % BridgeSharedRegionLayout.CtlSlotCount;
                    }

                    Trace?.Invoke($"ctl->host seq={sequence} slot={DescribeSlot(commandByteOffset)} {TraceText(line)}");
                    FrameRecord?.Invoke($"ctl->host seq={sequence} slot={DescribeSlot(commandByteOffset)} {line}");
                    break;
                }
            }

            Thread.Sleep(1);
        }

        if (displacedHostLine != null)
        {
            Trace?.Invoke($"displaced unread host line before command write: {TraceText(displacedHostLine)}");
        }
    }

    private int ResolveCommandByteOffset(out int ctlSlotIndex)
    {
        if (!_layoutV11)
        {
            ctlSlotIndex = 0;
            return CompatSlotOffset;
        }

        for (int attempt = 0; attempt < BridgeSharedRegionLayout.CtlSlotCount; attempt++)
        {
            ctlSlotIndex = (_ctlWriteCursor + attempt) % BridgeSharedRegionLayout.CtlSlotCount;
            int slot = BridgeSharedRegionLayout.CtlSlotBase + ctlSlotIndex;
            int byteOffset = SlotByteOffset(slot);
            if (CanWriteCtlSlot(byteOffset))
            {
                return byteOffset;
            }
        }

        ctlSlotIndex = _ctlWriteCursor;
        return SlotByteOffset(BridgeSharedRegionLayout.CtlSlotBase + ctlSlotIndex);
    }

    private bool CanWriteCtlSlot(int byteOffset)
    {
        if (!TryReadSenderId(byteOffset, out ulong senderId))
        {
            return false;
        }

        return senderId == 0
            || senderId == BridgeSharedRegionLayout.ConsumedSenderId
            || senderId == BridgeSharedRegionLayout.ServerSenderId;
    }

    private string? TryPeekDisplacedHostLine()
    {
        while (true)
        {
            if (!TryPeekDisplacedHostFrame(out int byteOffset, out string? line))
            {
                return null;
            }

            if (BridgeLineCodec.IsPlausibleHostFrameLine(line))
            {
                return line;
            }

            MarkFrameConsumed(byteOffset);
            Trace?.Invoke($"displaced corrupt host line dropped slot={DescribeSlot(byteOffset)}: {TraceText(line ?? string.Empty)}");
        }
    }

    private bool TryPeekDisplacedHostFrame(out int byteOffset, out string? line)
    {
        line = null;
        byteOffset = 0;
        if (!_layoutV11)
        {
            byteOffset = CompatSlotOffset;
            return TryReadHostFrameAt(CompatSlotOffset, out _, out line) && line != null;
        }

        for (int i = 0; i < BridgeSharedRegionLayout.HostSlotCount; i++)
        {
            byteOffset = SlotByteOffset(BridgeSharedRegionLayout.HostSlotBase + i);
            if (TryReadHostFrameAt(byteOffset, out _, out line) && line != null)
            {
                return true;
            }
        }

        line = null;
        return false;
    }

    /// <summary>
    /// Atomically read-and-consume the current host frame, if any. The consumed-stamp happens
    /// inside the same lock as the read: the old two-step path guarded by a content dedupe
    /// silently dropped identical consecutive host lines AND skipped their consumed-stamp,
    /// wedging the mod's outbound queue.
    /// </summary>
    public bool TryConsumeHostLine(out string? line)
    {
        line = null;
        if (!IsConnected)
        {
            return false;
        }

        int frameSequence = 0;
        lock (_frameLock)
        {
            RefreshLayoutMode();
            if (_sequence <= 0)
            {
                return false;
            }

            if (!_layoutV11)
            {
                if (!TryReadHostFrameAt(CompatSlotOffset, out frameSequence, out string? response) || response == null)
                {
                    return false;
                }

                if (!BridgeLineCodec.IsPlausibleHostFrameLine(response))
                {
                    MarkFrameConsumed(CompatSlotOffset);
                    Trace?.Invoke($"corrupt host frame consumed slot=compat0: {TraceText(response)}");
                    return false;
                }

                MarkFrameConsumed(CompatSlotOffset);
                line = response;
            }
            else
            {
                for (int i = 0; i < BridgeSharedRegionLayout.HostSlotCount; i++)
                {
                    int byteOffset = SlotByteOffset(BridgeSharedRegionLayout.HostSlotBase + i);
                    if (!TryReadHostFrameAt(byteOffset, out int slotSequence, out string? response) || response == null)
                    {
                        continue;
                    }

                    if (!BridgeLineCodec.IsPlausibleHostFrameLine(response))
                    {
                        MarkFrameConsumed(byteOffset);
                        Trace?.Invoke($"corrupt host frame consumed slot={DescribeSlot(byteOffset)}: {TraceText(response)}");
                        continue;
                    }

                    MarkFrameConsumed(byteOffset);
                    line = response;
                    frameSequence = slotSequence;
                    break;
                }

                if (line == null)
                {
                    return false;
                }
            }
        }

        Trace?.Invoke($"host->ctl seq={frameSequence} {TraceText(line)}");
        FrameRecord?.Invoke($"host->ctl seq={frameSequence} {line}");
        return true;
    }

    private void WaitWhileHostMidWrite(int byteOffset)
    {
        for (int spin = 0; spin < 250; spin++)
        {
            if (!TryReadSenderId(byteOffset, out ulong senderId) || senderId != BridgeSharedRegionLayout.HostWritingSenderId)
            {
                if (spin > 0)
                {
                    Trace?.Invoke($"mid-write sentinel cleared after ~{spin}ms");
                }
                return;
            }

            Thread.Sleep(1);
        }

        Trace?.Invoke("mid-write sentinel did not clear within 250ms; proceeding");
    }

    private bool TryReadSenderId(int byteOffset, out ulong senderId)
    {
        senderId = 0;
        byte[] header = new byte[8];
        if (!ReadProcessMemory(_processHandle, SlotAddress(byteOffset), header, header.Length, out IntPtr bytesReadPointer) ||
            bytesReadPointer.ToInt32() < header.Length)
        {
            return false;
        }

        senderId = BitConverter.ToUInt64(header, 0);
        return true;
    }

    /// <summary>
    /// true when the slot currently holds the mod's discovery beacon (re-armed
    /// find-marker). The mod arms it after ~10s without controller evidence; the reader
    /// answers with PING to refute it. Compares the first 8 marker bytes.
    /// </summary>
    public bool IsFindMarkerVisible()
    {
        if (!IsConnected)
        {
            return false;
        }

        byte[] probe = new byte[FindMarker.Length];
        if (!ReadProcessMemory(_processHandle, SlotAddress(CompatSlotOffset), probe, probe.Length, out IntPtr bytesReadPointer)
            || bytesReadPointer.ToInt32() < FindMarker.Length)
        {
            return false;
        }

        for (int i = 0; i < FindMarker.Length; i++)
        {
            if (probe[i] != FindMarker[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeSlot(int byteOffset)
    {
        if (byteOffset == CompatSlotOffset)
        {
            return "compat0";
        }

        return ((byteOffset - BridgeSharedRegionLayout.ControlHeaderSize) / BridgeSharedRegionLayout.SlotSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string TraceText(string line)
    {
        return line.Length <= 96 ? line : line[..96] + $"…({line.Length} chars)";
    }

    public void Dispose()
    {
        // GetCurrentProcess() returns the pseudo-handle (-1). Closing it is undefined and can
        // break subsequent ReadProcessMemory calls in the same process (DedicatedServer.Tests
        // injects the current-process handle for pinned-buffer fixtures).
        if (_processHandle != IntPtr.Zero && _processHandle != (IntPtr)(-1))
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Connect-phase request/response (CONNECT → HELLO). Only used before the HostBridgeServer
    /// reader loop starts, so inline polling cannot race another consumer ( moved all
    /// post-connect consumption into the single reader loop).
    /// </summary>
    private async Task<string> SendRawAsync(string line, Func<string, bool> acceptResponse, CancellationToken cancellationToken)
    {
        // CONNECT|19 uses compat slot 0. Clear orphaned ctl commands from the prior controller
        // session so the mod pump reaches compat promptly on re-attach.
        int byteOffset = CompatSlotOffset;
        lock (_frameLock)
        {
            ClearStaleServerCommandFrames();
            ClearStaleHostFrames();
            int sequence = ++_sequence;
            WriteFrame(byteOffset, sequence, BridgeSharedRegionLayout.ServerSenderId, line);
            Trace?.Invoke($"ctl->host seq={sequence} {TraceText(line)}");
            FrameRecord?.Invoke($"ctl->host seq={sequence} {line}");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfProcessExited();

            if (TryConsumeConnectResponse(out string? response) && response != null)
            {
                if (acceptResponse(response))
                {
                    RefreshLayoutMode();
                    return response;
                }
                continue;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private void ClearStaleServerCommandFrames()
    {
        for (int i = 0; i < BridgeSharedRegionLayout.CtlSlotCount; i++)
        {
            int slot = BridgeSharedRegionLayout.CtlSlotBase + i;
            int byteOffset = SlotByteOffset(slot);
            if (TryReadSenderId(byteOffset, out ulong senderId) && senderId == BridgeSharedRegionLayout.ServerSenderId)
            {
                MarkFrameConsumed(byteOffset);
            }
        }
    }

    /// <summary>
    /// The mod's inbound dedupe cursor (sequence &lt;= sLastServerSequence drops the frame)
    /// survives controller restarts, while each controller randomizes its starting sequence.
    /// A draw below the dead session's cursor made every frame — including CONNECT — silently
    /// invisible to the mod (~50/50 per re-attach; proven live 2026-07-03, CONNECT seq 603M
    /// dropped against cursor 846M). Seed above the maximum sequence observable in the region:
    /// ctl slots hold the prior controller's writes and host slots mirror the sequence of the
    /// last command the mod answered, so max(all slots) covers the mod's cursor in any real
    /// history. Runs before CONNECT is written; needs no mod-side cooperation.
    /// </summary>
    internal void SeedSequenceAboveRegionMax()
    {
        int maxObserved = 0;
        if (TryReadSlotSequenceForSeeding(CompatSlotOffset, out int compatSequence) && compatSequence > maxObserved)
        {
            maxObserved = compatSequence;
        }

        for (int slot = 0; slot < BridgeSharedRegionLayout.ControlSlotCount; slot++)
        {
            if (TryReadSlotSequenceForSeeding(SlotByteOffset(slot), out int sequence) && sequence > maxObserved)
            {
                maxObserved = sequence;
            }
        }

        if (maxObserved <= 0 || _sequence > maxObserved)
        {
            return;
        }

        long seeded = (long)maxObserved + 1024;
        if (seeded >= int.MaxValue - 1024)
        {
            // Pathological cursor near overflow: leave the randomized draw in place rather than
            // wrap negative. The mod-side CONNECT cursor snap covers this session.
            Trace?.Invoke($"attach seq seed skipped: region max {maxObserved} is too close to overflow");
            return;
        }

        Trace?.Invoke($"attach seq seeded above region max: {_sequence} -> {seeded} (max observed {maxObserved})");
        _sequence = (int)seeded;
    }

    private bool TryReadSlotSequenceForSeeding(int byteOffset, out int sequence)
    {
        sequence = 0;
        byte[] header = new byte[12];
        if (!ReadProcessMemory(_processHandle, SlotAddress(byteOffset), header, header.Length, out IntPtr bytesReadPointer)
            || bytesReadPointer.ToInt32() < header.Length)
        {
            return false;
        }

        ulong senderId = BitConverter.ToUInt64(header, 0);
        if (senderId != BridgeSharedRegionLayout.HostSenderId
            && senderId != BridgeSharedRegionLayout.ServerSenderId
            && senderId != BridgeSharedRegionLayout.ConsumedSenderId
            && senderId != BridgeSharedRegionLayout.HostWritingSenderId)
        {
            // Armed find-marker or unused slot — bytes 8-11 are not a sequence.
            return false;
        }

        sequence = BitConverter.ToInt32(header, 8);
        return true;
    }

    /// <summary>
    /// orphan HostSenderId frames in slots 0-7 from a prior controller session block
    /// CanWriteHostLine on the mod; stamp consumed on re-attach CONNECT so snapshot replies can publish.
    /// also consume mid-write host frames left from a crashed controller.
    /// </summary>
    private void ClearStaleHostFrames()
    {
        for (int i = 0; i < BridgeSharedRegionLayout.HostSlotCount; i++)
        {
            int slot = BridgeSharedRegionLayout.HostSlotBase + i;
            int byteOffset = SlotByteOffset(slot);
            if (TryReadSenderId(byteOffset, out ulong senderId)
                && (senderId == BridgeSharedRegionLayout.HostSenderId
                    || senderId == BridgeSharedRegionLayout.HostWritingSenderId))
            {
                MarkFrameConsumed(byteOffset);
            }
        }
    }

    /// <summary>
    /// after CONNECT succeeds, drain any same-sequence host replies still sitting in the
    /// ring (stale OK/HELLO flushed with the new controller sequence on re-attach).
    /// </summary>
    private void DrainOrphanHostFramesAfterConnect()
    {
        lock (_frameLock)
        {
            int sequence = _sequence;
            for (int pass = 0; pass < BridgeSharedRegionLayout.HostSlotCount + 1; pass++)
            {
                bool consumedAny = false;
                for (int i = 0; i < BridgeSharedRegionLayout.HostSlotCount; i++)
                {
                    int byteOffset = SlotByteOffset(BridgeSharedRegionLayout.HostSlotBase + i);
                    if (TryReadFrame(byteOffset, sequence, out string? line) && line != null)
                    {
                        MarkFrameConsumed(byteOffset);
                        Trace?.Invoke($"reattach orphan host frame consumed slot={DescribeSlot(byteOffset)}: {TraceText(line)}");
                        consumedAny = true;
                    }
                }

                if (!consumedAny)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// CONNECT-phase HELLO may land on compat (pre-upgrade) or host slots (live v11 session).
    /// </summary>
    private bool TryConsumeConnectResponse(out string? line)
    {
        line = null;
        if (!IsConnected)
        {
            return false;
        }

        int sequence;
        lock (_frameLock)
        {
            sequence = _sequence;
            if (sequence <= 0)
            {
                return false;
            }

            if (TryReadFrame(CompatSlotOffset, sequence, out string? compatResponse) && compatResponse != null)
            {
                MarkFrameConsumed(CompatSlotOffset);
                line = compatResponse;
            }
            else
            {
                for (int i = 0; i < BridgeSharedRegionLayout.HostSlotCount; i++)
                {
                    int byteOffset = SlotByteOffset(BridgeSharedRegionLayout.HostSlotBase + i);
                    if (!TryReadFrame(byteOffset, sequence, out string? hostResponse) || hostResponse == null)
                    {
                        continue;
                    }

                    MarkFrameConsumed(byteOffset);
                    line = hostResponse;
                    break;
                }
            }
        }

        if (line == null)
        {
            return false;
        }

        Trace?.Invoke($"host->ctl seq={sequence} {TraceText(line)}");
        FrameRecord?.Invoke($"host->ctl seq={sequence} {line}");
        return true;
    }

    private void EnsureProcessOpen(Process process)
    {
        if (_processHandle != IntPtr.Zero)
        {
            return;
        }

        _process = process;
        // desiredAccess is honored by the Windows accessor internally; the shim ignores the arg.
        _processHandle = OpenProcess(0, false, process.Id);

        if (_processHandle == IntPtr.Zero)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open process {process.Id} for memory bridge access.");
            }
            else
            {
                throw new InvalidOperationException($"Could not open process {process.Id} for memory bridge access (process not available for vm_read).");
            }
        }
    }

    /// <summary>
    /// with responses awaited via correlation instead of inline polling, the reader
    /// loop is the only place that can notice host death promptly — without this check a TS3
    /// crash during a long LoadSave would silently wait out the full command timeout.
    /// </summary>
    public void ThrowIfHostProcessExited()
    {
        ThrowIfProcessExited();
    }

    private void ThrowIfProcessExited()
    {
        try
        {
            if (_process?.HasExited == true)
            {
                throw new InvalidOperationException($"The Sims 3 host process exited before the process-memory bridge response was received. Exit code: {_process.ExitCode}.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // If process inspection itself fails, keep polling the memory bridge and let
            // the command timeout provide the final diagnostic.
        }
    }

    private IntPtr FindBridgeBuffer(CancellationToken cancellationToken)
    {
        IntPtr address = IntPtr.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_native.TryQueryRegion(_processHandle, address.ToInt64(), out NativeMemoryRegion region) || !region.Valid)
            {
                return IntPtr.Zero;
            }

            long baseAddress = region.BaseAddress;
            long regionSize = region.RegionSize;
            if (regionSize >= BridgeSharedRegionLayout.SlotSize && region.ReadableWritable)
            {
                IntPtr found = SearchRegion(baseAddress, regionSize);
                if (found != IntPtr.Zero)
                {
                    return found;
                }
            }

            long nextAddress = baseAddress + Math.Max(regionSize, 0x1000);
            if (nextAddress <= address.ToInt64() || nextAddress > 0x7FFFFFFF)
            {
                return IntPtr.Zero;
            }

            address = new IntPtr(nextAddress);
        }

        return IntPtr.Zero;
    }

    private IntPtr SearchRegion(long baseAddress, long regionSize)
    {
        const int chunkSize = 1024 * 1024;
        byte[] carry = new byte[FindMarker.Length - 1];
        int carryLength = 0;

        for (long offset = 0; offset < regionSize;)
        {
            int bytesToRead = (int)Math.Min(chunkSize, regionSize - offset);
            byte[] readBuffer = new byte[bytesToRead];

            if (!ReadProcessMemory(_processHandle, new IntPtr(baseAddress + offset), readBuffer, bytesToRead, out IntPtr bytesReadPointer))
            {
                offset += bytesToRead;
                carryLength = 0;
                continue;
            }

            int bytesRead = (int)Math.Min(bytesReadPointer.ToInt64(), int.MaxValue);
            if (bytesRead <= 0)
            {
                offset += bytesToRead;
                carryLength = 0;
                continue;
            }

            byte[] searchBuffer = new byte[carryLength + bytesRead];
            Array.Copy(carry, 0, searchBuffer, 0, carryLength);
            Array.Copy(readBuffer, 0, searchBuffer, carryLength, bytesRead);

            long searchBaseAddress = baseAddress + offset - carryLength;
            int searchStart = 0;
            while (true)
            {
                int index = IndexOf(searchBuffer, searchBuffer.Length, FindMarker, searchStart);
                if (index < 0)
                {
                    break;
                }

                IntPtr candidate = new(searchBaseAddress + index);
                if (IsCandidateBuffer(candidate))
                {
                    return candidate;
                }

                searchStart = index + 1;
            }

            carryLength = Math.Min(carry.Length, searchBuffer.Length);
            Array.Copy(searchBuffer, searchBuffer.Length - carryLength, carry, 0, carryLength);
            offset += bytesRead;
        }

        return SearchBulkMarkerInRegion(baseAddress, regionSize);
    }

    private IntPtr SearchBulkMarkerInRegion(long baseAddress, long regionSize)
    {
        if (regionSize < BulkFindMarker.Length)
        {
            return IntPtr.Zero;
        }

        // Narrow scan windows (unit fixtures + bulk-header-only re-probes) anchor at the bulk
        // sub-header; recover the control-region base directly when it validates.
        if (regionSize <= BridgeSharedRegionLayout.BulkHeaderSize + BulkFindMarker.Length)
        {
            long anchoredCandidate = baseAddress - BridgeSharedRegionLayout.BulkRegionBaseOffset();
            if (anchoredCandidate > 0)
            {
                IntPtr direct = new(anchoredCandidate);
                if (ValidateBulkSubRegion(direct))
                {
                    return direct;
                }
            }
        }

        const int chunkSize = 1024 * 1024;
        byte[] carry = new byte[BulkFindMarker.Length - 1];
        int carryLength = 0;

        for (long offset = 0; offset < regionSize;)
        {
            int bytesToRead = (int)Math.Min(chunkSize, regionSize - offset);
            byte[] readBuffer = new byte[bytesToRead];

            if (!ReadProcessMemory(_processHandle, new IntPtr(baseAddress + offset), readBuffer, bytesToRead, out IntPtr bytesReadPointer))
            {
                offset += bytesToRead;
                carryLength = 0;
                continue;
            }

            int bytesRead = (int)Math.Min(bytesReadPointer.ToInt64(), int.MaxValue);
            if (bytesRead <= 0)
            {
                offset += bytesToRead;
                carryLength = 0;
                continue;
            }

            byte[] searchBuffer = new byte[carryLength + bytesRead];
            Array.Copy(carry, 0, searchBuffer, 0, carryLength);
            Array.Copy(readBuffer, 0, searchBuffer, carryLength, bytesRead);

            long searchBaseAddress = baseAddress + offset - carryLength;
            int searchStart = 0;
            while (true)
            {
                int index = IndexOf(searchBuffer, searchBuffer.Length, BulkFindMarker, searchStart);
                if (index < 0)
                {
                    break;
                }

                long candidateOffset = searchBaseAddress + index - BridgeSharedRegionLayout.BulkRegionBaseOffset();
                if (candidateOffset > 0)
                {
                    IntPtr candidate = new(candidateOffset);
                    if (ValidateBulkSubRegion(candidate))
                    {
                        Trace?.Invoke($"bulk marker candidate=0x{candidateOffset:X}");
                        return candidate;
                    }
                }

                searchStart = index + 1;
            }

            carryLength = Math.Min(carry.Length, searchBuffer.Length);
            Array.Copy(searchBuffer, searchBuffer.Length - carryLength, carry, 0, carryLength);
            offset += bytesRead;
        }

        return IntPtr.Zero;
    }

    private void WriteFrame(int byteOffset, int sequence, ulong senderId, string line)
    {
        byte[] payload = Encoding.UTF8.GetBytes(line);
        if (payload.Length > BridgeSharedRegionLayout.SlotPayloadMax)
        {
            throw new InvalidOperationException($"Bridge frame is {payload.Length} bytes; maximum is {BridgeSharedRegionLayout.SlotPayloadMax}.");
        }

        int writeLength = BridgeSharedRegionLayout.SlotSize;
        if (byteOffset == CompatSlotOffset)
        {
            // offset 0 overlaps the v11 control header at bytes 32-63. Never blast a
            // full 4096-byte zero tail — that wiped the header and slot[0] on every command.
            writeLength = PayloadOffset + payload.Length;
        }

        byte[] frame = new byte[writeLength];
        Array.Copy(BitConverter.GetBytes(sequence), 0, frame, SequenceOffset, 4);
        Array.Copy(BitConverter.GetBytes(payload.Length), 0, frame, LengthOffset, 4);
        Array.Copy(payload, 0, frame, PayloadOffset, payload.Length);
        Array.Copy(BitConverter.GetBytes(senderId), 0, frame, 0, 8);

        if (!WriteProcessMemory(_processHandle, SlotAddress(byteOffset), frame, frame.Length, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write the Sims 3 process-memory bridge frame.");
        }

        if (byteOffset == CompatSlotOffset && _layoutV11)
        {
            RepairV11ControlHeaderAfterCompatWrite();
        }
    }

    /// <summary>
    /// compat payloads longer than 16 bytes overwrite bytes 32-63 (v11 control header).
    /// Rewrite the header immediately after every compat write on v11 sessions.
    /// </summary>
    private void RepairV11ControlHeaderAfterCompatWrite()
    {
        if (_processHandle == IntPtr.Zero || _bufferAddress == IntPtr.Zero)
        {
            return;
        }

        byte[] header = new byte[32];
        Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.LayoutVersion), 0, header, 0, 4);
        Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.ControlSlotCount), 0, header, 4, 4);
        Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.SlotSize), 0, header, 8, 4);
        Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.SlotHeaderSize), 0, header, 12, 4);
        Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.HostSlotBase), 0, header, 16, 4);
        Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.HostSlotCount), 0, header, 20, 4);
        Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.CtlSlotBase), 0, header, 24, 4);
        Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.CtlSlotCount), 0, header, 28, 4);
        WriteProcessMemory(
            _processHandle,
            _bufferAddress + BridgeSharedRegionLayout.LayoutVersionOffset,
            header,
            header.Length,
            out _);
    }

    private void MarkFrameConsumed(int byteOffset)
    {
        if (_processHandle == IntPtr.Zero || _bufferAddress == IntPtr.Zero)
        {
            return;
        }

        byte[] marker = BitConverter.GetBytes(BridgeSharedRegionLayout.ConsumedSenderId);
        WriteProcessMemory(_processHandle, SlotAddress(byteOffset), marker, marker.Length, out _);
    }

    private bool TryReadHostFrameAt(int byteOffset, out int frameSequence, out string? line)
    {
        line = null;
        frameSequence = 0;
        byte[] frame = new byte[BridgeSharedRegionLayout.SlotSize];
        if (!ReadProcessMemory(_processHandle, SlotAddress(byteOffset), frame, frame.Length, out IntPtr bytesReadPointer))
        {
            return false;
        }

        if (bytesReadPointer.ToInt32() < BridgeSharedRegionLayout.SlotHeaderSize)
        {
            return false;
        }

        ulong senderId = BitConverter.ToUInt64(frame, 0);
        frameSequence = BitConverter.ToInt32(frame, SequenceOffset);
        int length = BitConverter.ToInt32(frame, LengthOffset);
        if (senderId != BridgeSharedRegionLayout.HostSenderId || length <= 0 || length > BridgeSharedRegionLayout.SlotPayloadMax)
        {
            return false;
        }

        line = Encoding.UTF8.GetString(frame, PayloadOffset, length);
        return true;
    }

    private bool TryReadFrame(int byteOffset, int expectedSequence, out string? line)
    {
        line = null;
        byte[] frame = new byte[BridgeSharedRegionLayout.SlotSize];
        if (!ReadProcessMemory(_processHandle, SlotAddress(byteOffset), frame, frame.Length, out IntPtr bytesReadPointer))
        {
            return false;
        }

        if (bytesReadPointer.ToInt32() < BridgeSharedRegionLayout.SlotHeaderSize)
        {
            return false;
        }

        ulong senderId = BitConverter.ToUInt64(frame, 0);
        int sequence = BitConverter.ToInt32(frame, SequenceOffset);
        int length = BitConverter.ToInt32(frame, LengthOffset);
        if (senderId != BridgeSharedRegionLayout.HostSenderId || sequence != expectedSequence || length <= 0 || length > BridgeSharedRegionLayout.SlotPayloadMax)
        {
            return false;
        }

        line = Encoding.UTF8.GetString(frame, PayloadOffset, length);
        return true;
    }

    private bool IsCandidateBuffer(IntPtr candidate)
    {
        const int probeLength = 256;
        byte[] probe = new byte[probeLength];
        if (!ReadProcessMemory(_processHandle, candidate, probe, probe.Length, out IntPtr bytesReadPointer) ||
            !MemoryReadComplete(bytesReadPointer, probeLength))
        {
            return false;
        }

        // v11 layout validation first — after a live session the compat slot often still
        // holds a frame header at offset 0, so the discovery-marker gate must not run before
        // layoutVersion/bulk checks (re-attach + bulk-marker scan both depend on this).
        int layoutVersion = BitConverter.ToInt32(probe, BridgeSharedRegionLayout.LayoutVersionOffset);
        if (layoutVersion == BridgeSharedRegionLayout.LayoutVersion
            && ValidateV11Candidate(candidate, probe))
        {
            return true;
        }

        if (ValidateBulkSubRegion(candidate))
        {
            // The compat slot overlaps bytes 32-63. A warm v11 session can have a valid bulk
            // sub-region while the control header is zeroed, stale, or partially stomped by the
            // compat slot, so accept the bulk marker as the recovery anchor and let CONNECT|19
            // re-stamp the control header.
            return true;
        }

        for (int i = 0; i < FindMarker.Length; i++)
        {
            if (probe[i] != FindMarker[i])
            {
                return false;
            }
        }

        for (int i = FindMarker.Length; i < probe.Length; i++)
        {
            if (probe[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReadBulkMarkerAt(IntPtr candidate, out bool matches)
    {
        matches = false;
        byte[] bulkMarker = new byte[32];
        IntPtr bulkMarkerAddress = candidate + BridgeSharedRegionLayout.BulkRegionBaseOffset();
        if (!ReadProcessMemory(_processHandle, bulkMarkerAddress, bulkMarker, bulkMarker.Length, out IntPtr bytesReadPointer)
            || bytesReadPointer.ToInt32() < bulkMarker.Length)
        {
            return false;
        }

        for (int i = 0; i < bulkMarker.Length; i++)
        {
            if (bulkMarker[i] != BridgeSharedRegionLayout.BulkMarkerByte(i))
            {
                return true;
            }
        }

        matches = true;
        return true;
    }

    /// <summary>read a host-published bulk slot after BULK_REF metadata arrives on the control ring.</summary>
    public bool TryReadHostBulkPayload(int slotIndex, uint expectedCrc32, out byte[]? payload)
    {
        payload = null;
        if (!IsConnected || !_layoutV11 || slotIndex < 0 || slotIndex >= BridgeSharedRegionLayout.BulkSlotCount)
        {
            return false;
        }

        int byteOffset = BridgeSharedRegionLayout.BulkSlotByteOffset(slotIndex);
        lock (_frameLock)
        {
            byte[] header = new byte[BridgeSharedRegionLayout.BulkSlotHeaderSize];
            if (!ReadProcessMemory(_processHandle, SlotAddress(byteOffset), header, header.Length, out IntPtr headerRead)
                || headerRead.ToInt32() < header.Length)
            {
                return false;
            }

            ulong state = BitConverter.ToUInt64(header, 0);
            if (state != BridgeSharedRegionLayout.BulkStateHostReady)
            {
                return false;
            }

            int totalLength = BitConverter.ToInt32(header, 24);
            uint crc32 = BitConverter.ToUInt32(header, 28);
            if (totalLength <= 0 || totalLength > BridgeSharedRegionLayout.BulkSlotPayloadMax || crc32 != expectedCrc32)
            {
                return false;
            }

            payload = new byte[totalLength];
            if (!ReadProcessMemory(_processHandle, SlotAddress(byteOffset + BridgeSharedRegionLayout.BulkSlotHeaderSize), payload, payload.Length, out IntPtr payloadRead)
                || payloadRead.ToInt32() < totalLength)
            {
                payload = null;
                return false;
            }

            if (BridgeCrc32.Compute(payload) != expectedCrc32)
            {
                payload = null;
                return false;
            }

            byte[] consumed = BitConverter.GetBytes(BridgeSharedRegionLayout.BulkStateHostConsumed);
            WriteProcessMemory(_processHandle, SlotAddress(byteOffset), consumed, consumed.Length, out _);
            Trace?.Invoke($"bulk host slot={slotIndex} bytes={totalLength} crc={crc32:X8}");
            return true;
        }
    }

    public HostBulkReadResult ReadHostBulkPayload(
        Guid expectedCorrelationId,
        int slotIndex,
        int expectedLength,
        uint expectedCrc32)
    {
        if (!IsConnected || !_layoutV11)
        {
            return HostBulkReadResult.Failure(
                HouseholdExportTransportError.BridgeUnavailable,
                "A connected v11 memory bridge is required to read a household export.");
        }

        if (slotIndex < 0 || slotIndex >= BridgeSharedRegionLayout.BulkSlotCount)
        {
            return HostBulkReadResult.Failure(
                HouseholdExportTransportError.InvalidSlot,
                $"BULK slot index {slotIndex} is outside the configured slot range.");
        }

        if (expectedLength <= 0 || expectedLength > BridgeSharedRegionLayout.BulkSlotPayloadMax)
        {
            return HostBulkReadResult.Failure(
                HouseholdExportTransportError.PayloadLengthOutOfRange,
                $"Expected BULK length {expectedLength} is outside the single-slot range.");
        }

        int byteOffset = BridgeSharedRegionLayout.BulkSlotByteOffset(slotIndex);
        lock (_frameLock)
        {
            byte[] header = new byte[BridgeSharedRegionLayout.BulkSlotHeaderSize];
            if (!ReadProcessMemory(_processHandle, SlotAddress(byteOffset), header, header.Length, out IntPtr headerRead)
                || headerRead.ToInt32() < header.Length)
            {
                return HostBulkReadResult.Failure(
                    HouseholdExportTransportError.BulkReadFailed,
                    $"Could not read the BULK header for slot {slotIndex}.");
            }

            ulong state = BitConverter.ToUInt64(header, 0);
            if (state != BridgeSharedRegionLayout.BulkStateHostReady)
            {
                return HostBulkReadResult.Failure(
                    HouseholdExportTransportError.SlotNotReady,
                    $"BULK slot {slotIndex} is not host-ready (state=0x{state:X16}).");
            }

            byte[] correlationBytes = new byte[16];
            Buffer.BlockCopy(header, 8, correlationBytes, 0, correlationBytes.Length);
            Guid actualCorrelationId = new(correlationBytes);
            if (actualCorrelationId != expectedCorrelationId)
            {
                return HostBulkReadResult.Failure(
                    HouseholdExportTransportError.CorrelationMismatch,
                    $"BULK slot {slotIndex} belongs to {actualCorrelationId:N}, not {expectedCorrelationId:N}.");
            }

            int totalLength = BitConverter.ToInt32(header, 24);
            if (totalLength != expectedLength)
            {
                return HostBulkReadResult.Failure(
                    HouseholdExportTransportError.PayloadLengthMismatch,
                    $"BULK header length {totalLength} did not match response length {expectedLength}.");
            }

            uint headerCrc32 = BitConverter.ToUInt32(header, 28);
            if (headerCrc32 != expectedCrc32)
            {
                return HostBulkReadResult.Failure(
                    HouseholdExportTransportError.HeaderCrcMismatch,
                    $"BULK header CRC {headerCrc32:X8} did not match response CRC {expectedCrc32:X8}.");
            }

            byte[] payload = new byte[totalLength];
            if (!ReadProcessMemory(
                    _processHandle,
                    SlotAddress(byteOffset + BridgeSharedRegionLayout.BulkSlotHeaderSize),
                    payload,
                    payload.Length,
                    out IntPtr payloadRead)
                || payloadRead.ToInt32() < totalLength)
            {
                return HostBulkReadResult.Failure(
                    HouseholdExportTransportError.BulkReadFailed,
                    $"Could not read the complete payload from BULK slot {slotIndex}.");
            }

            uint actualCrc32 = BridgeCrc32.Compute(payload);
            if (actualCrc32 != expectedCrc32)
            {
                return HostBulkReadResult.Failure(
                    HouseholdExportTransportError.PayloadCrcMismatch,
                    $"BULK payload CRC {actualCrc32:X8} did not match response CRC {expectedCrc32:X8}.");
            }

            byte[] consumed = BitConverter.GetBytes(BridgeSharedRegionLayout.BulkStateHostConsumed);
            WriteProcessMemory(_processHandle, SlotAddress(byteOffset), consumed, consumed.Length, out _);
            Trace?.Invoke($"bulk household-export corr={expectedCorrelationId:N} slot={slotIndex} bytes={totalLength} crc={actualCrc32:X8}");
            return HostBulkReadResult.Success(payload);
        }
    }

    /// <summary> Phase 2a inbound: controller publishes probe/handler bytes into a ctl bulk slot.</summary>
    public bool TryWriteCtlBulkPayload(Guid correlationId, byte[] payload, out int slotIndex, out uint crc32)
    {
        slotIndex = -1;
        crc32 = 0;
        if (!IsConnected || !_layoutV11 || payload == null || payload.Length <= 0
            || payload.Length > BridgeSharedRegionLayout.BulkSlotPayloadMax)
        {
            return false;
        }

        lock (_frameLock)
        {
            if (!TryFindWritableCtlBulkSlot(out slotIndex))
            {
                return false;
            }

            crc32 = BridgeCrc32.Compute(payload);
            int byteOffset = BridgeSharedRegionLayout.BulkSlotByteOffset(slotIndex);
            byte[] writing = BitConverter.GetBytes(BridgeSharedRegionLayout.BulkStateCtlWriting);
            WriteProcessMemory(_processHandle, SlotAddress(byteOffset), writing, writing.Length, out _);

            byte[] header = new byte[BridgeSharedRegionLayout.BulkSlotHeaderSize];
            Array.Copy(BitConverter.GetBytes(BridgeSharedRegionLayout.BulkStateCtlWriting), 0, header, 0, 8);
            correlationId.ToByteArray().CopyTo(header, 8);
            Array.Copy(BitConverter.GetBytes(payload.Length), 0, header, 24, 4);
            Array.Copy(BitConverter.GetBytes(crc32), 0, header, 28, 4);
            WriteProcessMemory(_processHandle, SlotAddress(byteOffset), header, header.Length, out _);
            WriteProcessMemory(_processHandle, SlotAddress(byteOffset + BridgeSharedRegionLayout.BulkSlotHeaderSize), payload, payload.Length, out _);

            byte[] ready = BitConverter.GetBytes(BridgeSharedRegionLayout.BulkStateCtlReady);
            WriteProcessMemory(_processHandle, SlotAddress(byteOffset), ready, ready.Length, out _);
            Trace?.Invoke($"bulk ctl slot={slotIndex} bytes={payload.Length} crc={crc32:X8}");
            return true;
        }
    }

    private bool TryFindWritableCtlBulkSlot(out int slotIndex)
    {
        for (int i = 0; i < BridgeSharedRegionLayout.BulkSlotCount; i++)
        {
            int byteOffset = BridgeSharedRegionLayout.BulkSlotByteOffset(i);
            byte[] stateBytes = new byte[8];
            if (!ReadProcessMemory(_processHandle, SlotAddress(byteOffset), stateBytes, stateBytes.Length, out IntPtr read)
                || read.ToInt32() < 8)
            {
                continue;
            }

            ulong state = BitConverter.ToUInt64(stateBytes, 0);
            if (state == BridgeSharedRegionLayout.BulkStateEmpty || state == BridgeSharedRegionLayout.BulkStateCtlConsumed)
            {
                slotIndex = i;
                return true;
            }
        }

        slotIndex = -1;
        return false;
    }

    private bool ValidateV11Candidate(IntPtr candidate, byte[] controlProbe)
    {
        if (BitConverter.ToInt32(controlProbe, 36) != BridgeSharedRegionLayout.ControlSlotCount
            || BitConverter.ToInt32(controlProbe, 40) != BridgeSharedRegionLayout.SlotSize
            || BitConverter.ToInt32(controlProbe, 44) != BridgeSharedRegionLayout.SlotHeaderSize
            || BitConverter.ToInt32(controlProbe, 48) != BridgeSharedRegionLayout.HostSlotBase
            || BitConverter.ToInt32(controlProbe, 52) != BridgeSharedRegionLayout.HostSlotCount
            || BitConverter.ToInt32(controlProbe, 56) != BridgeSharedRegionLayout.CtlSlotBase
            || BitConverter.ToInt32(controlProbe, 60) != BridgeSharedRegionLayout.CtlSlotCount)
        {
            return false;
        }

        return ValidateBulkSubRegion(candidate);
    }

    private bool ValidateBulkSubRegion(IntPtr candidate)
    {
        byte[] bulkHeader = new byte[BridgeSharedRegionLayout.BulkHeaderSize];
        IntPtr bulkHeaderAddress = candidate + BridgeSharedRegionLayout.BulkRegionBaseOffset();
        if (!ReadProcessMemory(_processHandle, bulkHeaderAddress, bulkHeader, bulkHeader.Length, out IntPtr bytesReadPointer)
            || !MemoryReadComplete(bytesReadPointer, bulkHeader.Length))
        {
            return false;
        }

        for (int i = 0; i < 32; i++)
        {
            if (bulkHeader[i] != BridgeSharedRegionLayout.BulkMarkerByte(i))
            {
                return false;
            }
        }

        return BitConverter.ToInt32(bulkHeader, 32) == BridgeSharedRegionLayout.LayoutVersion
            && BitConverter.ToInt32(bulkHeader, 36) == BridgeSharedRegionLayout.BulkSlotCount
            && BitConverter.ToInt32(bulkHeader, 40) == BridgeSharedRegionLayout.BulkSlotSize
            && BitConverter.ToInt32(bulkHeader, 44) == BridgeSharedRegionLayout.BulkSlotHeaderSize;
    }

    private static byte[] BuildFindMarker()
    {
        byte[] marker = new byte[32];
        for (int i = 0; i < marker.Length; i++)
        {
            marker[i] = BridgeSharedRegionLayout.ControlMarkerByte(i);
        }

        return marker;
    }

    private static bool ProbeHasFindMarker(byte[] probe)
    {
        for (int i = 0; i < FindMarker.Length; i++)
        {
            if (probe[i] != FindMarker[i])
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] BuildBulkFindMarker()
    {
        byte[] marker = new byte[32];
        for (int i = 0; i < marker.Length; i++)
        {
            marker[i] = BridgeSharedRegionLayout.BulkMarkerByte(i);
        }

        return marker;
    }

    private static bool MemoryReadComplete(IntPtr bytesReadPointer, int requiredBytes) =>
        bytesReadPointer.ToInt64() >= requiredBytes;

    private static int IndexOf(byte[] buffer, int length, byte[] value, int startIndex)
    {
        for (int i = startIndex; i <= length - value.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < value.Length; j++)
            {
                if (buffer[i + j] != value[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    // Win32-named shims over the platform accessor so the many call sites below stay unchanged.
    // out IntPtr byte counts are preserved because callers compare them via MemoryReadComplete.
    private IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId) =>
        _native.Open(processId);

    private bool CloseHandle(IntPtr handle)
    {
        _native.Close(handle);
        return true;
    }

    private bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead)
    {
        bool ok = _native.Read(processHandle, baseAddress, buffer, size, out long read);
        bytesRead = new IntPtr(read);
        return ok;
    }

    private bool WriteProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesWritten)
    {
        bool ok = _native.Write(processHandle, baseAddress, buffer, size, out long written);
        bytesWritten = new IntPtr(written);
        return ok;
    }
}
