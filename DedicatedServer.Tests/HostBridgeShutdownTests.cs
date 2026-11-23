using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// HostBridgeServer.DisposeAsync ordering: sends waiting on the bridge end cancelled (not as a timeout), a send parked
/// on the write lock cannot hang, and the memory bridge closes only after the reader and every writing send have left.
/// </summary>
internal static partial class Program
{
    private static readonly TimeSpan HostBridgeShutdownWait = TimeSpan.FromSeconds(5);

    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;

    [Test("HostBridgeServer.DisposeCancelsPendingSend")]
    private static void HostBridgeServerDisposeCancelsPendingSend()
    {
        // Disposing the write lock before cancelling the send made its finally's Release() throw
        // ObjectDisposedException, which replaced the cancellation its caller expects.
        using PinnedHostBridge fixture = new();
        Task<BridgeResponse> send = fixture.Server.SendAsync(new BridgeCommand.Ping(Guid.NewGuid()), CancellationToken.None);
        Assert(!send.IsCompleted && fixture.Writes.Count == 1, "The send should have written its command and be waiting for the response.");

        // HeadlessSims3Host.StopAsync's order: the graceful DISCONNECT, then DisposeAsync.
        fixture.Server.SendDisconnectBestEffort();
        Assert(fixture.Writes.Count == 2 && fixture.Writes.Last().Contains(" DISCONNECT|", StringComparison.Ordinal),
            "DISCONNECT should still go out while a send waits. Writes: " + string.Join(" | ", fixture.Writes));

        fixture.DisposeServer();
        AssertSendCancelled(send, "The send waiting for its response");
        Assert(!fixture.Server.IsConnected, "DisposeAsync should close the bridge.");
    }

    [Test("HostBridgeServer.DisposeWakesSendWaitingForWriteLock")]
    private static void HostBridgeServerDisposeWakesSendWaitingForWriteLock()
    {
        // The second send waits for the write lock the first holds until its response. With a token that never
        // cancels it must still end when the controller shuts down, and without writing its command.
        using PinnedHostBridge fixture = new();
        Task<BridgeResponse> first = fixture.Server.SendAsync(new BridgeCommand.Ping(Guid.NewGuid()), CancellationToken.None);
        Task<BridgeResponse> second = fixture.Server.SendAsync(new BridgeCommand.Ping(Guid.NewGuid()), CancellationToken.None);
        Assert(!first.IsCompleted && !second.IsCompleted && fixture.Writes.Count == 1, "Only the first send should hold the write lock and have written.");

        fixture.DisposeServer();
        AssertSendCancelled(first, "The send holding the write lock");
        AssertSendCancelled(second, "The send waiting for the write lock");
        Assert(fixture.Writes.Count == 1, "The waiting send must not write once shutdown started. Writes: " + string.Join(" | ", fixture.Writes));
    }

    [Test("HostBridgeServer.DisposeWaitsForSendStillWriting")]
    private static void HostBridgeServerDisposeWaitsForSendStillWriting()
    {
        // A send that took the write lock just before shutdown may still be inside WriteCommand (waiting out the
        // host's mid-write sentinel, for one). Closing the bridge under it fails the send with something other
        // than a cancellation, so DisposeAsync must wait for it to leave the lock first.
        using PinnedHostBridge fixture = new();
        using ManualResetEventSlim writing = new();
        using ManualResetEventSlim finishWrite = new();
        fixture.Server.FrameRecord += line =>
        {
            if (line.StartsWith("ctl->host", StringComparison.Ordinal) && !writing.IsSet)
            {
                writing.Set();
                finishWrite.Wait(HostBridgeShutdownWait);
            }
        };
        try
        {
            Task<BridgeResponse> send = Task.Run(() => fixture.Server.SendAsync(new BridgeCommand.Ping(Guid.NewGuid()), CancellationToken.None));
            Assert(writing.Wait(HostBridgeShutdownWait), "The send should reach its frame write.");

            Task dispose = fixture.Server.DisposeAsync().AsTask();
            Assert(!dispose.IsCompleted, "DisposeAsync should wait for the send still writing.");
            Assert(fixture.Server.IsConnected, "The bridge must stay open while a send is still writing to it.");

            finishWrite.Set();
            AssertSendCancelled(send, "The send that was still writing");
            Assert(dispose.Wait(HostBridgeShutdownWait), "DisposeAsync should finish once the send left the write lock.");
            Assert(!fixture.Server.IsConnected, "DisposeAsync should close the bridge once the send left.");
        }
        finally
        {
            finishWrite.Set();
        }
    }

    [Test("HeadlessSims3Host.ShutdownCancelledCommandIsNotATimeout")]
    private static void HeadlessSims3HostShutdownCancelledCommandIsNotATimeout()
    {
        // SendBridgeCommandAsync turns its own command timeout into a TimeoutException. A command the bridge
        // cancelled because it is shutting down was reported the same way ("Timed out waiting 10 seconds")
        // although nothing waited; it must stay a cancellation.
        HeadlessSims3Host host = new(SessionConfig.FromArgs(Array.Empty<string>()));
        (typeof(HeadlessSims3Host).GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_started reflection target was not found.")).SetValue(host, true);
        using PinnedHostBridge fixture = new(host.Bridge);
        Task<string> diag = host.DiagStateAsync(CancellationToken.None);
        Assert(!diag.IsCompleted && fixture.Writes.Count == 1, "DIAG_STATE should have been written and be waiting for its response.");

        Assert(host.DisposeAsync().AsTask().Wait(HostBridgeShutdownWait), "HeadlessSims3Host.DisposeAsync should finish.");
        AssertSendCancelled(diag, "The command cancelled by shutdown");
    }

    [Test("HostBridgeServer.DisposeClosesBridgeAfterReaderStops")]
    private static void HostBridgeServerDisposeClosesBridgeAfterReaderStops()
    {
        // The reader reads a host frame and then stamps it consumed; closing the bridge between the two loses the
        // stamp. DisposeAsync must cancel the reader, wait for it, and only then close the bridge.
        using PinnedHostBridge fixture = new();
        using CancellationTokenSource readerCancellation = new();
        TaskCompletionSource reader = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HostBridgeField("_readerCancellation").SetValue(fixture.Server, readerCancellation);
        HostBridgeField("_readerTask").SetValue(fixture.Server, reader.Task);

        Task dispose = fixture.Server.DisposeAsync().AsTask();
        Assert(readerCancellation.IsCancellationRequested, "DisposeAsync should cancel the reader.");
        Assert(!dispose.IsCompleted, "DisposeAsync should wait for the reader to stop.");
        Assert(fixture.Server.IsConnected, "The bridge must stay open while the reader may still be reading it.");

        reader.SetException(new IOException("host frame read failed"));
        Assert(dispose.Wait(HostBridgeShutdownWait), "DisposeAsync should finish once the reader stops.");
        Assert(!fixture.Server.IsConnected, "DisposeAsync should close the bridge once the reader stopped.");
        Assert(fixture.Diagnostics.Any(line => line.Contains("bridge reader fault during shutdown (ignored): host frame read failed", StringComparison.Ordinal)),
            "A reader fault at shutdown should be logged, not lost. Diagnostics: " + string.Join(" | ", fixture.Diagnostics));
    }

    [Test("HostBridgeServer.ReaderStopsDrainingOnceCancelled")]
    private static void HostBridgeServerReaderStopsDrainingOnceCancelled()
    {
        // DisposeAsync waits for the reader before closing the bridge, so the reader's greedy drain must check its
        // token per frame: a host streaming frames (a chunked snapshot) would otherwise hold shutdown open.
        using PinnedHostBridge fixture = new();
        WriteInt32(fixture.Region, BridgeSharedRegionLayout.LayoutVersionOffset, BridgeSharedRegionLayout.LayoutVersion);
        int secondSlot = BridgeSharedRegionLayout.SlotByteOffset(BridgeSharedRegionLayout.HostSlotBase + 1);
        WriteHostFrame(fixture.Region, BridgeSharedRegionLayout.SlotByteOffset(BridgeSharedRegionLayout.HostSlotBase), 1, "EVENT|InteractionStarted|10|20");
        WriteHostFrame(fixture.Region, secondSlot, 2, "EVENT|InteractionStarted|11|21");

        using CancellationTokenSource readerCancellation = new();
        int events = 0;
        fixture.Server.AuthoritativeEvent += _ =>
        {
            events++;
            readerCancellation.Cancel();
        };
        MethodInfo readLoop = typeof(HostBridgeServer).GetMethod("ReadMemoryLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReadMemoryLoopAsync reflection target was not found.");
        Task readerTask = (Task)(readLoop.Invoke(fixture.Server, new object[] { readerCancellation.Token }) ?? throw new InvalidOperationException("ReadMemoryLoopAsync did not return a task."));

        Assert(readerTask.Wait(HostBridgeShutdownWait), "The reader should stop once cancelled.");
        Assert(events == 1, $"The reader should stop draining at the frame that saw the cancellation, got {events} events.");
        Assert(BitConverter.ToUInt64(fixture.Region, secondSlot) == BridgeSharedRegionLayout.HostSenderId, "The second frame should stay unconsumed.");
        fixture.DisposeServer();
    }

    private static void AssertSendCancelled(Task send, string what)
    {
        Assert(Task.WhenAny(send, Task.Delay(HostBridgeShutdownWait)).GetAwaiter().GetResult() == send,
            $"{what} was still waiting {HostBridgeShutdownWait.TotalSeconds:N0} s after DisposeAsync.");
        Exception? failure = null;
        try
        {
            send.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        Assert(failure is OperationCanceledException, $"{what} should end cancelled, got {(failure == null ? "a response" : failure.GetType().Name + ": " + failure.Message)}.");
    }

    private static void WriteHostFrame(byte[] region, int byteOffset, int sequence, string line)
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(line);
        Array.Copy(BitConverter.GetBytes(BridgeSharedRegionLayout.HostSenderId), 0, region, byteOffset, 8);
        WriteInt32(region, byteOffset + 8, sequence);
        WriteInt32(region, byteOffset + 12, payload.Length);
        Array.Copy(payload, 0, region, byteOffset + BridgeSharedRegionLayout.SlotHeaderSize, payload.Length);
    }

    private static FieldInfo HostBridgeField(string name) =>
        typeof(HostBridgeServer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(name + " reflection target was not found.");

    /// <summary>
    /// A memory: HostBridgeServer whose bridge points at a pinned, zeroed region in this process: a send writes its
    /// command there and then waits for a response nobody writes, like a host that stopped answering. No reader runs
    /// unless a test starts one.
    /// </summary>
    private sealed class PinnedHostBridge : IDisposable
    {
        private readonly GCHandle _pin;
        private readonly FieldInfo _processHandleField;
        private readonly FieldInfo _bufferAddressField;

        public HostBridgeServer Server { get; }
        public object Bridge { get; }
        public byte[] Region { get; } = new byte[BridgeSharedRegionLayout.TotalRegionSize];

        /// <summary>Command frames written into the region (FrameRecord "ctl->host" lines).</summary>
        public ConcurrentQueue<string> Writes { get; } = new();

        public ConcurrentQueue<string> Diagnostics { get; } = new();

        public PinnedHostBridge(HostBridgeServer? server = null)
        {
            Server = server ?? new HostBridgeServer("memory:TS3W");
            Server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Server.FrameRecord += line =>
            {
                if (line.StartsWith("ctl->host", StringComparison.Ordinal))
                {
                    Writes.Enqueue(line);
                }
            };
            Server.Diagnostic += Diagnostics.Enqueue;
            Bridge = HostBridgeField("_memoryBridge").GetValue(Server) ?? throw new InvalidOperationException("StartAsync did not create the memory bridge.");
            Type bridgeType = Bridge.GetType();
            _processHandleField = bridgeType.GetField("_processHandle", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("_processHandle reflection target was not found.");
            _bufferAddressField = bridgeType.GetField("_bufferAddress", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("_bufferAddress reflection target was not found.");
            _pin = GCHandle.Alloc(Region, GCHandleType.Pinned);
            _processHandleField.SetValue(Bridge, OpenCurrentProcessForVmWrite());
            _bufferAddressField.SetValue(Bridge, _pin.AddrOfPinnedObject());
        }

        public void DisposeServer()
        {
            Assert(Server.DisposeAsync().AsTask().Wait(HostBridgeShutdownWait), $"DisposeAsync did not finish within {HostBridgeShutdownWait.TotalSeconds:N0} s.");
        }

        public void Dispose()
        {
            // The bridge's Dispose closes and clears the handle; close it here only when a test ended before that.
            IntPtr handle = (IntPtr)(_processHandleField.GetValue(Bridge) ?? IntPtr.Zero);
            _processHandleField.SetValue(Bridge, IntPtr.Zero);
            _bufferAddressField.SetValue(Bridge, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                CloseVmHandle(handle);
            }
            _pin.Free();
        }
    }

    private static IntPtr OpenCurrentProcessForVmWrite()
    {
        // As OpenCurrentProcessForVmRead: the Linux accessor takes the pid as the handle. Sends also write.
        if (!OperatingSystem.IsWindows())
        {
            return new IntPtr(Environment.ProcessId);
        }

        IntPtr handle = OpenProcess(ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation, false, Environment.ProcessId);
        return handle != IntPtr.Zero ? handle : throw new InvalidOperationException("OpenProcess for current PID failed.");
    }
}
