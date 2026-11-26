mod fast_delete;
mod fast_walk;
mod win32;

use std::sync::atomic::AtomicBool;

pub use fast_delete::FastDeleteResult;
pub use fast_walk::{FastMeasureResult, NativeWalkTree};

const API_VERSION: u32 = 1;

#[no_mangle]
pub extern "C" fn dt_api_version() -> u32 {
    API_VERSION
}

/// Fast multi-threaded deletion of a file, directory junction, or entire directory tree.
/// `path_ptr` is a UTF-16 buffer of length `path_len` (without requiring NUL termination).
/// `keep_root`: non-zero empties directory contents while keeping the root directory.
/// `worker_count`: 0 = auto (4..24 based on CPU cores).
/// `cancel_ptr`: optional pointer to an i32/AtomicBool cancel flag (non-zero = cancel).
#[no_mangle]
pub unsafe extern "C" fn dt_fast_delete_path(
    path_ptr: *const u16,
    path_len: u32,
    keep_root: u32,
    worker_count: u32,
    cancel_ptr: *const u8,
) -> FastDeleteResult {
    if path_ptr.is_null() || path_len == 0 {
        return FastDeleteResult::default();
    }
    let slice = std::slice::from_raw_parts(path_ptr, path_len as usize);
    let cancel = if cancel_ptr.is_null() {
        None
    } else {
        Some(&*(cancel_ptr as *const AtomicBool))
    };
    fast_delete::fast_delete(slice, keep_root != 0, worker_count as usize, cancel)
}

/// Fast multi-threaded directory tree walker returning a CSR-ready folder tree with per-folder hashes.
/// Caller must free the returned pointer with `dt_free_walk_tree`.
#[no_mangle]
pub unsafe extern "C" fn dt_fast_walk_tree(
    path_ptr: *const u16,
    path_len: u32,
    worker_count: u32,
    cancel_ptr: *const u8,
) -> *mut NativeWalkTree {
    if path_ptr.is_null() || path_len == 0 {
        return std::ptr::null_mut();
    }
    let slice = std::slice::from_raw_parts(path_ptr, path_len as usize);
    let cancel = if cancel_ptr.is_null() {
        None
    } else {
        Some(&*(cancel_ptr as *const AtomicBool))
    };
    fast_walk::fast_walk_tree(slice, worker_count as usize, cancel)
}

#[no_mangle]
pub unsafe extern "C" fn dt_free_walk_tree(tree_ptr: *mut NativeWalkTree) {
    fast_walk::free_walk_tree(tree_ptr);
}

/// Fast multi-threaded single-directory measurement (bytes, files, dirs, content hash).
#[no_mangle]
pub unsafe extern "C" fn dt_fast_measure_dir(
    path_ptr: *const u16,
    path_len: u32,
    worker_count: u32,
    cancel_ptr: *const u8,
) -> FastMeasureResult {
    if path_ptr.is_null() || path_len == 0 {
        return FastMeasureResult::default();
    }
    let slice = std::slice::from_raw_parts(path_ptr, path_len as usize);
    let cancel = if cancel_ptr.is_null() {
        None
    } else {
        Some(&*(cancel_ptr as *const AtomicBool))
    };
    fast_walk::fast_measure_dir(slice, worker_count as usize, cancel)
}
