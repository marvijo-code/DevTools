use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread;

use crate::win32::*;

#[repr(C)]
#[derive(Default, Debug, Clone, Copy)]
pub struct FastDeleteResult {
    pub files_deleted: u64,
    pub dirs_deleted: u64,
    pub bytes_freed: u64,
    pub skipped: u64,
    pub root_removed: u32,
    pub last_error: u32,
}

struct DirQueue {
    items: Mutex<VecDeque<Vec<u16>>>,
    cv: Condvar,
    active: AtomicUsize,
    closed: AtomicBool,
}

impl DirQueue {
    fn new() -> Self {
        Self {
            items: Mutex::new(VecDeque::with_capacity(4096)),
            cv: Condvar::new(),
            active: AtomicUsize::new(0),
            closed: AtomicBool::new(false),
        }
    }

    fn push(&self, dir: Vec<u16>) {
        self.active.fetch_add(1, Ordering::AcqRel);
        let mut guard = self.items.lock().unwrap();
        guard.push_back(dir);
        self.cv.notify_one();
    }

    fn push_batch(&self, mut dirs: Vec<Vec<u16>>) {
        if dirs.is_empty() {
            return;
        }
        self.active.fetch_add(dirs.len(), Ordering::AcqRel);
        let mut guard = self.items.lock().unwrap();
        for d in dirs.drain(..) {
            guard.push_back(d);
        }
        self.cv.notify_all();
    }

    fn pop(&self, cancel: &AtomicBool) -> Option<Vec<u16>> {
        let mut guard = self.items.lock().unwrap();
        loop {
            if cancel.load(Ordering::Relaxed) {
                return None;
            }
            if let Some(item) = guard.pop_back() {
                return Some(item);
            }
            if self.closed.load(Ordering::Acquire) || self.active.load(Ordering::Acquire) == 0 {
                self.closed.store(true, Ordering::Release);
                self.cv.notify_all();
                return None;
            }
            guard = self.cv.wait(guard).unwrap();
        }
    }

    fn complete_one(&self) {
        if self.active.fetch_sub(1, Ordering::AcqRel) == 1 {
            let _guard = self.items.lock().unwrap();
            self.closed.store(true, Ordering::Release);
            self.cv.notify_all();
        }
    }
}

/// Fast single-call POSIX/NTFS delete using SetFileInformationByHandle(FileDispositionInfoEx)
/// with POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE, falling back to SetFileAttributesW + DeleteFileW.
#[inline]
fn delete_file_fast(path_nul: &[u16], attrs: DWORD) -> Result<(), DWORD> {
    unsafe {
        // Fast path on Windows 10 1809+: one kernel open+disposition call handles read-only files
        // and immediately unlinks the directory entry under POSIX semantics.
        let handle = CreateFileW(
            path_nul.as_ptr(),
            DELETE,
            FILE_SHARE_ALL,
            std::ptr::null_mut(),
            OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL,
            std::ptr::null_mut(),
        );
        if handle != INVALID_HANDLE_VALUE {
            let info = FILE_DISPOSITION_INFO_EX_STRUCT {
                Flags: FILE_DISPOSITION_FLAG_DELETE
                    | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                    | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
            };
            let ok = SetFileInformationByHandle(
                handle,
                FILE_DISPOSITION_INFO_EX,
                &info as *const _ as LPCVOID,
                std::mem::size_of::<FILE_DISPOSITION_INFO_EX_STRUCT>() as DWORD,
            );
            CloseHandle(handle);
            if ok != 0 {
                return Ok(());
            }
        }

        // Fallback: clear readonly/system/hidden if set, then DeleteFileW.
        if (attrs & (FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_SYSTEM | FILE_ATTRIBUTE_HIDDEN)) != 0
        {
            SetFileAttributesW(path_nul.as_ptr(), FILE_ATTRIBUTE_NORMAL);
        }
        if DeleteFileW(path_nul.as_ptr()) != 0 {
            return Ok(());
        }
        let err = GetLastError();
        if err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND {
            return Ok(());
        }
        if err == ERROR_ACCESS_DENIED {
            SetFileAttributesW(path_nul.as_ptr(), FILE_ATTRIBUTE_NORMAL);
            if DeleteFileW(path_nul.as_ptr()) != 0 {
                return Ok(());
            }
            return Err(GetLastError());
        }
        Err(err)
    }
}

/// Removes an empty directory OR a directory reparse point (junction / directory symlink)
/// WITHOUT ever recursing into the reparse point's target.
#[inline]
fn remove_dir_or_reparse_fast(path_nul: &[u16], attrs: DWORD) -> Result<(), DWORD> {
    unsafe {
        if (attrs & (FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_SYSTEM)) != 0 {
            SetFileAttributesW(path_nul.as_ptr(), FILE_ATTRIBUTE_NORMAL);
        }
        if RemoveDirectoryW(path_nul.as_ptr()) != 0 {
            return Ok(());
        }
        let err = GetLastError();
        if err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND {
            return Ok(());
        }
        if err == ERROR_ACCESS_DENIED {
            SetFileAttributesW(path_nul.as_ptr(), FILE_ATTRIBUTE_NORMAL);
            if RemoveDirectoryW(path_nul.as_ptr()) != 0 {
                return Ok(());
            }
            return Err(GetLastError());
        }
        Err(err)
    }
}

/// Deletes a file, directory junction, or entire directory tree using a multi-threaded worker pool.
/// If `keep_root` is true, empties the directory's contents while keeping the root folder itself.
pub fn fast_delete(
    path_utf16: &[u16],
    keep_root: bool,
    worker_count: usize,
    cancel_flag: Option<&AtomicBool>,
) -> FastDeleteResult {
    let mut result = FastDeleteResult::default();
    if path_utf16.is_empty() {
        return result;
    }

    let root_nul = to_extended_wide(path_utf16);
    let root_attrs = unsafe { GetFileAttributesW(root_nul.as_ptr()) };
    if root_attrs == u32::MAX {
        let err = unsafe { GetLastError() };
        if err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND {
            result.root_removed = if keep_root { 0 } else { 1 };
        } else {
            result.last_error = err;
            result.skipped = 1;
        }
        return result;
    }

    let is_dir = (root_attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
    let is_reparse = (root_attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

    // Single file
    if !is_dir {
        if keep_root {
            return result;
        }
        match delete_file_fast(&root_nul, root_attrs) {
            Ok(()) => {
                result.files_deleted = 1;
                result.root_removed = 1;
            }
            Err(err) => {
                result.skipped = 1;
                result.last_error = err;
            }
        }
        return result;
    }

    // Directory junction or symlink: unlink ONLY the reparse point, never touch target contents!
    if is_reparse {
        if keep_root {
            return result;
        }
        match remove_dir_or_reparse_fast(&root_nul, root_attrs) {
            Ok(()) => {
                result.dirs_deleted = 1;
                result.root_removed = 1;
            }
            Err(err) => {
                result.skipped = 1;
                result.last_error = err;
            }
        }
        return result;
    }

    // Strip trailing NUL for building child paths.
    let root_no_nul = root_nul[..root_nul.len() - 1].to_vec();

    let workers = if worker_count == 0 {
        let cores = thread::available_parallelism().map(|n| n.get()).unwrap_or(8);
        cores.clamp(4, 24)
    } else {
        worker_count.clamp(1, 32)
    };

    let queue = Arc::new(DirQueue::new());
    let local_cancel = Arc::new(AtomicBool::new(false));
    let files_deleted = Arc::new(AtomicU64::new(0));
    let dirs_deleted = Arc::new(AtomicU64::new(0));
    let bytes_freed = Arc::new(AtomicU64::new(0));
    let skipped = Arc::new(AtomicU64::new(0));
    let last_error = Arc::new(AtomicU64::new(0));

    // All discovered subdirectories (without trailing NUL), stored with their depth for bottom-up removal.
    let discovered_dirs: Arc<Mutex<Vec<(u32, Vec<u16>, DWORD)>>> =
        Arc::new(Mutex::new(Vec::with_capacity(1024)));

    queue.push(root_no_nul.clone());

    let mut handles = Vec::with_capacity(workers);
    for _ in 0..workers {
        let queue = Arc::clone(&queue);
        let cancel = Arc::clone(&local_cancel);
        let files_deleted = Arc::clone(&files_deleted);
        let dirs_deleted = Arc::clone(&dirs_deleted);
        let bytes_freed = Arc::clone(&bytes_freed);
        let skipped = Arc::clone(&skipped);
        let last_error = Arc::clone(&last_error);
        let discovered_dirs = Arc::clone(&discovered_dirs);
        let ext_cancel_ptr = cancel_flag.map(|c| c as *const AtomicBool as usize).unwrap_or(0);

        handles.push(thread::spawn(move || {
            let mut pattern = Vec::with_capacity(512);
            let mut child_buf = Vec::with_capacity(512);
            let mut local_subdirs: Vec<Vec<u16>> = Vec::with_capacity(64);
            let mut local_record: Vec<(u32, Vec<u16>, DWORD)> = Vec::with_capacity(64);
            let mut find_data: WIN32_FIND_DATAW = unsafe { std::mem::zeroed() };
            let mut local_files: u64 = 0;
            let mut local_dirs: u64 = 0;
            let mut local_bytes: u64 = 0;
            let mut local_skipped: u64 = 0;
            let mut local_err: u32 = 0;

            let is_canceled = || -> bool {
                if cancel.load(Ordering::Relaxed) {
                    return true;
                }
                if ext_cancel_ptr != 0 {
                    let ext = unsafe { &*(ext_cancel_ptr as *const AtomicBool) };
                    if ext.load(Ordering::Relaxed) {
                        cancel.store(true, Ordering::Relaxed);
                        return true;
                    }
                }
                false
            };

            while let Some(dir_path) = queue.pop(&cancel) {
                if is_canceled() {
                    queue.complete_one();
                    break;
                }

                // Build pattern: `<dir_path>\*\0`
                pattern.clear();
                pattern.extend_from_slice(&dir_path);
                if !pattern.ends_with(&[b'\\' as u16]) {
                    pattern.push(b'\\' as u16);
                }
                pattern.push(b'*' as u16);
                pattern.push(0);

                let find_handle = unsafe {
                    FindFirstFileExW(
                        pattern.as_ptr(),
                        FIND_EX_INFO_BASIC,
                        &mut find_data,
                        FIND_EX_SEARCH_NAME_MATCH,
                        std::ptr::null_mut(),
                        FIND_FIRST_EX_LARGE_FETCH,
                    )
                };

                if find_handle == INVALID_HANDLE_VALUE {
                    let err = unsafe { GetLastError() };
                    if err != ERROR_FILE_NOT_FOUND && err != ERROR_NO_MORE_FILES {
                        local_skipped += 1;
                        if local_err == 0 {
                            local_err = err;
                        }
                    }
                    queue.complete_one();
                    continue;
                }

                local_subdirs.clear();
                let depth = dir_path.iter().filter(|&&c| c == b'\\' as u16).count() as u32 + 1;

                loop {
                    if is_canceled() {
                        break;
                    }

                    let name = wide_name_slice(&find_data.cFileName);
                    if !is_dot_or_dotdot(name) {
                        let attrs = find_data.dwFileAttributes;
                        join_wide_into(&dir_path, name, &mut child_buf);

                        if (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0 {
                            if (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0 {
                                // Unlink directory junction/symlink safely without recursing!
                                match remove_dir_or_reparse_fast(&child_buf, attrs) {
                                    Ok(()) => local_dirs += 1,
                                    Err(err) => {
                                        local_skipped += 1;
                                        if local_err == 0 {
                                            local_err = err;
                                        }
                                    }
                                }
                            } else {
                                let subdir_no_nul = child_buf[..child_buf.len() - 1].to_vec();
                                local_record.push((depth, subdir_no_nul.clone(), attrs));
                                local_subdirs.push(subdir_no_nul);
                            }
                        } else {
                            let size = ((find_data.nFileSizeHigh as u64) << 32)
                                | (find_data.nFileSizeLow as u64);
                            match delete_file_fast(&child_buf, attrs) {
                                Ok(()) => {
                                    local_files += 1;
                                    local_bytes += size;
                                }
                                Err(err) => {
                                    local_skipped += 1;
                                    if local_err == 0 {
                                        local_err = err;
                                    }
                                }
                            }
                        }
                    }

                    if unsafe { FindNextFileW(find_handle, &mut find_data) } == 0 {
                        break;
                    }
                }

                unsafe { FindClose(find_handle) };

                if !local_subdirs.is_empty() && !is_canceled() {
                    queue.push_batch(std::mem::take(&mut local_subdirs));
                }

                if local_record.len() >= 128 {
                    let mut guard = discovered_dirs.lock().unwrap();
                    guard.append(&mut local_record);
                }

                queue.complete_one();
            }

            if !local_record.is_empty() {
                let mut guard = discovered_dirs.lock().unwrap();
                guard.append(&mut local_record);
            }

            files_deleted.fetch_add(local_files, Ordering::Relaxed);
            dirs_deleted.fetch_add(local_dirs, Ordering::Relaxed);
            bytes_freed.fetch_add(local_bytes, Ordering::Relaxed);
            skipped.fetch_add(local_skipped, Ordering::Relaxed);
            if local_err != 0 {
                let _ = last_error.compare_exchange(
                    0,
                    local_err as u64,
                    Ordering::Relaxed,
                    Ordering::Relaxed,
                );
            }
        }));
    }

    for h in handles {
        let _ = h.join();
    }

    let canceled = cancel_flag.map(|c| c.load(Ordering::Relaxed)).unwrap_or(false);

    // Phase 2: remove empty subdirectories bottom-up (deepest first).
    // Grouping by depth allows parallel RemoveDirectoryW within each depth level!
    let mut dirs = discovered_dirs.lock().unwrap();
    if !canceled && !dirs.is_empty() {
        dirs.sort_unstable_by(|a, b| b.0.cmp(&a.0));
        let mut start = 0;
        while start < dirs.len() {
            let depth = dirs[start].0;
            let mut end = start + 1;
            while end < dirs.len() && dirs[end].0 == depth {
                end += 1;
            }
            let level = &dirs[start..end];
            if level.len() >= 64 && workers > 1 {
                let chunk_size = (level.len() + workers - 1) / workers;
                thread::scope(|s| {
                    for chunk in level.chunks(chunk_size) {
                        let dirs_deleted = &dirs_deleted;
                        let skipped = &skipped;
                        let last_error = &last_error;
                        s.spawn(move || {
                            let mut buf = Vec::with_capacity(512);
                            let mut ld = 0u64;
                            let mut ls = 0u64;
                            let mut le = 0u32;
                            for (_, p, attrs) in chunk {
                                buf.clear();
                                buf.extend_from_slice(p);
                                buf.push(0);
                                match remove_dir_or_reparse_fast(&buf, *attrs) {
                                    Ok(()) => ld += 1,
                                    Err(err) => {
                                        ls += 1;
                                        if le == 0 {
                                            le = err;
                                        }
                                    }
                                }
                            }
                            dirs_deleted.fetch_add(ld, Ordering::Relaxed);
                            skipped.fetch_add(ls, Ordering::Relaxed);
                            if le != 0 {
                                let _ = last_error.compare_exchange(
                                    0,
                                    le as u64,
                                    Ordering::Relaxed,
                                    Ordering::Relaxed,
                                );
                            }
                        });
                    }
                });
            } else {
                let mut buf = Vec::with_capacity(512);
                for (_, p, attrs) in level {
                    buf.clear();
                    buf.extend_from_slice(p);
                    buf.push(0);
                    match remove_dir_or_reparse_fast(&buf, *attrs) {
                        Ok(()) => {
                            dirs_deleted.fetch_add(1, Ordering::Relaxed);
                        }
                        Err(err) => {
                            skipped.fetch_add(1, Ordering::Relaxed);
                            let _ = last_error.compare_exchange(
                                0,
                                err as u64,
                                Ordering::Relaxed,
                                Ordering::Relaxed,
                            );
                        }
                    }
                }
            }
            start = end;
        }
    }
    drop(dirs);

    // Phase 3: remove root directory itself unless keep_root is set.
    if !keep_root && !canceled {
        match remove_dir_or_reparse_fast(&root_nul, root_attrs) {
            Ok(()) => {
                dirs_deleted.fetch_add(1, Ordering::Relaxed);
                result.root_removed = 1;
            }
            Err(err) => {
                skipped.fetch_add(1, Ordering::Relaxed);
                let _ = last_error.compare_exchange(
                    0,
                    err as u64,
                    Ordering::Relaxed,
                    Ordering::Relaxed,
                );
                result.root_removed = 0;
            }
        }
    }

    result.files_deleted = files_deleted.load(Ordering::Relaxed);
    result.dirs_deleted = dirs_deleted.load(Ordering::Relaxed);
    result.bytes_freed = bytes_freed.load(Ordering::Relaxed);
    result.skipped = skipped.load(Ordering::Relaxed);
    result.last_error = last_error.load(Ordering::Relaxed) as u32;
    result
}
