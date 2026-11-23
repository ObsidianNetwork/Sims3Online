using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>
/// Controller side of the SimLink sidecar (SimLink/src/simlink_layout.h, layout v1): opens the shared-memory section a
/// TS3 process's sidecar created, runs the registration handshake, and drains its record ring. Mirrors the native
/// layout; change both together. A TS3 in a Sandboxie box keeps its named objects under
/// <c>\Sandbox\&lt;user&gt;\&lt;box&gt;\Session_&lt;n&gt;\Sessions\&lt;n&gt;\BaseNamedObjects</c>, reachable by NT path only.
/// </summary>
public sealed class SimLinkShadowReader : IDisposable
{
    public const int LayoutVersion = 1;
    public const int HeaderSize = 256;
    public const int RecordHeaderSize = 24;
    public const ushort KindData = 1;
    public const ushort KindPad = 2;

    public const uint StateCreated = 0;
    public const uint StateRegisterRequested = 1;
    public const uint StateRegistered = 2;
    public const uint StateFailed = 3;

    private const int OffLayoutVersion = 8;
    private const int OffPid = 16;
    private const int OffState = 20;
    private const int OffError = 24;
    private const int OffRingOffset = 28;
    private const int OffRingSize = 32;
    private const int OffStringLengthOffset = 36;
    private const int OffQpcFrequency = 40;
    private const int OffWritePos = 48;
    private const int OffReadPos = 56;
    private const int OffPublished = 64;
    private const int OffDropped = 72;
    private const int OffContended = 80;
    private const int OffBadArgs = 88;
    private const int OffNativeVersion = 96;
    private const int OffProbeResult = 100;
    private static readonly byte[] Magic = "SIMLINK1"u8.ToArray();

    private readonly IntPtr _section;
    private readonly IntPtr _view;
    private readonly IntPtr _controlEvent;
    private readonly IntPtr _dataEvent;
    private readonly IntPtr _ring;
    private readonly uint _ringSize;
    private bool _disposed;

    /// <summary>One published record. <paramref name="Qpc"/> is QueryPerformanceCounter in the game at publish,
    /// <paramref name="ArrivalQpc"/> the controller's <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> when read
    /// (the same counter on one machine).</summary>
    public readonly record struct Record(int Lane, int Seq, long Qpc, string Payload, long ArrivalQpc);

    private SimLinkShadowReader(IntPtr section, IntPtr view, IntPtr controlEvent, IntPtr dataEvent)
    {
        _section = section;
        _view = view;
        _controlEvent = controlEvent;
        _dataEvent = dataEvent;
        _ring = view + ReadInt32(OffRingOffset);
        _ringSize = (uint)ReadInt32(OffRingSize);
    }

    public static string SectionName(int processId) => "SimsOnline.SimLink." + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The sidecar section of a TS3 process: by Win32 name, or by NT path when it runs in a Sandboxie box.
    /// Null when there is none (no sidecar loaded, or another layout); <paramref name="reason"/> says why.</summary>
    public static SimLinkShadowReader? TryOpen(int processId, string? sandboxieBox, out string reason) =>
        TryOpen(processId, sandboxieBox, string.Empty, out reason);

    /// <summary>A sidecar section by suffix: "" for the game → controller ring, ".in" for the controller → game ring.</summary>
    public static SimLinkShadowReader? TryOpen(int processId, string? sandboxieBox, string suffix, out string reason)
    {
        string name = SectionName(processId) + suffix;
        if (string.IsNullOrEmpty(sandboxieBox))
        {
            return TryOpenByName(@"Local\" + name, out reason);
        }

        uint session = ProcessSessionId();
        string user = Environment.UserName;
        string directory = $@"\Sandbox\{user}\{sandboxieBox}\Session_{session}\Sessions\{session}\BaseNamedObjects\";
        IntPtr section = NtOpen(directory + name, isSection: true, SectionMapRead | SectionMapWrite, out int status);
        if (section == IntPtr.Zero)
        {
            reason = $"no section at {directory}{name} (0x{status:X8})";
            return null;
        }

        IntPtr control = NtOpen(directory + name + ".control", isSection: false, EventModifyState | Synchronize, out _);
        IntPtr data = NtOpen(directory + name + ".data", isSection: false, EventModifyState | Synchronize, out _);
        return Map(section, control, data, out reason);
    }

    /// <summary>A section by Win32 name (tests, and unboxed TS3s through <see cref="TryOpen"/>).</summary>
    public static SimLinkShadowReader? TryOpenByName(string win32Name, out string reason)
    {
        IntPtr section = OpenFileMappingW(FileMapRead | FileMapWrite, false, win32Name);
        if (section == IntPtr.Zero)
        {
            reason = $"no section {win32Name} ({Marshal.GetLastWin32Error()})";
            return null;
        }

        IntPtr control = OpenEventW(EventModifyState | Synchronize, false, win32Name + ".control");
        IntPtr data = OpenEventW(EventModifyState | Synchronize, false, win32Name + ".data");
        return Map(section, control, data, out reason);
    }

    private static SimLinkShadowReader? Map(IntPtr section, IntPtr control, IntPtr data, out string reason)
    {
        IntPtr view = MapViewOfFile(section, FileMapRead | FileMapWrite, 0, 0, UIntPtr.Zero);
        if (view == IntPtr.Zero)
        {
            reason = $"MapViewOfFile failed ({Marshal.GetLastWin32Error()})";
            CloseAll(section, control, data);
            return null;
        }

        byte[] magic = new byte[8];
        Marshal.Copy(view, magic, 0, 8);
        int layout = Marshal.ReadInt32(view, OffLayoutVersion);
        if (!magic.AsSpan().SequenceEqual(Magic) || layout != LayoutVersion)
        {
            reason = $"unexpected layout (magic={System.Text.Encoding.ASCII.GetString(magic)}, version={layout})";
            UnmapViewOfFile(view);
            CloseAll(section, control, data);
            return null;
        }

        reason = control == IntPtr.Zero || data == IntPtr.Zero ? "opened (events missing)" : "opened";
        return new SimLinkShadowReader(section, view, control, data);
    }

    public uint State => (uint)ReadInt32(OffState);
    public uint Error => (uint)ReadInt32(OffError);
    public int Pid => ReadInt32(OffPid);
    public int NativeVersion => ReadInt32(OffNativeVersion);
    public int ProbeResult => ReadInt32(OffProbeResult);
    public int StringLengthOffset => ReadInt32(OffStringLengthOffset);
    public long QpcFrequency => Marshal.ReadInt64(_view, OffQpcFrequency);
    public long Published => Marshal.ReadInt64(_view, OffPublished);
    public long Dropped => Marshal.ReadInt64(_view, OffDropped);
    public long Contended => Marshal.ReadInt64(_view, OffContended);
    public long BadArgs => Marshal.ReadInt64(_view, OffBadArgs);
    public uint RingSize => _ringSize;

    /// <summary>Asks the sidecar to register its internal calls (only once Mono is up: the bridge is connected) and
    /// waits for the answer. True when registered.</summary>
    public bool RequestRegistration(TimeSpan timeout)
    {
        if (State == StateRegistered)
        {
            return true;
        }

        if (_controlEvent == IntPtr.Zero)
        {
            return false;
        }

        Marshal.WriteInt32(_view, OffState, (int)StateRegisterRequested);
        Thread.MemoryBarrier();
        SetEvent(_controlEvent);
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            uint state = State;
            if (state == StateRegistered)
            {
                return true;
            }

            if (state == StateFailed)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    /// <summary>Waits up to <paramref name="milliseconds"/> for the sidecar's data event (records published).</summary>
    public bool WaitForData(int milliseconds) =>
        _dataEvent != IntPtr.Zero && WaitForSingleObject(_dataEvent, (uint)milliseconds) == 0;

    /// <summary>Reads every record published so far into <paramref name="into"/> and frees their space. Returns the
    /// number read.</summary>
    public int Drain(List<Record> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        long write = Marshal.ReadInt64(_view, OffWritePos);
        Thread.MemoryBarrier();
        long read = Marshal.ReadInt64(_view, OffReadPos);
        int before = into.Count;
        read = ReadRecords(read, write, into);
        Thread.MemoryBarrier();
        Marshal.WriteInt64(_view, OffReadPos, read);
        return into.Count - before;
    }

    /// <summary>Writer side: takes back every record the consumer hasn't read (the client controller reclaiming its
    /// backlog when the game stopped reading). The read position moves to the write position by compare-and-swap, as
    /// the native consumer commits its reads, so each record goes to exactly one side: a native read already under way
    /// commits nothing and applies nothing (ring.h, kConsumeClaimed). Returns the records taken, in order.</summary>
    public unsafe List<Record> ClaimUnread()
    {
        var taken = new List<Record>();
        long* readPosition = (long*)((byte*)_view + OffReadPos);
        while (true)
        {
            long write = Marshal.ReadInt64(_view, OffWritePos);
            Thread.MemoryBarrier();
            long read = Interlocked.Read(ref *readPosition);
            if (read >= write)
            {
                return taken;
            }

            var records = new List<Record>();
            long end = ReadRecords(read, write, records);
            if (Interlocked.CompareExchange(ref *readPosition, end, read) == read)
            {
                taken.AddRange(records);
                return taken;
            }

            // The consumer committed a read meanwhile: those records are its own. Take what is left.
        }
    }

    // Parses the records in [read, write) into `into`; returns the position after the last one.
    private long ReadRecords(long read, long write, List<Record> into)
    {
        long arrival = System.Diagnostics.Stopwatch.GetTimestamp();
        while (read < write)
        {
            uint position = (uint)(read & (_ringSize - 1));
            IntPtr at = _ring + (int)position;
            int size = Marshal.ReadInt32(at, 0);
            ushort kind = (ushort)Marshal.ReadInt16(at, 4);
            if (size <= 0 || (size & 7) != 0 || size > _ringSize - position)
            {
                throw new InvalidDataException($"SimLink record at {read} has size {size} (ring {_ringSize}, position {position}).");
            }

            if (kind == KindData)
            {
                ushort lane = (ushort)Marshal.ReadInt16(at, 6);
                int seq = Marshal.ReadInt32(at, 8);
                int chars = Marshal.ReadInt32(at, 12);
                long qpc = Marshal.ReadInt64(at, 16);
                if (chars < 0 || RecordHeaderSize + (long)chars * 2 > size)
                {
                    throw new InvalidDataException($"SimLink record at {read} claims {chars} chars in {size} bytes.");
                }

                into.Add(new Record(lane, seq, qpc, Marshal.PtrToStringUni(at + RecordHeaderSize, chars), arrival));
            }
            else if (kind != KindPad)
            {
                throw new InvalidDataException($"SimLink record at {read} has kind {kind}.");
            }

            read += size;
        }

        return read;
    }

    private readonly object _writeGate = new();

    /// <summary>Producer side (the controller → game ring): one record, the same algorithm as the native producer.
    /// Never blocks; false when the ring is full or the record too large (counted in Dropped).</summary>
    public bool Publish(ushort lane, int seq, string payload, long qpc)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_writeGate)
        {
            int need = (RecordHeaderSize + payload.Length * 2 + 7) & ~7;
            long write = Marshal.ReadInt64(_view, OffWritePos);
            long read = Marshal.ReadInt64(_view, OffReadPos);
            uint position = (uint)(write & (_ringSize - 1));
            uint tail = _ringSize - position;
            long total = need <= tail ? need : tail + (long)need;
            if (payload.Length > _ringSize / 4 || need > _ringSize / 2 || write - read + total > _ringSize)
            {
                Marshal.WriteInt64(_view, OffDropped, Marshal.ReadInt64(_view, OffDropped) + 1);
                return false;
            }

            Commit(WriteRecord(write, lane, seq, payload, qpc), 1);
            return true;
        }
    }

    // Writes one record at byte position `write` (a pad first when it doesn't fit before the ring's end) and returns
    // the position after it. Not visible to the reader until Commit moves WritePos; the caller checked the space.
    private long WriteRecord(long write, ushort lane, int seq, string payload, long qpc)
    {
        int need = (RecordHeaderSize + payload.Length * 2 + 7) & ~7;
        uint position = (uint)(write & (_ringSize - 1));
        uint tail = _ringSize - position;
        if (need > tail)
        {
            IntPtr pad = _ring + (int)position;
            Marshal.WriteInt32(pad, 0, (int)tail);
            Marshal.WriteInt16(pad, 4, (short)KindPad);
            write += tail;
            position = 0;
        }

        IntPtr at = _ring + (int)position;
        Marshal.WriteInt32(at, 0, need);
        Marshal.WriteInt16(at, 4, (short)KindData);
        Marshal.WriteInt16(at, 6, (short)lane);
        Marshal.WriteInt32(at, 8, seq);
        Marshal.WriteInt32(at, 12, payload.Length);
        Marshal.WriteInt64(at, 16, qpc);
        if (payload.Length > 0)
        {
            Marshal.Copy(payload.ToCharArray(), 0, at + RecordHeaderSize, payload.Length);
        }

        return write + need;
    }

    // Makes everything written up to `write` visible to the reader at once.
    private void Commit(long write, int records)
    {
        Thread.MemoryBarrier();
        Marshal.WriteInt64(_view, OffWritePos, write);
        Marshal.WriteInt64(_view, OffPublished, Marshal.ReadInt64(_view, OffPublished) + records);
    }

    private static int SeqOf(string line)
    {
        int split = line.IndexOf('|');
        return split > 0 && int.TryParse(line.AsSpan(0, split), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
    }

    /// <summary>All of <paramref name="lines"/> or none: reserves room for every record (plus the one pad a wrap can
    /// insert) before writing any, so an event is never half in the ring. Each record's seq is the line's "seq|"
    /// prefix (0 for synthetic lines). False when it doesn't fit (nothing written, Dropped counted once).</summary>
    public bool PublishBatch(ushort lane, IReadOnlyList<string> lines, long qpc)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_writeGate)
        {
            long total = 0;
            int largest = 0;
            foreach (string line in lines)
            {
                int need = (RecordHeaderSize + line.Length * 2 + 7) & ~7;
                if (line.Length > _ringSize / 4 || need > _ringSize / 2)
                {
                    Marshal.WriteInt64(_view, OffDropped, Marshal.ReadInt64(_view, OffDropped) + 1);
                    return false;
                }

                total += need;
                largest = Math.Max(largest, need);
            }

            long write = Marshal.ReadInt64(_view, OffWritePos);
            long read = Marshal.ReadInt64(_view, OffReadPos);
            if (write - read + total + largest > _ringSize)
            {
                Marshal.WriteInt64(_view, OffDropped, Marshal.ReadInt64(_view, OffDropped) + 1);
                return false;
            }

            foreach (string line in lines)
            {
                write = WriteRecord(write, lane, SeqOf(line), line, qpc);
            }

            // One commit: the reader never sees part of the event.
            Commit(write, lines.Count);
            return true;
        }
    }

    /// <summary>Client ring path: the batches of one SimAnimation event, as the bridge would send them, each followed by
    /// a batch-end marker (lane 5, empty) so the game applies exactly the same batches. All or nothing.</summary>
    public bool PublishBatches(IReadOnlyList<IReadOnlyList<string>> batches, long qpc)
    {
        ArgumentNullException.ThrowIfNull(batches);
        lock (_writeGate)
        {
            long total = 0;
            int largest = RecordHeaderSize;
            foreach (IReadOnlyList<string> batch in batches)
            {
                foreach (string line in batch)
                {
                    int need = (RecordHeaderSize + line.Length * 2 + 7) & ~7;
                    if (line.Length > _ringSize / 4 || need > _ringSize / 2)
                    {
                        Marshal.WriteInt64(_view, OffDropped, Marshal.ReadInt64(_view, OffDropped) + 1);
                        return false;
                    }

                    total += need;
                    largest = Math.Max(largest, need);
                }

                total += RecordHeaderSize; // the marker
            }

            long write = Marshal.ReadInt64(_view, OffWritePos);
            long read = Marshal.ReadInt64(_view, OffReadPos);
            if (write - read + total + largest > _ringSize)
            {
                Marshal.WriteInt64(_view, OffDropped, Marshal.ReadInt64(_view, OffDropped) + 1);
                return false;
            }

            int records = 0;
            foreach (IReadOnlyList<string> batch in batches)
            {
                foreach (string line in batch)
                {
                    write = WriteRecord(write, LaneReplayLine, SeqOf(line), line, qpc);
                    records++;
                }

                write = WriteRecord(write, LaneBatchEnd, 0, string.Empty, qpc);
                records++;
            }

            // One commit for the whole event: the game never reads part of a batch.
            Commit(write, records);
            return true;
        }
    }

    public const ushort LaneReplayLine = 1;
    public const ushort LaneBatchEnd = 5;

    /// <summary>The consumer's position (bytes consumed so far): a stuck consumer stops moving it.</summary>
    public long ReadPosition => Marshal.ReadInt64(_view, OffReadPos);

    /// <summary>The producer's position (bytes written so far).</summary>
    public long WritePosition => Marshal.ReadInt64(_view, OffWritePos);

    private int ReadInt32(int offset) => Marshal.ReadInt32(_view, offset);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnmapViewOfFile(_view);
        CloseAll(_section, _controlEvent, _dataEvent);
    }

    private static void CloseAll(params IntPtr[] handles)
    {
        foreach (IntPtr handle in handles)
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    private static uint ProcessSessionId()
    {
        return ProcessIdToSessionId((uint)Environment.ProcessId, out uint session) ? session : 1u;
    }

    private static IntPtr NtOpen(string ntPath, bool isSection, uint access, out int status)
    {
        IntPtr buffer = Marshal.StringToHGlobalUni(ntPath);
        try
        {
            var name = new UnicodeString
            {
                Length = (ushort)(ntPath.Length * 2),
                MaximumLength = (ushort)(ntPath.Length * 2 + 2),
                Buffer = buffer,
            };
            IntPtr namePtr = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(name, namePtr, false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    ObjectName = namePtr,
                    Attributes = ObjCaseInsensitive,
                };
                status = isSection
                    ? NtOpenSection(out IntPtr handle, access, ref attributes)
                    : NtOpenEvent(out handle, access, ref attributes);
                return status == 0 ? handle : IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(namePtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint FileMapWrite = 0x0002;
    private const uint FileMapRead = 0x0004;
    private const uint SectionMapWrite = 0x0002;
    private const uint SectionMapRead = 0x0004;
    private const uint EventModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;
    private const uint ObjCaseInsensitive = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr section, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("ntdll.dll")]
    private static extern int NtOpenSection(out IntPtr handle, uint access, ref ObjectAttributes attributes);

    [DllImport("ntdll.dll")]
    private static extern int NtOpenEvent(out IntPtr handle, uint access, ref ObjectAttributes attributes);
}
