use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread;

use crate::win32::*;

/// Flat CSR-ready folder tree built in native Rust and returned to C# via FFI.
/// Caller must free with `dt_free_walk_tree`.
#[repr(C)]
pub struct NativeWalkTree {
    pub folder_count: u32,
    pub incomplete_count: u32,
    pub total_bytes: u64,
    pub total_files: u64,
    pub tree_hash: u64,
    pub parents: *mut i32,
    pub total_bytes_nodes: *mut i64,
    pub file_count_nodes: *mut i64,
    pub dir_mtime_nodes: *mut i64,
    pub dir_hash_nodes: *mut u64,
    pub incomplete_flags: *mut u8,
    pub name_offsets: *mut i32,
    pub name_blob: *mut u8,
    pub name_blob_len: u32,
}

struct WalkJob {
    node_id: usize,
    path_no_nul: Vec<u16>,
}

struct WalkQueue {
    items: Mutex<VecDeque<WalkJob>>,
    cv: Condvar,
    active: AtomicUsize,
    closed: AtomicBool,
}

impl WalkQueue {
    fn new() -> Self {
        Self {
            items: Mutex::new(VecDeque::with_capacity(4096)),
            cv: Condvar::new(),
            active: AtomicUsize::new(0),
            closed: AtomicBool::new(false),
        }
    }

    fn push_one(&self, job: WalkJob) {
        self.active.fetch_add(1, Ordering::AcqRel);
        let mut guard = self.items.lock().unwrap();
        guard.push_back(job);
        self.cv.notify_one();
    }

    fn push_batch(&self, mut jobs: Vec<WalkJob>) {
        if jobs.is_empty() {
            return;
        }
        self.active.fetch_add(jobs.len(), Ordering::AcqRel);
        let mut guard = self.items.lock().unwrap();
        for job in jobs.drain(..) {
            guard.push_back(job);
        }
        self.cv.notify_all();
    }

    fn pop(&self, cancel: &AtomicBool) -> Option<WalkJob> {
        let mut guard = self.items.lock().unwrap();
        loop {
            if cancel.load(Ordering::Relaxed) {
                return None;
            }
            if let Some(job) = guard.pop_back() {
                return Some(job);
            }
            if self.closed.load(Ordering::Acquire) || self.active.load(Ordering::Acquire) == 0 {
                self.closed.store(true, Ordering::Release);
                self.cv.notify_all();
                return None;
            }
            guard = self.cv.wait(guard).unwrap();
        }
    }

    fn is_low(&self, threshold: usize) -> bool {
        let guard = self.items.lock().unwrap();
        guard.len() < threshold
    }

    fn complete_n(&self, n: usize) {
        if self.active.fetch_sub(n, Ordering::AcqRel) == n {
            let _guard = self.items.lock().unwrap();
            self.closed.store(true, Ordering::Release);
            self.cv.notify_all();
        }
    }

    fn complete_one(&self) {
        self.complete_n(1);
    }
}

struct TreeBuilderState {
    parents: Vec<i32>,
    own_bytes: Vec<i64>,
    own_files: Vec<i64>,
    dir_mtime: Vec<i64>,
    dir_hash: Vec<u64>,
    incomplete: Vec<u8>,
    name_offsets: Vec<i32>,
    name_blob: Vec<u8>,
}

impl TreeBuilderState {
    fn new() -> Self {
        let mut name_offsets = Vec::with_capacity(4096);
        name_offsets.push(0);
        name_offsets.push(0); // node 0 (root) has empty name
        Self {
            parents: vec![0],
            own_bytes: vec![0],
            own_files: vec![0],
            dir_mtime: vec![0],
            dir_hash: vec![0],
            incomplete: vec![0],
            name_offsets,
            name_blob: Vec::with_capacity(65536),
        }
    }

    fn append_children(
        &mut self,
        parent_node: usize,
        own_bytes: i64,
        own_files: i64,
        local_hash: u64,
        is_incomplete: bool,
        children: &[(String, i64)],
    ) -> usize {
        self.own_bytes[parent_node] = own_bytes;
        self.own_files[parent_node] = own_files;
        self.dir_hash[parent_node] = local_hash;
        if is_incomplete {
            self.incomplete[parent_node] = 1;
        }

        let first = self.parents.len();
        let parent_i32 = parent_node as i32;
        for (name, mtime) in children {
            self.parents.push(parent_i32);
            self.own_bytes.push(0);
            self.own_files.push(0);
            self.dir_mtime.push(*mtime);
            self.dir_hash.push(0);
            self.incomplete.push(0);
            self.name_blob.extend_from_slice(name.as_bytes());
            self.name_offsets.push(self.name_blob.len() as i32);
        }
        first
    }
}

#[inline]
fn fnv1a_step(mut h: u64, val: u64) -> u64 {
    const FNV_PRIME: u64 = 0x0000_0100_0000_01B3;
    h ^= val;
    h.wrapping_mul(FNV_PRIME)
}

#[inline]
fn filetime_to_i64(ft: &FILETIME) -> i64 {
    (((ft.dwHighDateTime as u64) << 32) | (ft.dwLowDateTime as u64)) as i64
}

/// Walks a directory subtree in parallel using FindFirstFileExW(FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH),
/// skipping directory reparse points/junctions. Returns a boxed `NativeWalkTree` (or null on cancel/error).
pub fn fast_walk_tree(
    root_utf16: &[u16],
    worker_count: usize,
    cancel_flag: Option<&AtomicBool>,
) -> *mut NativeWalkTree {
    if root_utf16.is_empty() {
        return std::ptr::null_mut();
    }

    let root_nul = to_extended_wide(root_utf16);
    let root_attrs = unsafe { GetFileAttributesW(root_nul.as_ptr()) };
    if root_attrs == u32::MAX || (root_attrs & FILE_ATTRIBUTE_DIRECTORY) == 0 {
        return std::ptr::null_mut();
    }

    let root_no_nul = root_nul[..root_nul.len() - 1].to_vec();

    let state = Arc::new(Mutex::new(TreeBuilderState::new()));

    // Never recurse through a reparse point / directory junction even when it is the scan root.
    if (root_attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0 {
        let workers = if worker_count == 0 {
            let cores = thread::available_parallelism().map(|n| n.get()).unwrap_or(8);
            cores.clamp(4, 24)
        } else {
            worker_count.clamp(1, 32)
        };

        let queue = Arc::new(WalkQueue::new());
        let local_cancel = Arc::new(AtomicBool::new(false));

        queue.push_one(WalkJob {
            node_id: 0,
            path_no_nul: root_no_nul,
        });

        let mut handles = Vec::with_capacity(workers);
        for _ in 0..workers {
        let queue = Arc::clone(&queue);
        let state = Arc::clone(&state);
        let cancel = Arc::clone(&local_cancel);
        let ext_cancel_ptr = cancel_flag.map(|c| c as *const AtomicBool as usize).unwrap_or(0);

        handles.push(thread::spawn(move || {
            let mut pattern = Vec::with_capacity(512);
            let mut local_stack: Vec<WalkJob> = Vec::with_capacity(128);
            let mut offload: Vec<WalkJob> = Vec::with_capacity(64);
            let mut child_wide_paths: Vec<Vec<u16>> = Vec::with_capacity(64);
            let mut child_meta: Vec<(String, i64)> = Vec::with_capacity(64);
            let mut find_data: WIN32_FIND_DATAW = unsafe { std::mem::zeroed() };

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

            while let Some(seed_job) = queue.pop(&cancel) {
                local_stack.push(seed_job);

                while let Some(job) = local_stack.pop() {
                    if is_canceled() {
                        local_stack.clear();
                        break;
                    }

                    pattern.clear();
                    pattern.extend_from_slice(&job.path_no_nul);
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
                        let incomplete = err != ERROR_FILE_NOT_FOUND && err != ERROR_NO_MORE_FILES;
                        if incomplete {
                            let mut guard = state.lock().unwrap();
                            guard.incomplete[job.node_id] = 1;
                        }
                        continue;
                    }

                    child_wide_paths.clear();
                    child_meta.clear();
                    let mut own_bytes: i64 = 0;
                    let mut own_files: i64 = 0;
                    // Order-independent hash accumulation over files in this directory (XOR + wrapping sum).
                    let mut hash_xor: u64 = 0;
                    let mut hash_sum: u64 = 0;

                    loop {
                        if is_canceled() {
                            break;
                        }

                        let name = wide_name_slice(&find_data.cFileName);
                        if !is_dot_or_dotdot(name) {
                            let attrs = find_data.dwFileAttributes;
                            let mtime = filetime_to_i64(&find_data.ftLastWriteTime);
                            if (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0 {
                                if (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0 {
                                    let name_str = String::from_utf16_lossy(name);
                                    let mut full = Vec::with_capacity(job.path_no_nul.len() + name.len() + 2);
                                    full.extend_from_slice(&job.path_no_nul);
                                    if !full.ends_with(&[b'\\' as u16]) {
                                        full.push(b'\\' as u16);
                                    }
                                    full.extend_from_slice(name);
                                    child_wide_paths.push(full);
                                    child_meta.push((name_str, mtime));
                                }
                            } else {
                                let size = ((find_data.nFileSizeHigh as u64) << 32)
                                    | (find_data.nFileSizeLow as u64);
                                own_bytes = own_bytes.saturating_add(size as i64);
                                own_files += 1;

                                // Mix file size + mtime + name length into directory content hash.
                                let mut entry_h = 0xCBF2_9CE4_8422_2325u64;
                                entry_h = fnv1a_step(entry_h, size);
                                entry_h = fnv1a_step(entry_h, mtime as u64);
                                entry_h = fnv1a_step(entry_h, name.len() as u64);
                                if !name.is_empty() {
                                    entry_h = fnv1a_step(entry_h, name[0] as u64);
                                    entry_h = fnv1a_step(entry_h, name[name.len() - 1] as u64);
                                }
                                hash_xor ^= entry_h;
                                hash_sum = hash_sum.wrapping_add(entry_h);
                            }
                        }

                        if unsafe { FindNextFileW(find_handle, &mut find_data) } == 0 {
                            break;
                        }
                    }

                    unsafe { FindClose(find_handle) };

                    if is_canceled() {
                        local_stack.clear();
                        break;
                    }

                    let local_hash = fnv1a_step(
                        fnv1a_step(0xCBF2_9CE4_8422_2325u64, hash_xor ^ hash_sum),
                        (own_bytes as u64) ^ ((own_files as u64).rotate_left(17)),
                    );

                    let first_child = {
                        let mut guard = state.lock().unwrap();
                        guard.append_children(
                            job.node_id,
                            own_bytes,
                            own_files,
                            local_hash,
                            false,
                            &child_meta,
                        )
                    };

                    if !child_wide_paths.is_empty() {
                        for (idx, path_no_nul) in child_wide_paths.drain(..).enumerate() {
                            local_stack.push(WalkJob {
                                node_id: first_child + idx,
                                path_no_nul,
                            });
                        }

                        if local_stack.len() >= 4 && queue.is_low(workers * 2) {
                            let share_count = local_stack.len() / 2;
                            offload.clear();
                            for _ in 0..share_count {
                                if let Some(j) = local_stack.pop() {
                                    offload.push(j);
                                }
                            }
                            if !offload.is_empty() {
                                queue.push_batch(std::mem::take(&mut offload));
                            }
                        }
                    }
                }

                queue.complete_one();
            }
        }));
    }

        for h in handles {
            let _ = h.join();
        }
    }

    if cancel_flag.map(|c| c.load(Ordering::Relaxed)).unwrap_or(false) {
        return std::ptr::null_mut();
    }

    let mut st = Arc::try_unwrap(state)
        .ok()
        .expect("all workers joined")
        .into_inner()
        .unwrap();

    let count = st.parents.len();
    let mut incomplete_count = 0u32;

    // Reverse bottom-up rollup: children always have higher indices than their parent.
    for i in (1..count).rev() {
        let p = st.parents[i] as usize;
        st.own_bytes[p] = st.own_bytes[p].saturating_add(st.own_bytes[i]);
        st.own_files[p] = st.own_files[p].saturating_add(st.own_files[i]);
        if st.incomplete[i] != 0 {
            incomplete_count += 1;
            st.incomplete[p] = 1;
        }
        // Roll directory Merkle hash into parent in an order-independent way.
        let child_mix = fnv1a_step(st.dir_hash[i], (st.dir_mtime[i] as u64) ^ (st.own_bytes[i] as u64));
        st.dir_hash[p] = st.dir_hash[p].wrapping_add(child_mix) ^ child_mix.rotate_left(13);
    }
    if st.incomplete[0] != 0 && count == 1 {
        incomplete_count += 1;
    }

    let total_bytes = st.own_bytes[0].max(0) as u64;
    let total_files = st.own_files[0].max(0) as u64;
    let tree_hash = st.dir_hash[0];
    let name_blob_len = st.name_blob.len() as u32;

    let mut parents = st.parents.into_boxed_slice();
    let mut total_bytes_nodes = st.own_bytes.into_boxed_slice();
    let mut file_count_nodes = st.own_files.into_boxed_slice();
    let mut dir_mtime_nodes = st.dir_mtime.into_boxed_slice();
    let mut dir_hash_nodes = st.dir_hash.into_boxed_slice();
    let mut incomplete_flags = st.incomplete.into_boxed_slice();
    let mut name_offsets = st.name_offsets.into_boxed_slice();
    let mut name_blob = st.name_blob.into_boxed_slice();

    let tree = Box::new(NativeWalkTree {
        folder_count: count as u32,
        incomplete_count,
        total_bytes,
        total_files,
        tree_hash,
        parents: parents.as_mut_ptr(),
        total_bytes_nodes: total_bytes_nodes.as_mut_ptr(),
        file_count_nodes: file_count_nodes.as_mut_ptr(),
        dir_mtime_nodes: dir_mtime_nodes.as_mut_ptr(),
        dir_hash_nodes: dir_hash_nodes.as_mut_ptr(),
        incomplete_flags: incomplete_flags.as_mut_ptr(),
        name_offsets: name_offsets.as_mut_ptr(),
        name_blob: name_blob.as_mut_ptr(),
        name_blob_len,
    });

    std::mem::forget(parents);
    std::mem::forget(total_bytes_nodes);
    std::mem::forget(file_count_nodes);
    std::mem::forget(dir_mtime_nodes);
    std::mem::forget(dir_hash_nodes);
    std::mem::forget(incomplete_flags);
    std::mem::forget(name_offsets);
    std::mem::forget(name_blob);

    Box::into_raw(tree)
}

pub unsafe fn free_walk_tree(ptr: *mut NativeWalkTree) {
    if ptr.is_null() {
        return;
    }
    let tree = Box::from_raw(ptr);
    let count = tree.folder_count as usize;
    let blob_len = tree.name_blob_len as usize;

    if !tree.parents.is_null() {
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(tree.parents, count));
    }
    if !tree.total_bytes_nodes.is_null() {
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(tree.total_bytes_nodes, count));
    }
    if !tree.file_count_nodes.is_null() {
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(tree.file_count_nodes, count));
    }
    if !tree.dir_mtime_nodes.is_null() {
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(tree.dir_mtime_nodes, count));
    }
    if !tree.dir_hash_nodes.is_null() {
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(tree.dir_hash_nodes, count));
    }
    if !tree.incomplete_flags.is_null() {
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(tree.incomplete_flags, count));
    }
    if !tree.name_offsets.is_null() {
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(tree.name_offsets, count + 1));
    }
    if !tree.name_blob.is_null() {
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(tree.name_blob, blob_len));
    }
}

/// Fast single-number measurement of a directory subtree (bytes + file count + hash) without building a node list.
#[repr(C)]
#[derive(Default, Debug, Clone, Copy)]
pub struct FastMeasureResult {
    pub total_bytes: u64,
    pub total_files: u64,
    pub total_dirs: u64,
    pub content_hash: u64,
    pub incomplete: u32,
}

pub fn fast_measure_dir(
    root_utf16: &[u16],
    worker_count: usize,
    cancel_flag: Option<&AtomicBool>,
) -> FastMeasureResult {
    let mut res = FastMeasureResult::default();
    if root_utf16.is_empty() {
        return res;
    }

    let root_nul = to_extended_wide(root_utf16);
    let root_attrs = unsafe { GetFileAttributesW(root_nul.as_ptr()) };
    if root_attrs == u32::MAX
        || (root_attrs & FILE_ATTRIBUTE_DIRECTORY) == 0
        || (root_attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0
    {
        return res;
    }

    let root_no_nul = root_nul[..root_nul.len() - 1].to_vec();

    // Fast single-threaded inline path: zero OS thread spawn, zero Mutex/Arc overhead.
    // Ideal when the caller already parallelizes across many directories (e.g. ProjectCleanerService).
    if worker_count == 1 {
        let mut stack: Vec<Vec<u16>> = Vec::with_capacity(256);
        let mut pattern: Vec<u16> = Vec::with_capacity(512);
        let mut find_data: WIN32_FIND_DATAW = unsafe { std::mem::zeroed() };
        stack.push(root_no_nul);

        let mut lb = 0u64;
        let mut lf = 0u64;
        let mut ld = 1u64;
        let mut lh = 0u64;
        let mut inc = 0u32;

        while let Some(dir_path) = stack.pop() {
            if cancel_flag.map(|c| c.load(Ordering::Relaxed)).unwrap_or(false) {
                break;
            }

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
                    inc = 1;
                }
                continue;
            }

            loop {
                let name = wide_name_slice(&find_data.cFileName);
                if !is_dot_or_dotdot(name) {
                    let attrs = find_data.dwFileAttributes;
                    if (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0 {
                        if (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0 {
                            ld += 1;
                            let mut full = Vec::with_capacity(dir_path.len() + name.len() + 2);
                            full.extend_from_slice(&dir_path);
                            if !full.ends_with(&[b'\\' as u16]) {
                                full.push(b'\\' as u16);
                            }
                            full.extend_from_slice(name);
                            stack.push(full);
                        }
                    } else {
                        let size = ((find_data.nFileSizeHigh as u64) << 32)
                            | (find_data.nFileSizeLow as u64);
                        let mtime = filetime_to_i64(&find_data.ftLastWriteTime) as u64;
                        lb = lb.saturating_add(size);
                        lf += 1;
                        lh ^= fnv1a_step(fnv1a_step(0xCBF2_9CE4_8422_2325u64, size), mtime);
                    }
                }

                if unsafe { FindNextFileW(find_handle, &mut find_data) } == 0 {
                    break;
                }
            }

            unsafe { FindClose(find_handle) };
        }

        res.total_bytes = lb;
        res.total_files = lf;
        res.total_dirs = ld;
        res.content_hash = lh;
        res.incomplete = inc;
        return res;
    }

    let workers = if worker_count == 0 {
        let cores = thread::available_parallelism().map(|n| n.get()).unwrap_or(8);
        cores.clamp(4, 24)
    } else {
        worker_count.clamp(1, 32)
    };

    let queue = Arc::new(WalkQueue::new());
    let local_cancel = Arc::new(AtomicBool::new(false));
    let total_bytes = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let total_files = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let total_dirs = Arc::new(std::sync::atomic::AtomicU64::new(1));
    let hash_acc = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let incomplete = Arc::new(std::sync::atomic::AtomicU32::new(0));

    queue.push_one(WalkJob {
        node_id: 0,
        path_no_nul: root_no_nul,
    });

    let mut handles = Vec::with_capacity(workers);
    for _ in 0..workers {
        let queue = Arc::clone(&queue);
        let cancel = Arc::clone(&local_cancel);
        let total_bytes = Arc::clone(&total_bytes);
        let total_files = Arc::clone(&total_files);
        let total_dirs = Arc::clone(&total_dirs);
        let hash_acc = Arc::clone(&hash_acc);
        let incomplete = Arc::clone(&incomplete);
        let ext_cancel_ptr = cancel_flag.map(|c| c as *const AtomicBool as usize).unwrap_or(0);

        handles.push(thread::spawn(move || {
            let mut pattern = Vec::with_capacity(512);
            let mut local_stack: Vec<Vec<u16>> = Vec::with_capacity(128);
            let mut offload: Vec<WalkJob> = Vec::with_capacity(64);
            let mut find_data: WIN32_FIND_DATAW = unsafe { std::mem::zeroed() };
            let mut lb = 0u64;
            let mut lf = 0u64;
            let mut ld = 0u64;
            let mut lh = 0u64;
            let mut inc = 0u32;

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

            while let Some(seed_job) = queue.pop(&cancel) {
                local_stack.push(seed_job.path_no_nul);

                while let Some(cur_path) = local_stack.pop() {
                    if is_canceled() {
                        local_stack.clear();
                        break;
                    }

                    pattern.clear();
                    pattern.extend_from_slice(&cur_path);
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
                            inc = 1;
                        }
                        continue;
                    }

                    loop {
                        if is_canceled() {
                            break;
                        }
                        let name = wide_name_slice(&find_data.cFileName);
                        if !is_dot_or_dotdot(name) {
                            let attrs = find_data.dwFileAttributes;
                            if (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0 {
                                if (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0 {
                                    ld += 1;
                                    let mut full = Vec::with_capacity(cur_path.len() + name.len() + 2);
                                    full.extend_from_slice(&cur_path);
                                    if !full.ends_with(&[b'\\' as u16]) {
                                        full.push(b'\\' as u16);
                                    }
                                    full.extend_from_slice(name);
                                    local_stack.push(full);
                                }
                            } else {
                                let size = ((find_data.nFileSizeHigh as u64) << 32)
                                    | (find_data.nFileSizeLow as u64);
                                let mtime = filetime_to_i64(&find_data.ftLastWriteTime) as u64;
                                lb = lb.saturating_add(size);
                                lf += 1;
                                lh ^= fnv1a_step(fnv1a_step(0xCBF2_9CE4_8422_2325u64, size), mtime);
                            }
                        }

                        if unsafe { FindNextFileW(find_handle, &mut find_data) } == 0 {
                            break;
                        }
                    }

                    unsafe { FindClose(find_handle) };

                    // Share work only when we have excess local folders and global queue is low.
                    if local_stack.len() >= 4 && queue.is_low(workers * 2) && !is_canceled() {
                        let share_count = local_stack.len() / 2;
                        offload.clear();
                        for _ in 0..share_count {
                            if let Some(p) = local_stack.pop() {
                                offload.push(WalkJob {
                                    node_id: 0,
                                    path_no_nul: p,
                                });
                            }
                        }
                        if !offload.is_empty() {
                            queue.push_batch(std::mem::take(&mut offload));
                        }
                    }
                }

                queue.complete_one();
            }

            total_bytes.fetch_add(lb, Ordering::Relaxed);
            total_files.fetch_add(lf, Ordering::Relaxed);
            total_dirs.fetch_add(ld, Ordering::Relaxed);
            hash_acc.fetch_xor(lh, Ordering::Relaxed);
            if inc != 0 {
                incomplete.store(1, Ordering::Relaxed);
            }
        }));
    }

    for h in handles {
        let _ = h.join();
    }

    res.total_bytes = total_bytes.load(Ordering::Relaxed);
    res.total_files = total_files.load(Ordering::Relaxed);
    res.total_dirs = total_dirs.load(Ordering::Relaxed);
    res.content_hash = hash_acc.load(Ordering::Relaxed);
    res.incomplete = incomplete.load(Ordering::Relaxed);
    res
}
