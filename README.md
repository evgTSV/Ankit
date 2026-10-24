# Ankit

An HTTP server that adds decks and notes to an [Anki](https://apps.ankiweb.net) collection and keeps the collection in
sync with an Anki sync server.

---

## Quick start

You need the [.NET SDK 10.0](https://dotnet.microsoft.com/download) and [Rust](https://rustup.rs) 1.97.1 (on Windows
also the MSVC build tools). The build is driven by `./build` (`build.cmd` on Windows):

```bash
./build      # prepare, build and test everything
./build Run  # start the server in Docker on port 5000
```

Set the sync account before running the server, for example with `SyncServer__Username` and `SyncServer__Password`.
On a fresh account, call `POST /sync/full` once to start syncing, see [First sync](docs/server.md#first-sync).

## Documentation

- [Building](docs/building.md): prerequisites, build options, pipeline and targets
- [Server](docs/server.md): configuration, HTTP API, errors and the first sync
- [Testing](docs/testing.md): test projects, the sync server container and coverage
- [rslib bindings](src/rslib-bindings/README.md): the native bridge between `Ankit.Native` and Anki's rslib

## License

Copyright (C) 2026 Evgenii Tsvetkov

Ankit is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public
License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
version. See [`LICENSE`](LICENSE) for the full text.

Ankit is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty
of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

The license follows from [Anki](https://github.com/ankitects/anki): `rslib-bindings` links Anki's rslib, which is
licensed under AGPL-3.0-or-later (with portions under BSD-3-Clause, see Anki's `LICENSE` and `CONTRIBUTORS`).

Because of the AGPL, if you run a modified version of Ankit as a network service, for example `Ankit.Server`, you must
offer its users the corresponding source code of that version (section 13 of the license).
