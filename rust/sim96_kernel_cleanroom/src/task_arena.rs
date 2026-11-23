#![forbid(unsafe_code)]

//! Typed task/object handles backed by the kernel's existing arena.
//!
//! This layer adds payload-type checking, not a new allocator or ABI handle.
//! Generation advancement, exhausted-slot retirement, and errors remain owned
//! by [`crate::handles::HandleArena`].

use std::cmp::Ordering;
use std::fmt;
use std::hash::{Hash, Hasher};
use std::marker::PhantomData;

pub use crate::handles::KernelError;

/// An arena index and generation tagged with the stored payload type.
///
/// Handles do not own their payload and remain copyable even when `T` is not.
/// They distinguish payload types, not separate arenas storing the same type.
///
/// ```
/// use sim96_kernel_cleanroom::task_arena::HandleArena;
///
/// let mut arena = HandleArena::new();
/// let handle = arena.insert(String::from("task")).unwrap();
/// assert_eq!(arena.get(handle).unwrap(), "task");
/// assert_eq!(arena.remove(handle).unwrap(), "task");
/// ```
///
/// A handle for one payload type cannot access another payload type:
///
/// ```compile_fail,E0308
/// use sim96_kernel_cleanroom::task_arena::HandleArena;
///
/// let mut tasks = HandleArena::<String>::new();
/// let objects = HandleArena::<u32>::new();
/// let task = tasks.insert(String::from("task")).unwrap();
/// objects.get(task); // Expected GenHandle<u32>, not GenHandle<String>.
/// ```
pub struct GenHandle<T> {
    pub index: u32,
    pub generation: u32,
    marker: PhantomData<fn() -> T>,
}

impl<T> GenHandle<T> {
    /// Reconstruct a typed identity. The arena validates it on every access.
    pub const fn from_parts(index: u32, generation: u32) -> Self {
        Self { index, generation, marker: PhantomData }
    }

    pub const fn index(self) -> u32 { self.index }

    pub const fn generation(self) -> u32 { self.generation }

    fn into_raw(self) -> crate::handles::GenHandle {
        crate::handles::GenHandle { index: self.index, generation: self.generation }
    }
}

impl<T> Copy for GenHandle<T> {}

impl<T> Clone for GenHandle<T> {
    fn clone(&self) -> Self { *self }
}

impl<T> fmt::Debug for GenHandle<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("GenHandle")
            .field("index", &self.index)
            .field("generation", &self.generation)
            .finish()
    }
}

impl<T> PartialEq for GenHandle<T> {
    fn eq(&self, other: &Self) -> bool {
        (self.index, self.generation) == (other.index, other.generation)
    }
}

impl<T> Eq for GenHandle<T> {}

impl<T> PartialOrd for GenHandle<T> {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl<T> Ord for GenHandle<T> {
    fn cmp(&self, other: &Self) -> Ordering {
        (self.index, self.generation).cmp(&(other.index, other.generation))
    }
}

impl<T> Hash for GenHandle<T> {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.index.hash(state);
        self.generation.hash(state);
    }
}

/// Payload-typed access to the existing generation-checked storage.
///
/// All errors, including `UnknownHandle`, `StaleObjectHandle`, and
/// `SlotRetired`, propagate unchanged from the underlying arena.
pub struct HandleArena<T> {
    inner: crate::handles::HandleArena<T>,
}

impl<T> Default for HandleArena<T> {
    fn default() -> Self {
        Self { inner: crate::handles::HandleArena::new() }
    }
}

impl<T> HandleArena<T> {
    pub fn new() -> Self { Self::default() }

    pub fn insert(&mut self, value: T) -> Result<GenHandle<T>, KernelError> {
        self.inner.insert(value).map(|h| GenHandle::from_parts(h.index, h.generation))
    }

    pub fn get(&self, handle: GenHandle<T>) -> Result<&T, KernelError> {
        self.inner.get(handle.into_raw())
    }

    pub fn get_mut(&mut self, handle: GenHandle<T>) -> Result<&mut T, KernelError> {
        self.inner.get_mut(handle.into_raw())
    }

    pub fn remove(&mut self, handle: GenHandle<T>) -> Result<T, KernelError> {
        self.inner.remove(handle.into_raw())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reused_slot_rejects_every_stale_access() {
        let mut arena = HandleArena::new();
        let old = arena.insert(11).unwrap();
        assert_eq!(arena.remove(old), Ok(11));
        let current = arena.insert(22).unwrap();
        assert_eq!(old.index(), current.index());
        assert_eq!(current.generation(), old.generation() + 1);

        assert_eq!(arena.get(old), Err(KernelError::StaleObjectHandle));
        assert_eq!(arena.get_mut(old), Err(KernelError::StaleObjectHandle));
        assert_eq!(arena.remove(old), Err(KernelError::StaleObjectHandle));
        assert_eq!(arena.get(current), Ok(&22));
    }

    #[test]
    fn removed_slot_rejects_reads_mutation_and_double_remove() {
        let mut arena = HandleArena::new();
        let handle = arena.insert(7).unwrap();
        assert_eq!(arena.remove(handle), Ok(7));
        assert_eq!(arena.get(handle), Err(KernelError::StaleObjectHandle));
        assert_eq!(arena.get_mut(handle), Err(KernelError::StaleObjectHandle));
        assert_eq!(arena.remove(handle), Err(KernelError::StaleObjectHandle));
    }

    #[test]
    fn unknown_indices_return_the_original_error() {
        let mut arena = HandleArena::<u32>::new();
        for index in [0, 9999, u32::MAX] {
            let handle = GenHandle::from_parts(index, 0);
            assert_eq!(arena.get(handle), Err(KernelError::UnknownHandle));
            assert_eq!(arena.get_mut(handle), Err(KernelError::UnknownHandle));
            assert_eq!(arena.remove(handle), Err(KernelError::UnknownHandle));
        }
        let live = arena.insert(9).unwrap();
        let unknown = GenHandle::from_parts(u32::MAX, u32::MAX);
        assert_eq!(arena.get(unknown), Err(KernelError::UnknownHandle));
        assert_eq!(arena.get_mut(unknown), Err(KernelError::UnknownHandle));
        assert_eq!(arena.remove(unknown), Err(KernelError::UnknownHandle));
        assert_eq!(arena.get(live), Ok(&9));
    }

    #[test]
    fn reconstructed_identity_still_checks_generation() {
        let mut arena = HandleArena::new();
        let live = arena.insert(5).unwrap();
        let restored = GenHandle::from_parts(live.index(), live.generation());
        assert_eq!(restored, live);
        assert_eq!(arena.get(restored), Ok(&5));
        let wrong_generation = GenHandle::from_parts(live.index(), u32::MAX);
        assert_eq!(arena.get(wrong_generation), Err(KernelError::StaleObjectHandle));
        assert_eq!(arena.get_mut(wrong_generation), Err(KernelError::StaleObjectHandle));
        assert_eq!(arena.remove(wrong_generation), Err(KernelError::StaleObjectHandle));
        assert_eq!(arena.get(live), Ok(&5));
    }

    #[test]
    fn non_copy_non_clone_payload_has_copyable_cloneable_handles() {
        struct Payload(String);

        fn require_copy<T: Copy>() {}
        fn clone_value<T: Clone>(value: &T) -> T { value.clone() }

        require_copy::<GenHandle<Payload>>();
        let mut arena = HandleArena::default();
        let handle = arena.insert(Payload(String::from("task"))).unwrap();
        let duplicate = clone_value(&handle);
        assert_eq!(handle, duplicate);
        arena.get_mut(handle).unwrap().0.push_str(" updated");
        assert_eq!(arena.get(duplicate).unwrap().0, "task updated");
        assert_eq!(arena.remove(handle).unwrap().0, "task updated");
        assert!(matches!(arena.get(duplicate), Err(KernelError::StaleObjectHandle)));
    }
}
