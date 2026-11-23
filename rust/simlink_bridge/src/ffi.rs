//! The one module of this crate that contains `unsafe` (card 56 §D U1). It exposes a safe API only (U2):
//! no `pub unsafe fn`, no raw pointer and no `windows-sys` type in any `pub` signature. Every `unsafe`
//! block below carries a `SAFETY:` comment, and `rust/simlink_bridge/AUDIT_UNSAFE.md` lists each entry
//! point with the tests that fail if it is mis-wired (U3) and the error-path test per failure kind (U4).
//!
//! The platform calls stay behind one internal trait, [`Platform`], so a second platform (Linux
//! `process_vm_readv` for Wine hosts, §G row 9, `DEFER`) lands in this module only. The oracle's seam is
//! `DedicatedServer/HostBridge/NativeProcessMemory.cs`.
#![deny(unsafe_op_in_unsafe_fn)]

use crate::error::BridgeError;
use crate::memory::{walk_regions, GuestMemory, RegionInfo};

/// The platform seam: open a process by id, read, write, query its address space, ask whether it exited.
trait Platform: Sized {
    fn open(pid: u32) -> Result<Self, BridgeError>;
    fn read(&self, addr: u32, buf: &mut [u8]) -> Result<(), BridgeError>;
    fn write(&self, addr: u32, data: &[u8]) -> Result<(), BridgeError>;
    fn query(&self, addr: u32) -> Result<Option<RegionInfo>, BridgeError>;
    fn exited(&self) -> bool;
}

/// An attach to another process's memory (`TRAIT_CONTRACT.md` "Card 56"). The handle is private and closed
/// on drop; no raw pointer or `windows-sys` type appears in any `pub` signature.
pub struct ProcessAttach {
    inner: imp::Process,
    pid: u32,
}

impl ProcessAttach {
    /// `OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE)`,
    /// the oracle's access mask. A process that does not exist is `process_exited`.
    pub fn open(pid: u32) -> Result<Self, BridgeError> {
        Ok(Self { inner: <imp::Process as Platform>::open(pid)?, pid })
    }

    /// `ReadProcessMemory` of exactly `buf.len()` bytes; a short read is `ShortRead`, never a zero fill.
    pub fn read(&self, addr: u32, buf: &mut [u8]) -> Result<(), BridgeError> {
        Platform::read(&self.inner, addr, buf)
    }

    /// `WriteProcessMemory` of all of `bytes`; a failure is `WriteFailed`, never retried.
    pub fn write(&self, addr: u32, bytes: &[u8]) -> Result<(), BridgeError> {
        Platform::write(&self.inner, addr, bytes)
    }

    /// The `VirtualQueryEx` walk from 0 to the 32-bit ceiling `0x7FFF_FFFF`.
    pub fn regions(&self) -> Result<Vec<RegionInfo>, BridgeError> {
        walk_regions(|a| Platform::query(&self.inner, a))
    }

    /// True once the target has exited (`GetExitCodeProcess`).
    pub fn has_exited(&self) -> bool {
        Platform::exited(&self.inner)
    }

    pub fn pid(&self) -> u32 {
        self.pid
    }

    /// One `VirtualQueryEx` (the walk's step), for tests.
    #[cfg(test)]
    pub(crate) fn query_region(&self, addr: u32) -> Result<Option<RegionInfo>, BridgeError> {
        Platform::query(&self.inner, addr)
    }
}

impl GuestMemory for ProcessAttach {
    fn read(&self, addr: u32, buf: &mut [u8]) -> Result<(), BridgeError> {
        ProcessAttach::read(self, addr, buf)
    }

    fn write(&self, addr: u32, data: &[u8]) -> Result<(), BridgeError> {
        ProcessAttach::write(self, addr, data)
    }

    fn regions(&self) -> Result<Vec<RegionInfo>, BridgeError> {
        ProcessAttach::regions(self)
    }

    fn has_exited(&self) -> bool {
        ProcessAttach::has_exited(self)
    }
}

/// The id of the current process (used by the CLI's self-test and by tests).
pub fn current_pid() -> u32 {
    std::process::id()
}

#[cfg(windows)]
mod imp {
    use core::ffi::c_void;
    use core::mem::{size_of, MaybeUninit};
    use core::ptr;

    use windows_sys::Win32::Foundation::{
        CloseHandle, GetLastError, ERROR_ACCESS_DENIED, ERROR_INVALID_PARAMETER, HANDLE, STILL_ACTIVE,
    };
    use windows_sys::Win32::System::Diagnostics::Debug::{ReadProcessMemory, WriteProcessMemory};
    use windows_sys::Win32::System::Memory::{
        VirtualQueryEx, MEMORY_BASIC_INFORMATION, MEM_COMMIT, MEM_PRIVATE, PAGE_EXECUTE_READWRITE,
        PAGE_EXECUTE_WRITECOPY, PAGE_GUARD, PAGE_NOACCESS, PAGE_READWRITE, PAGE_WRITECOPY,
    };
    use windows_sys::Win32::System::Threading::{
        GetExitCodeProcess, OpenProcess, PROCESS_ACCESS_RIGHTS, PROCESS_QUERY_INFORMATION, PROCESS_VM_OPERATION,
        PROCESS_VM_READ, PROCESS_VM_WRITE,
    };

    use super::Platform;
    use crate::error::BridgeError;
    use crate::memory::{checked_end, RegionInfo};

    pub(super) struct Process {
        handle: HANDLE,
    }

    // SAFETY: a process handle is a kernel object reference valid on any thread; every call through it is a
    // separate system call that takes no Rust-side lock, and the handle is closed exactly once, on drop.
    unsafe impl Send for Process {}
    // SAFETY: as above; `ReadProcessMemory`/`WriteProcessMemory`/`VirtualQueryEx` are thread-safe.
    unsafe impl Sync for Process {}

    fn last_error() -> u32 {
        // SAFETY: GetLastError has no preconditions.
        unsafe { GetLastError() }
    }

    fn guest_ptr(addr: u32) -> *const c_void {
        ptr::without_provenance(addr as usize)
    }

    impl Process {
        pub(super) fn open_with(pid: u32, rights: PROCESS_ACCESS_RIGHTS) -> Result<Self, BridgeError> {
            // SAFETY: OpenProcess takes plain values and returns a handle or null; no pointer is passed.
            let handle = unsafe { OpenProcess(rights, 0, pid) };
            if handle.is_null() {
                return Err(match last_error() {
                    ERROR_INVALID_PARAMETER => BridgeError::ProcessExited,
                    ERROR_ACCESS_DENIED => BridgeError::AccessDenied { os_code: ERROR_ACCESS_DENIED },
                    code => BridgeError::QueryFailed { addr: 0, os_code: code },
                });
            }
            Ok(Self { handle })
        }

        /// A failure is reported as the process's exit when it has exited, else as `fallback`.
        fn classify(&self, os_code: u32, fallback: BridgeError) -> BridgeError {
            if Platform::exited(self) {
                BridgeError::ProcessExited
            } else if os_code == ERROR_ACCESS_DENIED {
                BridgeError::AccessDenied { os_code }
            } else {
                fallback
            }
        }
    }

    impl Platform for Process {
        fn open(pid: u32) -> Result<Self, BridgeError> {
            Self::open_with(pid, PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE)
        }

        fn read(&self, addr: u32, buf: &mut [u8]) -> Result<(), BridgeError> {
            checked_end(addr, buf.len())?;
            if buf.is_empty() {
                return Ok(());
            }
            let mut got: usize = 0;
            // SAFETY: `buf` is a live, exclusively borrowed buffer of exactly `buf.len()` bytes; the kernel
            // writes at most that many bytes into it and stores the count in `got`, a live local.
            let ok = unsafe {
                ReadProcessMemory(self.handle, guest_ptr(addr), buf.as_mut_ptr().cast(), buf.len(), &mut got)
            };
            if ok != 0 && got == buf.len() {
                return Ok(());
            }
            let code = if ok == 0 { last_error() } else { 0 };
            Err(self.classify(code, BridgeError::ShortRead { addr, want: buf.len(), got }))
        }

        fn write(&self, addr: u32, data: &[u8]) -> Result<(), BridgeError> {
            checked_end(addr, data.len())?;
            if data.is_empty() {
                return Ok(());
            }
            let mut written: usize = 0;
            // SAFETY: `data` is a live shared borrow of exactly `data.len()` bytes that the kernel only reads;
            // the count goes to `written`, a live local.
            let ok = unsafe {
                WriteProcessMemory(self.handle, guest_ptr(addr), data.as_ptr().cast(), data.len(), &mut written)
            };
            if ok != 0 && written == data.len() {
                return Ok(());
            }
            let code = if ok == 0 { last_error() } else { 0 };
            Err(self.classify(code, BridgeError::WriteFailed { addr }))
        }

        fn query(&self, addr: u32) -> Result<Option<RegionInfo>, BridgeError> {
            let mut info = MaybeUninit::<MEMORY_BASIC_INFORMATION>::zeroed();
            // SAFETY: `info` is a live, writable MEMORY_BASIC_INFORMATION and the length passed is its size.
            let n = unsafe {
                VirtualQueryEx(self.handle, guest_ptr(addr), info.as_mut_ptr(), size_of::<MEMORY_BASIC_INFORMATION>())
            };
            if n == 0 {
                let code = last_error();
                if code == ERROR_INVALID_PARAMETER && !Platform::exited(self) {
                    // Past the last mapping of the address space.
                    return Ok(None);
                }
                return Err(self.classify(code, BridgeError::QueryFailed { addr, os_code: code }));
            }
            // SAFETY: VirtualQueryEx returned non-zero, so it filled the structure; it was zeroed before.
            let info = unsafe { info.assume_init() };
            Ok(Some(RegionInfo {
                base: info.BaseAddress.addr() as u64,
                size: info.RegionSize as u64,
                readable_writable: readable_writable(&info),
            }))
        }

        fn exited(&self) -> bool {
            let mut code: u32 = 0;
            // SAFETY: `code` is a live local the call writes one u32 into.
            let ok = unsafe { GetExitCodeProcess(self.handle, &mut code) };
            ok != 0 && code != STILL_ACTIVE as u32
        }
    }

    /// `NativeProcessMemory.cs` `IsReadableWritable`: committed, private, not guard, not no-access, and a
    /// writable protection.
    fn readable_writable(info: &MEMORY_BASIC_INFORMATION) -> bool {
        if info.State & MEM_COMMIT == 0
            || info.Type & MEM_PRIVATE == 0
            || info.Protect & PAGE_GUARD != 0
            || info.Protect & PAGE_NOACCESS != 0
        {
            return false;
        }
        let protect = info.Protect & 0xFF;
        protect == PAGE_READWRITE
            || protect == PAGE_WRITECOPY
            || protect == PAGE_EXECUTE_READWRITE
            || protect == PAGE_EXECUTE_WRITECOPY
    }

    impl Drop for Process {
        fn drop(&mut self) {
            // SAFETY: `handle` came from OpenProcess, is non-null, and is closed exactly once here.
            unsafe {
                CloseHandle(self.handle);
            }
        }
    }

    #[cfg(test)]
    pub(crate) fn open_query_only(pid: u32) -> Result<super::ProcessAttach, BridgeError> {
        use windows_sys::Win32::System::Threading::PROCESS_QUERY_LIMITED_INFORMATION;
        Ok(super::ProcessAttach { inner: Process::open_with(pid, PROCESS_QUERY_LIMITED_INFORMATION)?, pid })
    }

    /// Test-only: a region of this process placed below 2 GiB, so its address is a u32 guest address.
    #[cfg(test)]
    pub(crate) mod low {
        use core::ffi::c_void;
        use core::ptr;

        use windows_sys::Win32::System::Memory::{
            VirtualAlloc, VirtualFree, MEM_COMMIT, MEM_RELEASE, MEM_RESERVE, PAGE_READWRITE,
        };

        pub(crate) struct LowRegion {
            base: *mut c_void,
            committed: usize,
        }

        impl LowRegion {
            /// Reserve `reserve` bytes below 2 GiB and commit the first `commit` bytes read/write.
            pub(crate) fn new(reserve: usize, commit: usize) -> Self {
                assert!(commit <= reserve);
                let mut hint: usize = 0x1000_0000;
                while hint < 0x7000_0000 {
                    // SAFETY: VirtualAlloc with a hint address either reserves fresh pages there or returns null.
                    let base = unsafe { VirtualAlloc(ptr::without_provenance(hint), reserve, MEM_RESERVE, PAGE_READWRITE) };
                    if !base.is_null() {
                        if commit > 0 {
                            // SAFETY: commits the first `commit` bytes of the reservation just made.
                            let c = unsafe { VirtualAlloc(base, commit, MEM_COMMIT, PAGE_READWRITE) };
                            assert!(!c.is_null(), "commit failed");
                        }
                        return Self { base, committed: commit };
                    }
                    hint += 0x0100_0000;
                }
                panic!("no free low address range for a test region");
            }

            pub(crate) fn addr(&self) -> u32 {
                u32::try_from(self.base.addr()).expect("low region below 4 GiB")
            }

            pub(crate) fn poke(&self, offset: usize, data: &[u8]) {
                assert!(offset + data.len() <= self.committed);
                // SAFETY: the destination lies inside the committed read/write part of this live reservation.
                unsafe { ptr::copy_nonoverlapping(data.as_ptr(), self.base.cast::<u8>().add(offset), data.len()) }
            }

            pub(crate) fn peek(&self, offset: usize, len: usize) -> Vec<u8> {
                assert!(offset + len <= self.committed);
                let mut out = vec![0u8; len];
                // SAFETY: the source lies inside the committed part of this live reservation.
                unsafe { ptr::copy_nonoverlapping(self.base.cast::<u8>().add(offset), out.as_mut_ptr(), len) }
                out
            }
        }

        impl Drop for LowRegion {
            fn drop(&mut self) {
                // SAFETY: `base` is the start of a reservation this value owns; MEM_RELEASE with size 0 frees it.
                unsafe {
                    VirtualFree(self.base, 0, MEM_RELEASE);
                }
            }
        }
    }

}

#[cfg(not(windows))]
mod imp {
    use super::Platform;
    use crate::error::BridgeError;
    use crate::memory::RegionInfo;

    /// No platform implementation outside Windows in this wave (§G row 9: Wine `process_vm_readv`, `DEFER`).
    pub(super) struct Process;

    impl Platform for Process {
        fn open(_pid: u32) -> Result<Self, BridgeError> {
            Err(BridgeError::Unsupported)
        }
        fn read(&self, _addr: u32, _buf: &mut [u8]) -> Result<(), BridgeError> {
            Err(BridgeError::Unsupported)
        }
        fn write(&self, _addr: u32, _data: &[u8]) -> Result<(), BridgeError> {
            Err(BridgeError::Unsupported)
        }
        fn query(&self, _addr: u32) -> Result<Option<RegionInfo>, BridgeError> {
            Err(BridgeError::Unsupported)
        }
        fn exited(&self) -> bool {
            true
        }
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::imp::low::LowRegion;
    use super::imp::open_query_only;
    use super::*;
    use crate::layout::{
        bulk_region_base_offset, BulkRegionHeader, ControlRegionHeader, TOTAL_REGION_SIZE,
    };

    const PAGE: usize = 0x1000;

    fn me() -> ProcessAttach {
        ProcessAttach::open(current_pid()).expect("open own process")
    }

    // ---- open ----
    #[test]
    fn open_own_process_reports_its_pid_and_is_alive() {
        let pm = me();
        assert_eq!(pm.pid(), current_pid());
        assert!(!pm.has_exited());
    }

    #[test]
    fn open_missing_process_is_process_exited() {
        assert!(matches!(ProcessAttach::open(0), Err(BridgeError::ProcessExited)));
    }

    // ---- read ----
    #[test]
    fn read_returns_the_bytes_at_the_address() {
        let r = LowRegion::new(PAGE, PAGE);
        let pattern: Vec<u8> = (0..64u8).collect();
        r.poke(16, &pattern);
        let pm = me();
        let mut buf = [0u8; 64];
        pm.read(r.addr() + 16, &mut buf).unwrap();
        assert_eq!(&buf[..], &pattern[..]);
        // A wrong-address wiring (base instead of base+16) would read the zeros before the pattern.
        let mut first = [0xEEu8; 4];
        pm.read(r.addr() + 17, &mut first).unwrap();
        assert_eq!(first, [1, 2, 3, 4]);
    }

    #[test]
    fn read_across_the_commit_boundary_is_a_short_read() {
        let r = LowRegion::new(2 * PAGE, PAGE);
        let pm = me();
        let mut buf = [0xAAu8; 8];
        let err = pm.read(r.addr() + PAGE as u32 - 4, &mut buf).unwrap_err();
        assert!(matches!(err, BridgeError::ShortRead { want: 8, got, .. } if got < 8), "{err:?}");
    }

    #[test]
    fn read_of_an_uncommitted_page_is_a_short_read_of_zero() {
        let r = LowRegion::new(2 * PAGE, PAGE);
        let pm = me();
        let mut buf = [0u8; 4];
        let err = pm.read(r.addr() + PAGE as u32, &mut buf).unwrap_err();
        assert_eq!(err, BridgeError::ShortRead { addr: r.addr() + PAGE as u32, want: 4, got: 0 });
    }

    #[test]
    fn read_through_a_query_only_handle_is_access_denied() {
        let r = LowRegion::new(PAGE, PAGE);
        let pm = open_query_only(current_pid()).unwrap();
        let mut buf = [0u8; 4];
        assert!(matches!(pm.read(r.addr(), &mut buf), Err(BridgeError::AccessDenied { .. })));
    }

    // ---- write ----
    #[test]
    fn write_lands_at_the_address_and_nowhere_else() {
        let r = LowRegion::new(PAGE, PAGE);
        let pm = me();
        pm.write(r.addr() + 100, &[9, 8, 7]).unwrap();
        assert_eq!(r.peek(99, 5), vec![0, 9, 8, 7, 0]);
    }

    #[test]
    fn write_to_an_uncommitted_page_fails_typed() {
        let r = LowRegion::new(2 * PAGE, PAGE);
        let pm = me();
        let err = pm.write(r.addr() + PAGE as u32, &[1, 2, 3, 4]).unwrap_err();
        assert_eq!(err, BridgeError::WriteFailed { addr: r.addr() + PAGE as u32 });
    }

    #[test]
    fn write_through_a_query_only_handle_is_access_denied() {
        let r = LowRegion::new(PAGE, PAGE);
        let pm = open_query_only(current_pid()).unwrap();
        assert!(matches!(pm.write(r.addr(), &[1]), Err(BridgeError::AccessDenied { .. })));
        assert_eq!(r.peek(0, 1), vec![0]);
    }

    #[test]
    fn address_overflow_is_typed_before_any_call() {
        let pm = me();
        let mut buf = [0u8; 8];
        assert!(matches!(pm.read(u32::MAX - 3, &mut buf), Err(BridgeError::AddressOverflow { .. })));
        assert!(matches!(pm.write(u32::MAX - 3, &buf), Err(BridgeError::AddressOverflow { .. })));
    }

    // ---- query_region ----
    #[test]
    fn query_describes_the_committed_and_the_reserved_part() {
        let r = LowRegion::new(2 * PAGE, PAGE);
        let pm = me();
        let q = pm.query_region(r.addr()).unwrap().unwrap();
        assert_eq!(q.base, u64::from(r.addr()));
        assert_eq!(q.size, PAGE as u64);
        assert!(q.readable_writable);
        let q2 = pm.query_region(r.addr() + PAGE as u32).unwrap().unwrap();
        assert_eq!(q2.base, u64::from(r.addr()) + PAGE as u64);
        assert!(!q2.readable_writable);
    }

    // ---- has_exited, and every call after exit ----
    #[test]
    fn exited_child_is_process_exited_on_every_entry_point() {
        let mut child = std::process::Command::new("cmd").args(["/C", "exit 3"]).spawn().expect("spawn cmd");
        let pm = ProcessAttach::open(child.id()).expect("open child while its handle is held");
        let status = child.wait().expect("wait child");
        assert_eq!(status.code(), Some(3));
        assert!(pm.has_exited());
        let mut buf = [0u8; 4];
        assert_eq!(pm.read(0x0040_0000, &mut buf), Err(BridgeError::ProcessExited));
        assert_eq!(pm.write(0x0040_0000, &buf), Err(BridgeError::ProcessExited));
        assert_eq!(pm.query_region(0x0040_0000), Err(BridgeError::ProcessExited));
        assert_eq!(pm.regions(), Err(BridgeError::ProcessExited));
    }

    // ---- the whole stack: discovery through the FFI on this process ----
    #[test]
    fn discovery_finds_a_planted_v11_region_in_this_process() {
        let r = LowRegion::new(TOTAL_REGION_SIZE, TOTAL_REGION_SIZE);
        r.poke(0, &ControlRegionHeader::v11().to_bytes());
        r.poke(bulk_region_base_offset(), &BulkRegionHeader::v11().to_bytes());
        let pm = me();
        let found = crate::discovery::find_region_once(&pm).expect("no refusal");
        assert_eq!(found, Some(r.addr()));
        let walked = pm.regions().unwrap();
        assert!(walked.iter().any(|q| q.base == u64::from(r.addr()) && q.readable_writable));
        assert!(walked.iter().all(|q| q.base <= crate::memory::SCAN_CEILING));
    }
}


// ---- the SimLink section: the live backing of `ring::Section` -------------------------------------------------
//
// Oracle: the P/Invoke half of `DedicatedServer/HostBridge/SimLinkShadowReader.cs`. Every shared byte is read with
// an atomic load, an atomic store or a volatile byte copy: no `&[u8]` or `&T` into the mapping is ever built, so
// the producer in another process can write concurrently without Rust aliasing UB.
//
// The raw byte-offset primitives are private to `native::Mapping`. The only way in is a fixed v1 header slot
// (`Slot32` / `Slot64`, each with one width, none overlapping) or `copy_ring`, which refuses the header bytes. So
// header bytes are only ever touched atomically at their one width and ring bytes only by volatile copies: safe
// callers cannot create mixed-width or atomic/non-atomic accesses to the same bytes, even through `&self`.

use crate::ring::{RingError, Section};

/// The 4-byte header slots (`ring::off`), each read at exactly this width.
#[derive(Clone, Copy)]
enum Slot32 {
    Version,
    HeaderSize,
    Pid,
    State,
    Error,
    RingOffset,
    RingSize,
    NativeVersion,
    ProbeResult,
}

/// The 8-byte header slots (`ring::off`), each read at exactly this width. `Magic` is the 8 magic bytes.
#[derive(Clone, Copy)]
enum Slot64 {
    Magic,
    QpcFrequency,
    WritePos,
    ReadPos,
    Published,
    Dropped,
    Contended,
    BadArgs,
}

/// The id of this process's Windows session (`ProcessIdToSessionId`), 1 when it cannot be read. The Sandboxie
/// object directory of a boxed TS3 is keyed by it.
pub fn own_session_id() -> u32 {
    native::own_session_id()
}

/// An open SimLink section and its `.control` / `.data` events (either event may be absent). The mapping and
/// handles are released on drop. Windows only: elsewhere every `open_*` is `RingError::NoSection`.
pub struct NativeSection {
    inner: native::Mapping,
}

impl NativeSection {
    /// `OpenFileMappingW` + `OpenEventW` by Win32 name, e.g. `Local\SimsOnline.SimLink.1234`.
    pub fn open_by_name(win32_name: &str) -> Result<Self, RingError> {
        native::Mapping::open_win32(win32_name).map(|inner| Self { inner })
    }

    /// `NtOpenSection` + `NtOpenEvent` by NT path: `directory` (with its trailing backslash, see
    /// `ring::sandboxie_directory`) followed by `name`.
    pub fn open_nt(directory: &str, name: &str) -> Result<Self, RingError> {
        native::Mapping::open_nt(directory, name).map(|inner| Self { inner })
    }
}

impl Section for NativeSection {
    fn len(&self) -> usize {
        self.inner.len()
    }
    fn magic(&self) -> [u8; 8] {
        self.inner.slot64(Slot64::Magic).to_le_bytes()
    }
    fn version(&self) -> u32 {
        self.inner.slot32(Slot32::Version)
    }
    fn header_size(&self) -> u32 {
        self.inner.slot32(Slot32::HeaderSize)
    }
    fn pid(&self) -> u32 {
        self.inner.slot32(Slot32::Pid)
    }
    fn state(&self) -> u32 {
        self.inner.slot32(Slot32::State)
    }
    fn set_state(&self, state: u32) {
        self.inner.set_state(state);
    }
    fn error(&self) -> u32 {
        self.inner.slot32(Slot32::Error)
    }
    fn ring_offset(&self) -> u32 {
        self.inner.slot32(Slot32::RingOffset)
    }
    fn ring_size(&self) -> u32 {
        self.inner.slot32(Slot32::RingSize)
    }
    fn qpc_frequency(&self) -> u64 {
        self.inner.slot64(Slot64::QpcFrequency)
    }
    fn write_pos(&self) -> u64 {
        self.inner.slot64(Slot64::WritePos)
    }
    fn read_pos(&self) -> u64 {
        self.inner.slot64(Slot64::ReadPos)
    }
    fn set_read_pos(&self, pos: u64) {
        self.inner.set_read_pos(pos);
    }
    fn published(&self) -> u64 {
        self.inner.slot64(Slot64::Published)
    }
    fn dropped(&self) -> u64 {
        self.inner.slot64(Slot64::Dropped)
    }
    fn contended(&self) -> u64 {
        self.inner.slot64(Slot64::Contended)
    }
    fn bad_args(&self) -> u64 {
        self.inner.slot64(Slot64::BadArgs)
    }
    fn native_version(&self) -> u32 {
        self.inner.slot32(Slot32::NativeVersion)
    }
    fn probe_result(&self) -> u32 {
        self.inner.slot32(Slot32::ProbeResult)
    }
    fn copy_ring(&self, offset: usize, out: &mut [u8]) {
        self.inner.copy_ring(offset, out);
    }
    fn has_control(&self) -> bool {
        self.inner.has_control()
    }
    fn has_data(&self) -> bool {
        self.inner.has_data()
    }
    fn signal_control(&self) {
        self.inner.signal_control();
    }
    fn wait_data(&self, ms: u32) -> bool {
        self.inner.wait_data(ms)
    }
}

#[cfg(windows)]
mod native {
    use core::ffi::c_void;
    use core::mem::{size_of, MaybeUninit};
    use core::ptr;
    use core::sync::atomic::{AtomicU32, AtomicU64, Ordering};

    use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, HANDLE};
    use windows_sys::Win32::System::Memory::{
        MapViewOfFile, OpenFileMappingW, UnmapViewOfFile, VirtualQuery, MEMORY_BASIC_INFORMATION,
        MEMORY_MAPPED_VIEW_ADDRESS,
    };
    use windows_sys::Win32::System::RemoteDesktop::ProcessIdToSessionId;
    use windows_sys::Win32::System::Threading::{OpenEventW, SetEvent, WaitForSingleObject};

    use super::{Slot32, Slot64};
    use crate::ring::{off, RingError, HEADER_BYTES};

    const MEM_COMMIT: u32 = 0x1000;
    const PAGE_NOACCESS: u32 = 0x01;
    const PAGE_READWRITE: u32 = 0x04;
    const PAGE_EXECUTE_READWRITE: u32 = 0x40;
    const PAGE_GUARD: u32 = 0x100;
    const FILE_MAP_WRITE: u32 = 0x0002;
    const FILE_MAP_READ: u32 = 0x0004;
    const SECTION_MAP_WRITE: u32 = 0x0002;
    const SECTION_MAP_READ: u32 = 0x0004;
    const EVENT_MODIFY_STATE: u32 = 0x0002;
    const SYNCHRONIZE: u32 = 0x0010_0000;
    const OBJ_CASE_INSENSITIVE: u32 = 0x40;
    const STATUS_INVALID_PARAMETER: i32 = 0xC000_000Du32 as i32;

    /// `UNICODE_STRING`.
    #[repr(C)]
    struct UnicodeString {
        length: u16,
        maximum_length: u16,
        buffer: *mut u16,
    }

    /// `OBJECT_ATTRIBUTES`.
    #[repr(C)]
    struct ObjectAttributes {
        length: u32,
        root_directory: HANDLE,
        object_name: *mut UnicodeString,
        attributes: u32,
        security_descriptor: *mut c_void,
        security_quality_of_service: *mut c_void,
    }

    #[link(name = "ntdll", kind = "raw-dylib")]
    extern "system" {
        fn NtOpenSection(handle: *mut HANDLE, access: u32, attributes: *const ObjectAttributes) -> i32;
        fn NtOpenEvent(handle: *mut HANDLE, access: u32, attributes: *const ObjectAttributes) -> i32;
    }

    fn last_error() -> u32 {
        // SAFETY: GetLastError has no preconditions.
        unsafe { GetLastError() }
    }

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(Some(0)).collect()
    }

    fn close_all(handles: [HANDLE; 3]) {
        for handle in handles {
            if !handle.is_null() {
                // SAFETY: each non-null handle came from an open call here and is closed exactly once.
                unsafe {
                    CloseHandle(handle);
                }
            }
        }
    }

    fn open_event(name: &str) -> HANDLE {
        let wide_name = wide(name);
        // SAFETY: `wide_name` is a live, NUL-terminated UTF-16 buffer; the call returns a handle or null.
        unsafe { OpenEventW(EVENT_MODIFY_STATE | SYNCHRONIZE, 0, wide_name.as_ptr()) }
    }

    /// `NtOpenSection` / `NtOpenEvent` of `path` (an NT object path). `Err` carries the NTSTATUS.
    fn nt_open(path: &str, section: bool, access: u32) -> Result<HANDLE, i32> {
        let mut text = wide(path);
        let bytes = (text.len() - 1) * 2;
        if bytes + 2 > usize::from(u16::MAX) {
            return Err(STATUS_INVALID_PARAMETER);
        }
        let mut name = UnicodeString { length: bytes as u16, maximum_length: (bytes + 2) as u16, buffer: text.as_mut_ptr() };
        let attributes = ObjectAttributes {
            length: size_of::<ObjectAttributes>() as u32,
            root_directory: ptr::null_mut(),
            object_name: &mut name,
            attributes: OBJ_CASE_INSENSITIVE,
            security_descriptor: ptr::null_mut(),
            security_quality_of_service: ptr::null_mut(),
        };
        let mut handle: HANDLE = ptr::null_mut();
        // SAFETY: `name` and `text` outlive the call and describe a NUL-terminated buffer of `maximum_length`
        // bytes; `attributes` is a fully initialised OBJECT_ATTRIBUTES; `handle` is a live out-parameter.
        let status = unsafe {
            if section {
                NtOpenSection(&mut handle, access, &attributes)
            } else {
                NtOpenEvent(&mut handle, access, &attributes)
            }
        };
        if status == 0 && !handle.is_null() {
            Ok(handle)
        } else {
            Err(if status == 0 { STATUS_INVALID_PARAMETER } else { status })
        }
    }

    pub(super) fn own_session_id() -> u32 {
        let mut session = 0u32;
        // SAFETY: `session` is a live local the call writes one u32 into.
        let ok = unsafe { ProcessIdToSessionId(std::process::id(), &mut session) };
        if ok != 0 {
            session
        } else {
            1
        }
    }

    pub(super) struct Mapping {
        section: HANDLE,
        view: *mut u8,
        len: usize,
        control: HANDLE,
        data: HANDLE,
    }

    // SAFETY: the handles are kernel object references valid on any thread, the view stays mapped until drop, and
    // every access to it is an atomic or a volatile byte copy, so concurrent use from several threads is sound.
    unsafe impl Send for Mapping {}
    // SAFETY: as above; all `&self` methods only perform atomic or volatile accesses and thread-safe system calls.
    unsafe impl Sync for Mapping {}

    impl Mapping {
        pub(super) fn open_win32(name: &str) -> Result<Self, RingError> {
            let wide_name = wide(name);
            // SAFETY: `wide_name` is a live, NUL-terminated UTF-16 buffer; the call returns a handle or null.
            let section = unsafe { OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, 0, wide_name.as_ptr()) };
            if section.is_null() {
                return Err(RingError::NoSection(format!("no section {name} ({})", last_error())));
            }
            Self::map(section, open_event(&format!("{name}.control")), open_event(&format!("{name}.data")))
        }

        pub(super) fn open_nt(directory: &str, name: &str) -> Result<Self, RingError> {
            let path = format!("{directory}{name}");
            let section = nt_open(&path, true, SECTION_MAP_READ | SECTION_MAP_WRITE)
                .map_err(|status| RingError::NoSection(format!("no section at {path} (0x{status:08X})")))?;
            let event = |suffix: &str| {
                nt_open(&format!("{path}{suffix}"), false, EVENT_MODIFY_STATE | SYNCHRONIZE).unwrap_or(ptr::null_mut())
            };
            Self::map(section, event(".control"), event(".data"))
        }

        fn map(section: HANDLE, control: HANDLE, data: HANDLE) -> Result<Self, RingError> {
            // SAFETY: `section` is a valid section handle; offset 0 and length 0 map the whole section.
            let view = unsafe { MapViewOfFile(section, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0) };
            let base = view.Value.cast::<u8>();
            if base.is_null() {
                let code = last_error();
                close_all([section, control, data]);
                return Err(RingError::NoSection(format!("MapViewOfFile failed ({code})")));
            }
            let mut info = MaybeUninit::<MEMORY_BASIC_INFORMATION>::zeroed();
            // SAFETY: `base` is the start of a view just mapped; `info` is a live, writable
            // MEMORY_BASIC_INFORMATION and the length passed is its size.
            let written = unsafe { VirtualQuery(base.cast_const().cast(), info.as_mut_ptr(), size_of::<MEMORY_BASIC_INFORMATION>()) };
            // SAFETY: VirtualQuery returned non-zero, so it filled the structure; it was zeroed before.
            let info = if written == 0 { None } else { Some(unsafe { info.assume_init() }) };
            let len = info.as_ref().map_or(0, |info| info.RegionSize);
            // The view is read and written, so its first region must be committed, read-write and not guarded:
            // a `SEC_RESERVE` section or a guard/no-access page would fault on the first header read.
            let accessible = info.as_ref().is_some_and(|info| {
                info.State == MEM_COMMIT
                    && info.Protect & (PAGE_GUARD | PAGE_NOACCESS) == 0
                    && info.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE) != 0
            });
            if len != 0 && !accessible {
                let (state, protect) = info.as_ref().map_or((0, 0), |info| (info.State, info.Protect));
                // SAFETY: `base` is the view returned by MapViewOfFile above, unmapped exactly once here.
                unsafe {
                    UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: base.cast() });
                }
                close_all([section, control, data]);
                return Err(RingError::BadHeader(format!(
                    "mapping is not committed read-write memory (state 0x{state:X}, protect 0x{protect:X})"
                )));
            }
            if len == 0 {
                // SAFETY: `base` is the view returned by MapViewOfFile above, unmapped exactly once here.
                unsafe {
                    UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: base.cast() });
                }
                close_all([section, control, data]);
                return Err(RingError::NoSection("mapped length unknown".into()));
            }
            Ok(Self { section, view: base, len, control, data })
        }

        pub(super) fn len(&self) -> usize {
            self.len
        }

        fn offset32(slot: Slot32) -> usize {
            match slot {
                Slot32::Version => off::LAYOUT_VERSION,
                Slot32::HeaderSize => off::HEADER_SIZE,
                Slot32::Pid => off::PID,
                Slot32::State => off::STATE,
                Slot32::Error => off::ERROR,
                Slot32::RingOffset => off::RING_OFFSET,
                Slot32::RingSize => off::RING_SIZE,
                Slot32::NativeVersion => off::NATIVE_VERSION,
                Slot32::ProbeResult => off::PROBE_RESULT,
            }
        }

        fn offset64(slot: Slot64) -> usize {
            match slot {
                Slot64::Magic => off::MAGIC,
                Slot64::QpcFrequency => off::QPC_FREQUENCY,
                Slot64::WritePos => off::WRITE_POS,
                Slot64::ReadPos => off::READ_POS,
                Slot64::Published => off::PUBLISHED,
                Slot64::Dropped => off::DROPPED,
                Slot64::Contended => off::CONTENDED,
                Slot64::BadArgs => off::BAD_ARGS,
            }
        }

        /// The atomic cell of a 4-byte header slot, `None` when the mapping is too short to hold it. Every
        /// `Slot32` offset is 4-aligned and inside the first `HEADER_BYTES` bytes, so it never overlaps a `Slot64`.
        fn cell32(&self, slot: Slot32) -> Option<&AtomicU32> {
            let at = Self::offset32(slot);
            if !at.is_multiple_of(4) || at.checked_add(4).is_none_or(|end| end > self.len) {
                return None;
            }
            // SAFETY: the 4 bytes are inside the live mapping and 4-aligned (the view is page-aligned and `at` is
            // a multiple of 4); the cell lives no longer than `&self`, which keeps the view mapped. Header bytes are
            // only ever accessed through atomics of their slot's single width, so no access conflicts with it.
            Some(unsafe { AtomicU32::from_ptr(self.view.add(at).cast()) })
        }

        /// As `cell32`, for an 8-byte slot at an 8-aligned offset.
        fn cell64(&self, slot: Slot64) -> Option<&AtomicU64> {
            let at = Self::offset64(slot);
            if !at.is_multiple_of(8) || at.checked_add(8).is_none_or(|end| end > self.len) {
                return None;
            }
            // SAFETY: as `cell32`, for 8 bytes at an 8-aligned offset.
            Some(unsafe { AtomicU64::from_ptr(self.view.add(at).cast()) })
        }

        pub(super) fn slot32(&self, slot: Slot32) -> u32 {
            self.cell32(slot).map_or(0, |cell| cell.load(Ordering::Acquire))
        }

        pub(super) fn slot64(&self, slot: Slot64) -> u64 {
            self.cell64(slot).map_or(0, |cell| cell.load(Ordering::Acquire))
        }

        pub(super) fn set_state(&self, state: u32) {
            if let Some(cell) = self.cell32(Slot32::State) {
                cell.store(state, Ordering::Release);
            }
        }

        pub(super) fn set_read_pos(&self, pos: u64) {
            if let Some(cell) = self.cell64(Slot64::ReadPos) {
                cell.store(pos, Ordering::Release);
            }
        }

        /// Volatile byte copy of ring bytes. A range starting inside the header, or leaving the mapping, yields
        /// zeros without touching memory: header bytes are reachable only through their atomic slots.
        pub(super) fn copy_ring(&self, offset: usize, out: &mut [u8]) {
            if offset < HEADER_BYTES as usize || offset.checked_add(out.len()).is_none_or(|end| end > self.len) {
                out.fill(0);
                return;
            }
            for (index, byte) in out.iter_mut().enumerate() {
                // SAFETY: `offset + index` is inside the live mapping and past the header (checked above); a
                // volatile single-byte read never forms a reference and cannot be torn.
                *byte = unsafe { ptr::read_volatile(self.view.add(offset + index)) };
            }
        }

        pub(super) fn has_control(&self) -> bool {
            !self.control.is_null()
        }

        pub(super) fn has_data(&self) -> bool {
            !self.data.is_null()
        }

        pub(super) fn signal_control(&self) {
            if !self.control.is_null() {
                // SAFETY: `control` is a valid event handle opened with EVENT_MODIFY_STATE, closed only on drop.
                unsafe {
                    SetEvent(self.control);
                }
            }
        }

        pub(super) fn wait_data(&self, ms: u32) -> bool {
            if self.data.is_null() {
                return false;
            }
            // SAFETY: `data` is a valid event handle opened with SYNCHRONIZE, closed only on drop.
            unsafe { WaitForSingleObject(self.data, ms) == 0 }
        }
    }

    impl Drop for Mapping {
        fn drop(&mut self) {
            // SAFETY: `view` is the address MapViewOfFile returned for this mapping, unmapped exactly once here.
            unsafe {
                UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: self.view.cast() });
            }
            close_all([self.section, self.control, self.data]);
        }
    }
}

#[cfg(not(windows))]
mod native {
    use super::{Slot32, Slot64};
    use crate::ring::RingError;

    pub(super) fn own_session_id() -> u32 {
        1
    }

    pub(super) struct Mapping;

    impl Mapping {
        pub(super) fn open_win32(_name: &str) -> Result<Self, RingError> {
            Err(RingError::NoSection("SimLink sections are Windows-only".into()))
        }
        pub(super) fn open_nt(_directory: &str, _name: &str) -> Result<Self, RingError> {
            Err(RingError::NoSection("SimLink sections are Windows-only".into()))
        }
        pub(super) fn len(&self) -> usize {
            0
        }
        pub(super) fn slot32(&self, _slot: Slot32) -> u32 {
            0
        }
        pub(super) fn slot64(&self, _slot: Slot64) -> u64 {
            0
        }
        pub(super) fn set_state(&self, _state: u32) {}
        pub(super) fn set_read_pos(&self, _pos: u64) {}
        pub(super) fn copy_ring(&self, _offset: usize, out: &mut [u8]) {
            out.fill(0);
        }
        pub(super) fn has_control(&self) -> bool {
            false
        }
        pub(super) fn has_data(&self) -> bool {
            false
        }
        pub(super) fn signal_control(&self) {}
        pub(super) fn wait_data(&self, _ms: u32) -> bool {
            false
        }
    }
}

/// The Windows round trip: a real section and two real events created by a test "producer" with an isolated name,
/// read through `NativeSection` (no game needed, parallel-safe because every name is unique).
#[cfg(all(test, windows))]
mod native_ring_tests {
    use core::ptr;
    use core::sync::atomic::{AtomicU32, AtomicU64, Ordering};
    use std::sync::atomic::AtomicUsize;
    use std::sync::Arc;
    use std::time::Duration;

    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Memory::{
        CreateFileMappingW, MapViewOfFile, UnmapViewOfFile, MEMORY_MAPPED_VIEW_ADDRESS, PAGE_READWRITE, SEC_RESERVE,
    };
    use windows_sys::Win32::System::Threading::{CreateEventW, SetEvent, WaitForSingleObject};

    use super::NativeSection;
    use crate::ring::{self, off, RingError, RingReader, Section};

    static NEXT: AtomicUsize = AtomicUsize::new(0);

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(Some(0)).collect()
    }

    /// The test-side owner of the section: creates it, writes like the native producer, answers registration.
    struct Owner {
        name: String,
        section: HANDLE,
        view: *mut u8,
        size: usize,
        control: HANDLE,
        data: HANDLE,
    }

    // SAFETY: handles are valid on any thread; the view is only accessed through atomics and a bounds-checked copy.
    unsafe impl Send for Owner {}
    // SAFETY: as above.
    unsafe impl Sync for Owner {}

    impl Owner {
        fn create(size: usize) -> Self {
            let unique = format!("SimsOnlineTest.SimLink.{}.{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst));
            let name = format!(r"Local\{unique}");
            let wide_name = wide(&name);
            // SAFETY: a pagefile-backed mapping of `size` bytes named by a live NUL-terminated buffer.
            let section = unsafe {
                CreateFileMappingW(INVALID_HANDLE_VALUE, ptr::null(), PAGE_READWRITE, 0, size as u32, wide_name.as_ptr())
            };
            assert!(!section.is_null(), "CreateFileMappingW");
            // SAFETY: `section` is the valid mapping just created; access 6 is FILE_MAP_READ | FILE_MAP_WRITE.
            let view = unsafe { MapViewOfFile(section, 0x0006, 0, 0, 0) }.Value.cast::<u8>();
            assert!(!view.is_null(), "MapViewOfFile");
            let event = |suffix: &str| {
                let event_name = wide(&format!("{name}{suffix}"));
                // SAFETY: an unnamed-security, auto-reset, initially clear event named by a live buffer.
                let handle = unsafe { CreateEventW(ptr::null(), 0, 0, event_name.as_ptr()) };
                assert!(!handle.is_null(), "CreateEventW");
                handle
            };
            let control = event(".control");
            let data = event(".data");
            Self { name, section, view, size, control, data }
        }

        fn poke(&self, off: usize, bytes: &[u8]) {
            assert!(off + bytes.len() <= self.size);
            // SAFETY: the destination range is inside the mapping (asserted) and cannot overlap `bytes`.
            unsafe { ptr::copy_nonoverlapping(bytes.as_ptr(), self.view.add(off), bytes.len()) };
        }

        fn store_u64(&self, off: usize, value: u64) {
            assert!(off.is_multiple_of(8) && off + 8 <= self.size);
            // SAFETY: in bounds and 8-aligned inside a page-aligned view.
            unsafe { AtomicU64::from_ptr(self.view.add(off).cast()).store(value, Ordering::Release) }
        }

        fn store_u32(&self, off: usize, value: u32) {
            assert!(off.is_multiple_of(4) && off + 4 <= self.size);
            // SAFETY: in bounds and 4-aligned inside a page-aligned view.
            unsafe { AtomicU32::from_ptr(self.view.add(off).cast()).store(value, Ordering::Release) }
        }

        fn load_u64(&self, off: usize) -> u64 {
            assert!(off.is_multiple_of(8) && off + 8 <= self.size);
            // SAFETY: in bounds and 8-aligned inside a page-aligned view.
            unsafe { AtomicU64::from_ptr(self.view.add(off).cast()).load(Ordering::Acquire) }
        }

        fn wait_control(&self, ms: u32) -> bool {
            // SAFETY: `control` is a valid event handle owned by `self`.
            unsafe { WaitForSingleObject(self.control, ms) == 0 }
        }

        fn signal_data(&self) {
            // SAFETY: `data` is a valid event handle owned by `self`.
            unsafe {
                SetEvent(self.data);
            }
        }

        fn init(&self, ring_size: u32) {
            self.poke(0, &ring::initial_header(ring_size, std::process::id()));
        }
    }

    impl Drop for Owner {
        fn drop(&mut self) {
            // SAFETY: the view and handles were created in `create` and are released exactly once here.
            unsafe {
                UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: self.view.cast() });
                CloseHandle(self.control);
                CloseHandle(self.data);
                CloseHandle(self.section);
            }
        }
    }

    fn reader(owner: &Owner) -> RingReader<NativeSection> {
        let section = NativeSection::open_by_name(&owner.name).expect("open the owner's section");
        RingReader::open(section, Some(std::process::id())).expect("valid header")
    }

    #[test]
    fn a_real_section_and_events_round_trip_registration_and_records() {
        let owner = Arc::new(Owner::create(4096 + 256));
        owner.init(4096);
        let mut ring = reader(&owner);
        assert_eq!(ring.ring_size(), 4096);
        assert_eq!(ring.state(), ring::STATE_CREATED);
        let answering = Arc::clone(&owner);
        let responder = std::thread::spawn(move || {
            assert!(answering.wait_control(5000), "the reader signalled .control");
            answering.store_u32(off::STATE, ring::STATE_REGISTERED);
        });
        assert!(ring.request_registration(Duration::from_secs(5)));
        responder.join().expect("responder thread");
        assert_eq!(ring.state(), ring::STATE_REGISTERED);

        let first = ring::encode_record(6, 1, 77, "page one");
        owner.poke(256, &first);
        owner.store_u64(off::PUBLISHED, 1);
        owner.store_u64(off::WRITE_POS, first.len() as u64);
        owner.signal_data();
        assert!(ring.wait_for_data(2000), "the .data event fired");
        assert!(!ring.wait_for_data(0), "auto-reset: the signal was consumed");
        let mut out = Vec::new();
        assert_eq!(ring.drain(&mut out).expect("drain"), 1);
        assert_eq!(out, vec![ring::Record { lane: 6, seq: 1, qpc: 77, units: "page one".encode_utf16().collect() }]);
        assert_eq!(owner.load_u64(off::READ_POS), first.len() as u64, "ReadPos advanced in the shared bytes");
        assert_eq!(ring.telemetry().published, 1);
        assert_eq!(ring.telemetry().qpc_frequency, 10_000_000);
    }

    #[test]
    fn a_real_ring_wraps_through_a_pad_that_only_has_its_head_written() {
        let owner = Owner::create(256 + 128);
        owner.init(128);
        let mut ring = reader(&owner);
        let mut out = Vec::new();
        let near = ring::encode_record(6, 1, 1, &"a".repeat(36));
        assert_eq!(near.len(), 96);
        owner.poke(256, &near);
        owner.store_u64(off::WRITE_POS, 96);
        assert_eq!(ring.drain(&mut out).expect("first"), 1);
        let wrapped = ring::encode_record(6, 2, 2, &"b".repeat(16));
        assert_eq!(wrapped.len(), 56);
        owner.poke(256 + 96, &[32, 0, 0, 0, 2, 0, 0, 0]);
        owner.poke(256, &wrapped);
        owner.store_u64(off::WRITE_POS, 96 + 32 + 56);
        assert_eq!(ring.drain(&mut out).expect("wrapped"), 1);
        assert_eq!(out[1].text_lossy(), "b".repeat(16));
        assert_eq!(owner.load_u64(off::READ_POS), 184);
    }

    #[test]
    fn a_missing_section_and_a_missing_nt_path_are_no_section() {
        assert!(matches!(
            NativeSection::open_by_name(r"Local\SimsOnlineTest.SimLink.no-such-section"),
            Err(RingError::NoSection(_))
        ));
        let directory = ring::sandboxie_directory("nobody", "nobox", 1);
        assert!(matches!(NativeSection::open_nt(&directory, "SimsOnline.SimLink.1"), Err(RingError::NoSection(_))));
    }

    #[test]
    fn the_native_section_refuses_header_and_out_of_range_ring_copies_without_touching_memory() {
        let owner = Owner::create(4096);
        owner.init(1024);
        let section = NativeSection::open_by_name(&owner.name).expect("open");
        let mut buf = [0xFFu8; 8];
        section.copy_ring(section.len() - 4, &mut buf);
        assert_eq!(buf, [0u8; 8], "past the mapping");
        let mut header = [0xFFu8; 8];
        section.copy_ring(0, &mut header);
        assert_eq!(header, [0u8; 8], "the magic is only reachable through its atomic slot");
        section.copy_ring(ring::HEADER_BYTES as usize - 4, &mut header);
        assert_eq!(header, [0u8; 8], "a range that starts in the header is refused");
        owner.poke(ring::HEADER_BYTES as usize, &[7u8; 8]);
        let mut ring_bytes = [0u8; 8];
        section.copy_ring(ring::HEADER_BYTES as usize, &mut ring_bytes);
        assert_eq!(ring_bytes, [7u8; 8], "the ring region itself is readable");
        assert_eq!(&section.magic(), b"SIMLINK1");
        assert_eq!(section.ring_size(), 1024);
    }

    #[test]
    fn a_reserved_uncommitted_section_is_rejected_without_reading_its_pages() {
        let unique = format!("SimsOnlineTest.SimLink.{}.{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst));
        let name = format!(r"Local\{unique}");
        let wide_name = wide(&name);
        // SAFETY: a pagefile-backed, reserve-only mapping of 4096 bytes named by a live NUL-terminated buffer.
        let section = unsafe {
            CreateFileMappingW(INVALID_HANDLE_VALUE, ptr::null(), PAGE_READWRITE | SEC_RESERVE, 0, 4096, wide_name.as_ptr())
        };
        assert!(!section.is_null(), "CreateFileMappingW(SEC_RESERVE)");
        let opened = NativeSection::open_by_name(&name);
        assert!(matches!(opened, Err(RingError::BadHeader(_))), "reserved pages are not a usable ring");
        // SAFETY: `section` is the handle created above, closed exactly once.
        unsafe {
            CloseHandle(section);
        }
    }
}
