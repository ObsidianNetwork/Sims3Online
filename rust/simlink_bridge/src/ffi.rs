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
