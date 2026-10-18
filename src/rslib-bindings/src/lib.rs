//! C ABI over Anki's rslib backend for Ankit.Native.
//!
//! The bridge is deliberately thin: every rslib call is a protobuf-encoded
//! request addressed by `(service, method)` indices, answered by a
//! protobuf-encoded response. On failure rslib returns an encoded
//! `anki.backend.BackendError`. Service and method indices are those produced
//! by `anki_proto_gen::get_services`, the same ones Anki's Python and
//! TypeScript clients use.
//!
//! Memory ownership rules:
//! - input buffers are borrowed for the duration of the call only;
//! - every [`AnkitBuffer`] handed out must be released with
//!   [`ankit_buffer_free`];
//! - a backend handle must be released with [`ankit_backend_close`].

use std::any::Any;
use std::ffi::c_char;
use std::ffi::CString;
use std::panic;
use std::panic::AssertUnwindSafe;
use std::ptr;
use std::slice;
use std::sync::OnceLock;

use anki::backend::init_backend;
use anki::backend::Backend;
use anki::log::set_global_logger;

/// Call completed, the output buffer holds the response.
pub const ANKIT_OK: i32 = 0;
/// rslib reported an error, the output buffer holds an encoded
/// `anki.backend.BackendError` (for [`ankit_backend_open`] and
/// [`ankit_initialize_logging`]: a UTF-8 message).
pub const ANKIT_BACKEND_ERROR: i32 = 1;
/// rslib panicked, the output buffer holds a UTF-8 message.
pub const ANKIT_PANIC: i32 = 2;
/// The caller passed a null pointer where one is not allowed, or invalid
/// UTF-8. The output buffer, if any, holds a UTF-8 message.
pub const ANKIT_INVALID_ARGUMENT: i32 = 3;

/// A byte buffer allocated by this library.
#[repr(C)]
pub struct AnkitBuffer {
    pub data: *mut u8,
    pub len: usize,
}

impl AnkitBuffer {
    const EMPTY: AnkitBuffer = AnkitBuffer {
        data: ptr::null_mut(),
        len: 0,
    };

    fn from_vec(bytes: Vec<u8>) -> Self {
        if bytes.is_empty() {
            return Self::EMPTY;
        }
        let boxed = bytes.into_boxed_slice();
        let len = boxed.len();
        AnkitBuffer {
            data: Box::into_raw(boxed).cast(),
            len,
        }
    }
}

/// Opaque handle to an rslib backend. It is safe to use from several threads
/// at once; rslib serializes access to the open collection internally.
pub struct AnkitBackend {
    backend: Backend,
}

type CallResult = Result<Vec<u8>, (i32, Vec<u8>)>;

fn invalid_argument(message: &str) -> (i32, Vec<u8>) {
    (ANKIT_INVALID_ARGUMENT, message.as_bytes().to_vec())
}

fn panic_message(payload: Box<dyn Any + Send>) -> Vec<u8> {
    let message = if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_string()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "rslib panicked".to_string()
    };
    message.into_bytes()
}

/// Runs `call` without letting a panic unwind into the caller, and stores its
/// output (or error) in `out`.
unsafe fn guarded(out: *mut AnkitBuffer, call: impl FnOnce() -> CallResult) -> i32 {
    if out.is_null() {
        return ANKIT_INVALID_ARGUMENT;
    }
    out.write(AnkitBuffer::EMPTY);

    let (status, bytes) = match panic::catch_unwind(AssertUnwindSafe(call)) {
        Ok(Ok(bytes)) => (ANKIT_OK, bytes),
        Ok(Err((status, bytes))) => (status, bytes),
        Err(payload) => (ANKIT_PANIC, panic_message(payload)),
    };
    out.write(AnkitBuffer::from_vec(bytes));
    status
}

unsafe fn input<'a>(data: *const u8, len: usize) -> Result<&'a [u8], (i32, Vec<u8>)> {
    if data.is_null() {
        if len == 0 {
            Ok(&[])
        } else {
            Err(invalid_argument("input is null but its length is not zero"))
        }
    } else {
        Ok(slice::from_raw_parts(data, len))
    }
}

unsafe fn backend<'a>(handle: *const AnkitBackend) -> Result<&'a Backend, (i32, Vec<u8>)> {
    handle
        .as_ref()
        .map(|h| &h.backend)
        .ok_or_else(|| invalid_argument("backend handle is null"))
}

/// Returns the build hash of the linked rslib as a static, NUL-terminated
/// string. The caller must not free it.
#[no_mangle]
pub extern "C" fn ankit_buildhash() -> *const c_char {
    static BUILDHASH: OnceLock<CString> = OnceLock::new();
    BUILDHASH
        .get_or_init(|| CString::new(anki::version::buildhash()).unwrap_or_default())
        .as_ptr()
}

/// Sets up rslib's global logger. `path` is a UTF-8 file path (not
/// NUL-terminated); pass null to log to stderr only. Can be called once per
/// process.
///
/// # Safety
/// `path` must be null or point to `path_len` readable bytes, and `out_error`
/// must point to writable memory for an [`AnkitBuffer`].
#[no_mangle]
pub unsafe extern "C" fn ankit_initialize_logging(
    path: *const u8,
    path_len: usize,
    out_error: *mut AnkitBuffer,
) -> i32 {
    guarded(out_error, || {
        let path = if path.is_null() {
            None
        } else {
            let bytes = input(path, path_len)?;
            Some(
                std::str::from_utf8(bytes)
                    .map_err(|_| invalid_argument("path is not valid UTF-8"))?,
            )
        };
        set_global_logger(path)
            .map(|_| Vec::new())
            .map_err(|e| (ANKIT_BACKEND_ERROR, e.to_string().into_bytes()))
    })
}

/// Creates a backend from an encoded `anki.backend.BackendInit` message.
///
/// On success writes the handle to `out_backend`. On failure `out_error`
/// receives a UTF-8 message.
///
/// # Safety
/// `init` must be null (with `init_len == 0`) or point to `init_len` readable
/// bytes; `out_backend` and `out_error` must point to writable memory.
#[no_mangle]
pub unsafe extern "C" fn ankit_backend_open(
    init: *const u8,
    init_len: usize,
    out_backend: *mut *mut AnkitBackend,
    out_error: *mut AnkitBuffer,
) -> i32 {
    guarded(out_error, || {
        if out_backend.is_null() {
            return Err(invalid_argument("out_backend is null"));
        }
        out_backend.write(ptr::null_mut());

        let init = input(init, init_len)?;
        let backend = init_backend(init).map_err(|e| (ANKIT_BACKEND_ERROR, e.into_bytes()))?;
        out_backend.write(Box::into_raw(Box::new(AnkitBackend { backend })));
        Ok(Vec::new())
    })
}

/// Releases a backend created by [`ankit_backend_open`]. Any open collection
/// should be closed beforehand via `CollectionService.CloseCollection`.
/// Passing null is a no-op.
///
/// # Safety
/// `handle` must come from [`ankit_backend_open`], must not be used by any
/// other thread during or after this call, and must not be closed twice.
#[no_mangle]
pub unsafe extern "C" fn ankit_backend_close(handle: *mut AnkitBackend) {
    if !handle.is_null() {
        let _ = panic::catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(handle))));
    }
}

/// Invokes an rslib service method.
///
/// `input` is the encoded request message. On [`ANKIT_OK`] `out` holds the
/// encoded response, on [`ANKIT_BACKEND_ERROR`] an encoded
/// `anki.backend.BackendError`.
///
/// # Safety
/// `handle` must be a live backend handle; `input` must be null (with
/// `input_len == 0`) or point to `input_len` readable bytes; `out` must point
/// to writable memory for an [`AnkitBuffer`].
#[no_mangle]
pub unsafe extern "C" fn ankit_backend_command(
    handle: *const AnkitBackend,
    service: u32,
    method: u32,
    input_data: *const u8,
    input_len: usize,
    out: *mut AnkitBuffer,
) -> i32 {
    guarded(out, || {
        let backend = backend(handle)?;
        let input = input(input_data, input_len)?;
        backend
            .run_service_method(service, method, input)
            .map_err(|err| (ANKIT_BACKEND_ERROR, err))
    })
}

/// Runs a raw database command against the open collection. Unlike
/// [`ankit_backend_command`], request and response are JSON, mirroring
/// rslib's `db_command`; errors are still an encoded
/// `anki.backend.BackendError`.
///
/// # Safety
/// Same requirements as [`ankit_backend_command`].
#[no_mangle]
pub unsafe extern "C" fn ankit_backend_db_command(
    handle: *const AnkitBackend,
    input_data: *const u8,
    input_len: usize,
    out: *mut AnkitBuffer,
) -> i32 {
    guarded(out, || {
        let backend = backend(handle)?;
        let input = input(input_data, input_len)?;
        backend
            .run_db_command_bytes(input)
            .map_err(|err| (ANKIT_BACKEND_ERROR, err))
    })
}

/// Releases a buffer returned by this library. Passing an empty buffer is a
/// no-op.
///
/// # Safety
/// `buffer` must have been produced by this library and not freed before.
#[no_mangle]
pub unsafe extern "C" fn ankit_buffer_free(buffer: AnkitBuffer) {
    if !buffer.data.is_null() {
        drop(Box::from_raw(ptr::slice_from_raw_parts_mut(
            buffer.data,
            buffer.len,
        )));
    }
}
