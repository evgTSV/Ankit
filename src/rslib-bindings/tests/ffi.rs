use std::ffi::CStr;
use std::path::PathBuf;
use std::ptr;

use anki_proto::backend::BackendError;
use anki_proto::backend::BackendInit;
use anki_proto::collection::CloseCollectionRequest;
use anki_proto::collection::OpenCollectionRequest;
use anki_proto_gen::get_services;
use ankit_rslib::*;
use prost::Message;
use prost_reflect::DescriptorPool;

/// Looks up `(service, method)` indices the same way a generated client does.
fn method_index(service: &str, method: &str) -> (u32, u32) {
    let path =
        std::env::var("DESCRIPTORS_BIN").expect("DESCRIPTORS_BIN is set in .cargo/config.toml");
    let pool = DescriptorPool::decode(std::fs::read(path).unwrap().as_ref()).unwrap();
    let (_, services) = get_services(&pool);
    let service = services
        .iter()
        .find(|s| s.name == service)
        .unwrap_or_else(|| panic!("no service {service}"));
    let method = service
        .trait_methods
        .iter()
        .chain(&service.delegating_methods)
        .find(|m| m.proto.name() == method)
        .unwrap_or_else(|| panic!("no method {method}"));
    (service.index as u32, method.index as u32)
}

fn take(buffer: AnkitBuffer) -> Vec<u8> {
    let bytes = if buffer.data.is_null() {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(buffer.data, buffer.len) }.to_vec()
    };
    unsafe { ankit_buffer_free(buffer) };
    bytes
}

fn open() -> *mut AnkitBackend {
    let init = BackendInit {
        preferred_langs: vec!["en".into()],
        locale_folder_path: String::new(),
        server: false,
    }
    .encode_to_vec();
    let mut handle = ptr::null_mut();
    let mut error = AnkitBuffer {
        data: ptr::null_mut(),
        len: 0,
    };
    let status = unsafe { ankit_backend_open(init.as_ptr(), init.len(), &mut handle, &mut error) };
    let error = take(error);
    assert_eq!(status, ANKIT_OK, "{}", String::from_utf8_lossy(&error));
    assert!(!handle.is_null());
    handle
}

fn command(
    handle: *mut AnkitBackend,
    (service, method): (u32, u32),
    input: &[u8],
) -> (i32, Vec<u8>) {
    let mut out = AnkitBuffer {
        data: ptr::null_mut(),
        len: 0,
    };
    let status = unsafe {
        ankit_backend_command(
            handle,
            service,
            method,
            input.as_ptr(),
            input.len(),
            &mut out,
        )
    };
    (status, take(out))
}

#[test]
fn buildhash_is_available() {
    // Empty for sources without Anki's out/buildhash file, as is the case with the Paket download
    let hash = unsafe { CStr::from_ptr(ankit_buildhash()) };
    assert!(hash.to_str().is_ok());
}

#[test]
fn opens_and_closes_collection() {
    let dir = std::env::temp_dir().join(format!("ankit-rslib-test-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = |name: &str| -> String { PathBuf::from(&dir).join(name).to_string_lossy().into() };

    let handle = open();

    let request = OpenCollectionRequest {
        collection_path: path("collection.anki2"),
        media_folder_path: path("collection.media"),
        media_db_path: path("collection.media.db"),
    };
    let (status, out) = command(
        handle,
        method_index("BackendCollectionService", "OpenCollection"),
        &request.encode_to_vec(),
    );
    assert_eq!(
        status,
        ANKIT_OK,
        "{:?}",
        BackendError::decode(out.as_slice())
    );

    let request = CloseCollectionRequest {
        downgrade_to_schema11: false,
    };
    let (status, out) = command(
        handle,
        method_index("BackendCollectionService", "CloseCollection"),
        &request.encode_to_vec(),
    );
    assert_eq!(
        status,
        ANKIT_OK,
        "{:?}",
        BackendError::decode(out.as_slice())
    );

    unsafe { ankit_backend_close(handle) };
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn backend_errors_are_encoded_protobuf() {
    let handle = open();
    let (status, out) = command(handle, (u32::MAX, 0), &[]);
    assert_eq!(status, ANKIT_BACKEND_ERROR);
    let error = BackendError::decode(out.as_slice()).unwrap();
    assert!(!error.message.is_empty());
    unsafe { ankit_backend_close(handle) };
}

#[test]
fn translation_index_finds_known_keys_only() {
    let (mut module, mut message) = (u32::MAX, u32::MAX);
    let key = "sync-wrong-pass";
    let status =
        unsafe { ankit_translation_index(key.as_ptr(), key.len(), &mut module, &mut message) };
    assert_eq!(status, ANKIT_OK);
    assert_ne!(module, u32::MAX);

    let key = "no-such-message";
    let status =
        unsafe { ankit_translation_index(key.as_ptr(), key.len(), &mut module, &mut message) };
    assert_eq!(status, ANKIT_INVALID_ARGUMENT);
}

#[test]
fn null_arguments_are_rejected() {
    let mut out = AnkitBuffer {
        data: ptr::null_mut(),
        len: 0,
    };
    let status = unsafe { ankit_backend_command(ptr::null(), 0, 0, ptr::null(), 0, &mut out) };
    assert_eq!(status, ANKIT_INVALID_ARGUMENT);
    assert_eq!(take(out), b"backend handle is null");
}
