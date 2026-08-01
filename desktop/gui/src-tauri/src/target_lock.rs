/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

#[derive(Debug)]
pub(crate) struct TargetFileLock {
    path: PathBuf,
    #[cfg(windows)]
    handle: *mut std::ffi::c_void,
    #[cfg(not(windows))]
    _file: fs::File,
}

impl TargetFileLock {
    pub(crate) fn acquire(path: &Path) -> io::Result<Self> {
        #[cfg(windows)]
        {
            use std::os::windows::ffi::OsStrExt;

            const GENERIC_READ: u32 = 0x8000_0000;
            const GENERIC_WRITE: u32 = 0x4000_0000;
            const OPEN_ALWAYS: u32 = 4;
            const FILE_ATTRIBUTE_NORMAL: u32 = 0x80;

            #[link(name = "Kernel32")]
            extern "system" {
                fn CreateFileW(
                    file_name: *const u16,
                    desired_access: u32,
                    share_mode: u32,
                    security_attributes: *mut std::ffi::c_void,
                    creation_disposition: u32,
                    flags_and_attributes: u32,
                    template_file: *mut std::ffi::c_void,
                ) -> *mut std::ffi::c_void;
            }

            let wide = path
                .as_os_str()
                .encode_wide()
                .chain(std::iter::once(0))
                .collect::<Vec<_>>();
            let handle = unsafe {
                CreateFileW(
                    wide.as_ptr(),
                    GENERIC_READ | GENERIC_WRITE,
                    0,
                    std::ptr::null_mut(),
                    OPEN_ALWAYS,
                    FILE_ATTRIBUTE_NORMAL,
                    std::ptr::null_mut(),
                )
            };
            if handle as isize == -1 {
                let error = io::Error::last_os_error();
                if matches!(error.raw_os_error(), Some(32) | Some(33)) {
                    return Err(io::Error::new(io::ErrorKind::WouldBlock, error));
                }
                return Err(error);
            }
            return Ok(Self {
                path: path.to_path_buf(),
                handle,
            });
        }

        #[cfg(not(windows))]
        {
            let file = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(path)
                .map_err(|error| {
                    if error.kind() == io::ErrorKind::AlreadyExists {
                        io::Error::new(io::ErrorKind::WouldBlock, error)
                    } else {
                        error
                    }
                })?;
            Ok(Self {
                path: path.to_path_buf(),
                _file: file,
            })
        }
    }
}

impl Drop for TargetFileLock {
    fn drop(&mut self) {
        #[cfg(windows)]
        unsafe {
            #[link(name = "Kernel32")]
            extern "system" {
                fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
            }
            let _ = CloseHandle(self.handle);
        }
        let _ = fs::remove_file(&self.path);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn target_lock_excludes_a_second_writer_and_releases_on_drop() {
        let root = std::env::temp_dir().join(format!(
            "demotracer-target-lock-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("target.lock");

        let first = TargetFileLock::acquire(&path).unwrap();
        assert_eq!(
            TargetFileLock::acquire(&path).unwrap_err().kind(),
            io::ErrorKind::WouldBlock
        );
        drop(first);
        drop(TargetFileLock::acquire(&path).unwrap());

        fs::remove_dir_all(root).unwrap();
    }
}
