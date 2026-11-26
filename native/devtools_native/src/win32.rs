#![allow(non_camel_case_types, non_snake_case, dead_code)]

use std::ffi::c_void;

pub type BOOL = i32;
pub type DWORD = u32;
pub type WORD = u16;
pub type BYTE = u8;
pub type HANDLE = *mut c_void;
pub type LPCWSTR = *const u16;
pub type LPWSTR = *mut u16;
pub type LPVOID = *mut c_void;
pub type LPCVOID = *const c_void;
pub type ULONG = u32;
pub type ULONGLONG = u64;
pub type LONGLONG = i64;
pub type LARGE_INTEGER = i64;

pub const INVALID_HANDLE_VALUE: HANDLE = -1isize as HANDLE;

pub const GENERIC_READ: DWORD = 0x8000_0000;
pub const DELETE: DWORD = 0x0001_0000;
pub const FILE_READ_ATTRIBUTES: DWORD = 0x0000_0080;

pub const FILE_SHARE_READ: DWORD = 0x0000_0001;
pub const FILE_SHARE_WRITE: DWORD = 0x0000_0002;
pub const FILE_SHARE_DELETE: DWORD = 0x0000_0004;
pub const FILE_SHARE_ALL: DWORD = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

pub const OPEN_EXISTING: DWORD = 3;

pub const FILE_ATTRIBUTE_READONLY: DWORD = 0x0000_0001;
pub const FILE_ATTRIBUTE_HIDDEN: DWORD = 0x0000_0002;
pub const FILE_ATTRIBUTE_SYSTEM: DWORD = 0x0000_0004;
pub const FILE_ATTRIBUTE_DIRECTORY: DWORD = 0x0000_0010;
pub const FILE_ATTRIBUTE_NORMAL: DWORD = 0x0000_0080;
pub const FILE_ATTRIBUTE_REPARSE_POINT: DWORD = 0x0000_0400;

pub const FILE_FLAG_OPEN_NO_RECALL: DWORD = 0x0010_0000;
pub const FILE_FLAG_OPEN_REPARSE_POINT: DWORD = 0x0020_0000;
pub const FILE_FLAG_BACKUP_SEMANTICS: DWORD = 0x0200_0000;
pub const FILE_FLAG_SEQUENTIAL_SCAN: DWORD = 0x0800_0000;
pub const FILE_FLAG_NO_BUFFERING: DWORD = 0x2000_0000;
pub const FILE_FLAG_OVERLAPPED: DWORD = 0x4000_0000;

pub const FIND_FIRST_EX_LARGE_FETCH: DWORD = 0x0000_0002;
pub const FIND_EX_INFO_BASIC: DWORD = 1;
pub const FIND_EX_SEARCH_NAME_MATCH: DWORD = 0;

pub const FILE_DISPOSITION_INFO_EX: DWORD = 21;
pub const FILE_DISPOSITION_FLAG_DELETE: DWORD = 0x0000_0001;
pub const FILE_DISPOSITION_FLAG_POSIX_SEMANTICS: DWORD = 0x0000_0002;
pub const FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE: DWORD = 0x0000_0010;

pub const ERROR_FILE_NOT_FOUND: DWORD = 2;
pub const ERROR_PATH_NOT_FOUND: DWORD = 3;
pub const ERROR_ACCESS_DENIED: DWORD = 5;
pub const ERROR_NO_MORE_FILES: DWORD = 18;
pub const ERROR_SHARING_VIOLATION: DWORD = 32;
pub const ERROR_DIR_NOT_EMPTY: DWORD = 145;

#[repr(C)]
pub struct FILETIME {
    pub dwLowDateTime: DWORD,
    pub dwHighDateTime: DWORD,
}

#[repr(C)]
pub struct WIN32_FIND_DATAW {
    pub dwFileAttributes: DWORD,
    pub ftCreationTime: FILETIME,
    pub ftLastAccessTime: FILETIME,
    pub ftLastWriteTime: FILETIME,
    pub nFileSizeHigh: DWORD,
    pub nFileSizeLow: DWORD,
    pub dwReserved0: DWORD,
    pub dwReserved1: DWORD,
    pub cFileName: [u16; 260],
    pub cAlternateFileName: [u16; 14],
}

#[repr(C)]
pub struct FILE_DISPOSITION_INFO_EX_STRUCT {
    pub Flags: DWORD,
}

#[repr(C)]
pub struct OVERLAPPED {
    pub Internal: usize,
    pub InternalHigh: usize,
    pub Offset: DWORD,
    pub OffsetHigh: DWORD,
    pub hEvent: HANDLE,
}

#[link(name = "kernel32")]
extern "system" {
    pub fn CreateFileW(
        lpFileName: LPCWSTR,
        dwDesiredAccess: DWORD,
        dwShareMode: DWORD,
        lpSecurityAttributes: LPVOID,
        dwCreationDisposition: DWORD,
        dwFlagsAndAttributes: DWORD,
        hTemplateFile: HANDLE,
    ) -> HANDLE;

    pub fn CloseHandle(hObject: HANDLE) -> BOOL;

    pub fn GetFileAttributesW(lpFileName: LPCWSTR) -> DWORD;

    pub fn SetFileAttributesW(lpFileName: LPCWSTR, dwFileAttributes: DWORD) -> BOOL;

    pub fn DeleteFileW(lpFileName: LPCWSTR) -> BOOL;

    pub fn RemoveDirectoryW(lpPathName: LPCWSTR) -> BOOL;

    pub fn SetFileInformationByHandle(
        hFile: HANDLE,
        FileInformationClass: DWORD,
        lpFileInformation: LPCVOID,
        dwBufferSize: DWORD,
    ) -> BOOL;

    pub fn FindFirstFileExW(
        lpFileName: LPCWSTR,
        fInfoLevelId: DWORD,
        lpFindFileData: *mut WIN32_FIND_DATAW,
        fSearchOp: DWORD,
        lpSearchFilter: LPVOID,
        dwAdditionalFlags: DWORD,
    ) -> HANDLE;

    pub fn FindNextFileW(hFindFile: HANDLE, lpFindFileData: *mut WIN32_FIND_DATAW) -> BOOL;

    pub fn FindClose(hFindFile: HANDLE) -> BOOL;

    pub fn ReadFile(
        hFile: HANDLE,
        lpBuffer: LPVOID,
        nNumberOfBytesToRead: DWORD,
        lpNumberOfBytesRead: *mut DWORD,
        lpOverlapped: *mut OVERLAPPED,
    ) -> BOOL;

    pub fn GetLastError() -> DWORD;
}

/// Converts a Rust UTF-16 slice (no trailing NUL) to a `\\?\` extended-length NUL-terminated path.
pub fn to_extended_wide(path_utf16: &[u16]) -> Vec<u16> {
    // Trim trailing backslashes except drive root like `C:\`.
    let mut slice = path_utf16;
    while slice.len() > 3 && (slice[slice.len() - 1] == b'\\' as u16 || slice[slice.len() - 1] == b'/' as u16) {
        slice = &slice[..slice.len() - 1];
    }

    let has_prefix = slice.len() >= 4
        && slice[0] == b'\\' as u16
        && slice[1] == b'\\' as u16
        && (slice[2] == b'?' as u16 || slice[2] == b'.' as u16)
        && slice[3] == b'\\' as u16;

    let mut out = Vec::with_capacity(slice.len() + 10);
    if !has_prefix {
        if slice.len() >= 2 && slice[0] == b'\\' as u16 && slice[1] == b'\\' as u16 {
            // UNC path \\server\share -> \\?\UNC\server\share
            out.extend_from_slice(&[
                b'\\' as u16,
                b'\\' as u16,
                b'?' as u16,
                b'\\' as u16,
                b'U' as u16,
                b'N' as u16,
                b'C' as u16,
                b'\\' as u16,
            ]);
            slice = &slice[2..];
        } else {
            out.extend_from_slice(&[b'\\' as u16, b'\\' as u16, b'?' as u16, b'\\' as u16]);
        }
    }

    for &ch in slice {
        out.push(if ch == b'/' as u16 { b'\\' as u16 } else { ch });
    }
    out.push(0);
    out
}

/// Joins a parent extended wide path (without trailing NUL) and a child UTF-16 name into a NUL-terminated buffer.
pub fn join_wide_into(parent: &[u16], child: &[u16], out: &mut Vec<u16>) {
    out.clear();
    out.extend_from_slice(parent);
    if !out.ends_with(&[b'\\' as u16]) {
        out.push(b'\\' as u16);
    }
    out.extend_from_slice(child);
    out.push(0);
}

pub fn wide_name_slice(buf: &[u16; 260]) -> &[u16] {
    let mut len = 0;
    while len < buf.len() && buf[len] != 0 {
        len += 1;
    }
    &buf[..len]
}

#[inline]
pub fn is_dot_or_dotdot(name: &[u16]) -> bool {
    (name.len() == 1 && name[0] == b'.' as u16)
        || (name.len() == 2 && name[0] == b'.' as u16 && name[1] == b'.' as u16)
}
