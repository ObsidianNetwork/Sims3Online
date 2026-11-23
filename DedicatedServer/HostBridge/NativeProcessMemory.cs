using System.Globalization;
using System.Runtime.InteropServices;

namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>
/// A committed, readable/writable memory region of a target process, in the neutral form the
/// bridge scanner needs. Replaces the Win32 MEMORY_BASIC_INFORMATION so <see cref="ProcessMemoryBridge"/>
/// can walk either a Windows process (VirtualQueryEx) or a Linux/Wine process (/proc/pid/maps).
/// </summary>
internal readonly struct NativeMemoryRegion
{
    public long BaseAddress { get; init; }
    public long RegionSize { get; init; }

    /// <summary>True when this region is a plausible bridge-buffer host (committed, private, r/w).</summary>
    public bool ReadableWritable { get; init; }

    /// <summary>False signals the enumeration has passed the last mapping (stop scanning).</summary>
    public bool Valid { get; init; }
}

/// <summary>
/// Cross-process memory primitives behind the 5 Win32 calls the bridge historically used
/// (OpenProcess / CloseHandle / VirtualQueryEx / ReadProcessMemory / WriteProcessMemory).
/// Implementations are STATELESS w.r.t. the returned handle: the handle carries everything a
/// later Read/Write/Query needs, so a handle produced by one call can be used by any instance
/// (the DedicatedServer.Tests self-read fixtures rely on this).
/// </summary>
internal interface INativeProcessMemory
{
    /// <summary>Returns an opaque handle for <paramref name="processId"/>, or IntPtr.Zero on failure.</summary>
    IntPtr Open(int processId);

    void Close(IntPtr handle);

    /// <summary>
    /// VirtualQueryEx analog: describe the mapped region at or after <paramref name="address"/>.
    /// Sets <see cref="NativeMemoryRegion.Valid"/> false once no mapping remains.
    /// </summary>
    bool TryQueryRegion(IntPtr handle, long address, out NativeMemoryRegion region);

    bool Read(IntPtr handle, IntPtr address, byte[] buffer, int size, out long bytesRead);

    bool Write(IntPtr handle, IntPtr address, byte[] buffer, int size, out long bytesWritten);
}

internal static class NativeProcessMemory
{
    /// <summary>
    /// Shared, stateless accessor for the current OS. Safe to reuse across bridge instances and
    /// from test code because no per-target state is retained between calls.
    /// </summary>
    public static INativeProcessMemory Instance { get; } =
        OperatingSystem.IsWindows()
            ? new WindowsProcessMemory()
            : new LinuxProcessMemory();

    /// <summary>
    /// Linux: mark the current process as a child subreaper (prctl PR_SET_CHILD_SUBREAPER) so
    /// that when Wine's loader chain exits, the orphaned TS3W.exe reparents to the CONTROLLER
    /// instead of systemd. That keeps the game a descendant, which is what lets process_vm_readv
    /// work under the default kernel.yama.ptrace_scope=1 with no system configuration.
    /// No-op on Windows; failure is non-fatal (a ptrace_scope=0 host doesn't need it).
    /// </summary>
    public static bool TryEnableChildSubreaper()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        const int PR_SET_CHILD_SUBREAPER = 36;
        try
        {
            return prctl(PR_SET_CHILD_SUBREAPER, 1, 0, 0, 0) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    /// <summary>
    /// Process.GetProcessesByName with Wine awareness: a Wine game process keeps its PE file name
    /// as the Linux comm ("TS3W.exe"), so a Windows-style lookup for "TS3W" also probes
    /// "TS3W.exe" on non-Windows hosts. Callers own disposal of the returned processes.
    /// </summary>
    public static System.Diagnostics.Process[] GetProcessesByWindowsName(string processName)
    {
        System.Diagnostics.Process[] direct = System.Diagnostics.Process.GetProcessesByName(processName);
        if (OperatingSystem.IsWindows() || direct.Length > 0 || processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return direct;
        }

        return System.Diagnostics.Process.GetProcessesByName(processName + ".exe");
    }
}

/// <summary>Original Win32 path, unchanged in behavior — just relocated behind the seam.</summary>
internal sealed class WindowsProcessMemory : INativeProcessMemory
{
    private const uint MemCommit = 0x00001000;
    private const uint PageNoAccess = 0x01;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;
    private const uint MemPrivate = 0x00020000;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;

    public IntPtr Open(int processId) =>
        OpenProcess(ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, processId);

    public void Close(IntPtr handle) => CloseHandle(handle);

    public bool TryQueryRegion(IntPtr handle, long address, out NativeMemoryRegion region)
    {
        int mbiSize = Marshal.SizeOf<MemoryBasicInformation>();
        UIntPtr result = VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation info, (UIntPtr)mbiSize);
        if (result == UIntPtr.Zero)
        {
            region = default;
            return false;
        }

        region = new NativeMemoryRegion
        {
            BaseAddress = info.BaseAddress.ToInt64(),
            RegionSize = checked((long)info.RegionSize.ToUInt64()),
            ReadableWritable = IsReadableWritable(info),
            Valid = true,
        };
        return true;
    }

    public bool Read(IntPtr handle, IntPtr address, byte[] buffer, int size, out long bytesRead)
    {
        bool ok = ReadProcessMemory(handle, address, buffer, size, out IntPtr read);
        bytesRead = read.ToInt64();
        return ok;
    }

    public bool Write(IntPtr handle, IntPtr address, byte[] buffer, int size, out long bytesWritten)
    {
        bool ok = WriteProcessMemory(handle, address, buffer, size, out IntPtr written);
        bytesWritten = written.ToInt64();
        return ok;
    }

    private static bool IsReadableWritable(MemoryBasicInformation info)
    {
        if ((info.State & MemCommit) == 0 ||
            (info.Type & MemPrivate) == 0 ||
            (info.Protect & PageGuard) != 0 ||
            (info.Protect & PageNoAccess) != 0)
        {
            return false;
        }

        uint protect = info.Protect & 0xFF;
        return protect is PageReadWrite or PageWriteCopy or PageExecuteReadWrite or PageExecuteWriteCopy;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(
        IntPtr processHandle,
        IntPtr address,
        out MemoryBasicInformation buffer,
        UIntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr bytesWritten);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}

/// <summary>
/// Linux path: the Wine TS3W process is an ordinary Linux process, so /proc/&lt;pid&gt;/maps
/// enumerates its mappings and process_vm_readv/writev move bytes without a handle. The mod plants
/// the bridge region in an anonymous private rwx mapping in the low 4 GB (new-WoW64 keeps the
/// 32-bit guest address space there), so the existing scan loop's 0x7FFFFFFF ceiling still applies.
///
/// The handle is simply the pid boxed as an IntPtr — process_vm_* take the pid directly and require
/// no open fd, which keeps the accessor stateless. Reading the CURRENT process (self pid) always
/// passes the kernel ptrace check; reading a launched child TS3W passes under ptrace_scope=1.
/// Re-attaching to an unrelated running TS3W needs CAP_SYS_PTRACE or ptrace_scope=0 (a deploy
/// concern, not a code one).
/// </summary>
internal sealed class LinuxProcessMemory : INativeProcessMemory
{
    public IntPtr Open(int processId)
    {
        // No fd to acquire; just confirm the process exists so IsConnected reflects reality.
        return Directory.Exists($"/proc/{processId}") ? new IntPtr(processId) : IntPtr.Zero;
    }

    public void Close(IntPtr handle)
    {
        // Stateless: nothing to release.
    }

    public bool TryQueryRegion(IntPtr handle, long address, out NativeMemoryRegion region)
    {
        int pid = handle.ToInt32();
        region = default;

        string mapsPath = $"/proc/{pid}/maps";
        if (!File.Exists(mapsPath))
        {
            return false;
        }

        try
        {
            // Return the first mapping that ends past `address` — i.e. the region containing it,
            // or the next one up if `address` fell in an unmapped gap. Mirrors how the caller
            // walks VirtualQueryEx: query, then advance to base+size.
            foreach (string line in File.ReadLines(mapsPath))
            {
                if (!TryParseMapsLine(line, out long start, out long end, out bool readable, out bool writable, out bool priv))
                {
                    continue;
                }

                if (end <= address)
                {
                    continue;
                }

                region = new NativeMemoryRegion
                {
                    BaseAddress = start,
                    RegionSize = end - start,
                    ReadableWritable = readable && writable && priv,
                    Valid = true,
                };
                return true;
            }
        }
        catch (IOException)
        {
            // maps can change under us; treat as end-of-enumeration.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    public bool Read(IntPtr handle, IntPtr address, byte[] buffer, int size, out long bytesRead)
    {
        int pid = handle.ToInt32();
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            IOVec local = new() { Base = pin.AddrOfPinnedObject(), Length = (UIntPtr)size };
            IOVec remote = new() { Base = address, Length = (UIntPtr)size };
            long n = process_vm_readv(pid, ref local, 1, ref remote, 1, 0);
            bytesRead = n < 0 ? 0 : n;
            return n == size;
        }
        finally
        {
            pin.Free();
        }
    }

    public bool Write(IntPtr handle, IntPtr address, byte[] buffer, int size, out long bytesWritten)
    {
        int pid = handle.ToInt32();
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            IOVec local = new() { Base = pin.AddrOfPinnedObject(), Length = (UIntPtr)size };
            IOVec remote = new() { Base = address, Length = (UIntPtr)size };
            long n = process_vm_writev(pid, ref local, 1, ref remote, 1, 0);
            bytesWritten = n < 0 ? 0 : n;
            return n == size;
        }
        finally
        {
            pin.Free();
        }
    }

    private static bool TryParseMapsLine(string line, out long start, out long end, out bool readable, out bool writable, out bool priv)
    {
        start = end = 0;
        readable = writable = priv = false;

        // Format: "5567f0a00000-5567f0a21000 rw-p 00000000 00:00 0    [heap]"
        int dash = line.IndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        int space = line.IndexOf(' ', dash);
        if (space < 0)
        {
            return false;
        }

        if (!ulong.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong startU) ||
            !ulong.TryParse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong endU))
        {
            return false;
        }

        start = unchecked((long)startU);
        end = unchecked((long)endU);

        // perms are the 4 chars after the single space: r/w/x/(s|p)
        int p = space + 1;
        if (p + 4 > line.Length)
        {
            return false;
        }

        readable = line[p] == 'r';
        writable = line[p + 1] == 'w';
        priv = line[p + 3] == 'p';
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IOVec
    {
        public IntPtr Base;
        public UIntPtr Length;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern long process_vm_readv(
        int pid,
        ref IOVec localIov,
        ulong localIovCount,
        ref IOVec remoteIov,
        ulong remoteIovCount,
        ulong flags);

    [DllImport("libc", SetLastError = true)]
    private static extern long process_vm_writev(
        int pid,
        ref IOVec localIov,
        ulong localIovCount,
        ref IOVec remoteIov,
        ulong remoteIovCount,
        ulong flags);
}
