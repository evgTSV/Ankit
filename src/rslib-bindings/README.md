# rslib-bindings

`ankit_rslib` is a native library (`ankit_rslib.dll` / `libankit_rslib.so` / `libankit_rslib.dylib`) that exposes
[Anki's](https://github.com/ankitects/anki) Rust backend, `rslib`, to `Ankit.Native` through a small C ABI.

It does what Anki's own Python bridge (`pylib/rsbridge`) does: requests and responses cross the boundary as
protobuf bytes, addressed by a `(service, method)` index pair. The bridge itself knows nothing about individual
messages, so every rslib service is available without any changes here.

## API

See [`include/ankit_rslib.h`](include/ankit_rslib.h) and the doc comments in [`src/lib.rs`](src/lib.rs).

| Function                   | Description                                                             |
|----------------------------|-------------------------------------------------------------------------|
| `ankit_backend_open`       | Creates a backend from an encoded `anki.backend.BackendInit`            |
| `ankit_backend_command`    | Calls a service method: encoded request in, encoded response out        |
| `ankit_backend_db_command` | Runs a raw database command (JSON in, JSON out)                         |
| `ankit_backend_close`      | Releases a backend                                                      |
| `ankit_buffer_free`        | Releases a buffer returned by any of the functions above                |
| `ankit_initialize_logging` | Sets up rslib's logger (stderr, optionally a file)                      |
| `ankit_buildhash`          | Build hash of the linked rslib                                          |

Every fallible function returns a status code and writes its output to an `AnkitBuffer`:

| Status                   | Buffer contents                                                         |
|--------------------------|-------------------------------------------------------------------------|
| `ANKIT_OK` (0)           | Encoded response message                                                |
| `ANKIT_BACKEND_ERROR` (1)| Encoded `anki.backend.BackendError` (UTF-8 text for open/logging)       |
| `ANKIT_PANIC` (2)        | UTF-8 panic message; panics never unwind into the caller                |
| `ANKIT_INVALID_ARGUMENT` (3) | UTF-8 message about a null pointer or invalid UTF-8                 |

### Service and method indices

Indices are not the plain order of `service`/`rpc` declarations in the `.proto` files: rslib merges each
`FooService` into `BackendFooService` (see `anki_proto_gen::get_services`). The build writes Anki's descriptor set
to `target/anki_descriptors.bin`, and `csharp-gen` turns it into the typed C# client, so the indices always match
the linked rslib. `tests/ffi.rs` shows the lookup.

## Using it from C#

`Ankit.Native` wraps the library with `LibraryImport` (`Interop/NativeMethods.cs`). Its `RslibBackend` class has
one method per rslib backend method, taking and returning the protobuf classes Grpc.Tools generates from Anki's
`.proto` files:

```csharp
using var backend = RslibBackend.Open("en");
backend.OpenCollection(new OpenCollectionRequest { CollectionPath = path, MediaFolderPath = media, MediaDbPath = mediaDb });
var deck = backend.NewDeck();
deck.Name = "Japanese::Kanji";
backend.AddDeck(deck);
backend.CloseCollection(new CloseCollectionRequest());
```

Errors reported by rslib are thrown as `BackendException`, with the decoded `BackendError` in `Error`; panics and
invalid calls as `RslibException`.

Those methods live in `src/Ankit.Native/Generated/RslibBackend.Methods.g.cs`. The file is not committed: its service and
method indices must match the linked rslib, so it is generated from the descriptor set of the same build. Run
`Build-Rslib` once after cloning, and again whenever the Anki version changes, before building the solution in an
IDE; until then `Ankit.Native` does not compile. The FAKE `Build` target does this on its own. To regenerate the
file by hand, after rslib has been built:

```bash
cargo run -p csharp-gen -- ../Ankit.Native/Generated/RslibBackend.Methods.g.cs
```

`Ankit.Native` copies the native library from `target/debug` or `target/release` (matching its own build
configuration) to its output, and warns if it has not been built.

## Building

Prerequisites: Rust 1.97.1 (picked up from `rust-toolchain.toml`) and, on Windows, the MSVC build tools.

The Anki sources come from Paket (`group Anki` in `paket.dependencies`), so restore first. Then, from the
repository root:

```bash
./build Setup-Rslib  # provide protoc, prepare the Anki sources
./build Build-Rslib  # cargo build, in Release unless -c Debug is given
./build Test-Rslib   # cargo test
```

`Setup-Rslib` copies the `protoc` that ships with Grpc.Tools (the one `Ankit.Native` generates its C# classes with)
into `tools/protoc`, and creates the empty translation folders `ftl/core-repo/core` and `ftl/qt-repo/desktop` in the Anki sources: they are git submodules and are missing
from the archive Paket downloads. As a result, rslib is built with English strings only.

After setup, plain `cargo build` / `cargo test` in this folder work too.

## License

AGPL-3.0-or-later, like the rest of Ankit (see the repository [`README.md`](../../README.md#license) and
[`LICENSE`](../../LICENSE)). This is required rather than chosen: the compiled `ankit_rslib` library statically links
Anki's rslib, which is licensed under AGPL-3.0-or-later.
