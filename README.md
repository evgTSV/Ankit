# Ankit

---

## Building

### Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) (see `global.json`)
- [Rust](https://rustup.rs) 1.97.1 for the rslib bindings (see `src/rslib-bindings/rust-toolchain.toml`), and on Windows
  the MSVC build tools

Local tools (Paket, Fantomas) are restored automatically by the `Restore` target.

### Usage

The build is driven by a [FAKE](https://fake.build) script, `Scripts/build.fsx`. Run it through a wrapper
in the repository root:

```bash
./build [options] [<target>]   # Linux / macOS / Git Bash
build.cmd [options] [<target>] # Windows
```

Without a target, `All` is run.

| Option                       | Description                                            |
|------------------------------|--------------------------------------------------------|
| `-c, --configuration <name>` | Build configuration: `Debug` or `Release` (default: `Release`) |
| `-p, --parallel <jobs>`      | Number of targets to run in parallel (default: `1`)    |
| `--list`                     | List available targets                                 |
| `-h, --help`                 | Show help                                              |

Examples:

```bash
./build                  # restore, clean, build and test in Release
./build -c Debug Build   # build in Debug
./build Prepare-Sources  # format sources and fix encodings before committing
./build --list           # list targets
```

### Targets

| Target            | Description                                                            | Depends on              |
|-------------------|------------------------------------------------------------------------|-------------------------|
| `Info`            | Print project and build information                                    |                         |
| `Restore`         | Restore dotnet tools, Paket and NuGet packages                         |                         |
| `Clean`           | Remove `bin` and `obj` directories                                     |                         |
| `Setup-Rslib`     | Provide `protoc` and prepare the Anki sources for building rslib       | (after `Restore`)       |
| `Build-Rslib`     | Build the native rslib bindings and regenerate the C# client           | `Setup-Rslib`           |
| `Test-Rslib`      | Run the rslib bindings tests                                           | `Build-Rslib`           |
| `Build`           | Build the solution                                                     | `Restore`, `Clean`, `Build-Rslib` |
| `Test`            | Run tests                                                              | `Build`                 |
| `All`             | Build and test everything                                              | `Test`, `Test-Rslib`    |
| `Coverage`        | Run tests with code coverage, results go to `coverage-results/`       |                         |
| `Format`          | Format sources with Fantomas                                           | `Restore`               |
| `Verify-Encoding` | Check that all repository files are UTF-8 without BOM                  |                         |
| `Remove-BOM`      | Strip UTF-8 BOM from repository files                                  |                         |
| `Prepare-Sources` | Format sources and remove BOMs                                         | `Format`, `Remove-BOM` (only if any file has a BOM) |

To get an HTML coverage report, install ReportGenerator and run it on the results:

```bash
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:coverage-results/**/coverage.cobertura.xml -targetdir:coverage-report -reporttypes:Html
```

See [`src/rslib-bindings/README.md`](src/rslib-bindings/README.md) for the native bridge between `Ankit.Native` and rslib.

After cloning, run `./build Setup-Rslib` and `./build Build-Rslib` once before building the solution in an IDE: they
build the native library and generate the typed C# rslib client (`src/Ankit.Native/Generated`), which is not committed.

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
