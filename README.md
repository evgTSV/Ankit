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
| `-f, --full-clean`           | Make `Clean` also remove restored and generated files (see [Targets](#targets)) |
| `-s, --single-target`        | Run only the given target, without its dependencies    |
| `--list`                     | List available targets                                 |
| `--no-logo`                  | Do not print the project name and version on start     |
| `-h, --help`                 | Show help                                              |

Examples:

```bash
./build                  # prepare, build and test everything in Release
./build Prepare          # restore packages and generate sources, once after cloning
./build -c Debug Build   # build in Debug
./build Run              # start the server in Docker
./build Prepare-Sources  # format sources and fix encodings before committing
./build Before-Push      # format, verify and test before pushing
./build -f Clean         # remove everything the build left behind, then run Prepare again
./build --list           # list targets
```

### Pipeline

The pipeline has three stages, each a target of its own. `All` runs the first two in order; `Run`,
`Prepare-Sources` and `Before-Push` are not part of it.

1. **`Prepare`** restores packages and tools and generates sources that are not committed: the typed C# rslib client
   (`src/Ankit.Native/Generated`) and the C header of the native library (`src/rslib-bindings/include/ankit_rslib.h`).
   Run it once after cloning, and again after restoring a different Anki version or changing the C ABI.
2. **`Build`** compiles the native rslib bindings and the solution; `Test` and `Test-Rslib` run the tests. Build
   targets do not run `Prepare` on their own, they fail with a hint to run it if its outputs are missing.
3. **`Run`** builds the `Ankit.Server` Docker image and starts it in the background with Docker Compose
   (`compose.yaml`); `Stop` stops it. Run it explicitly with `./build Run`.

To build the solution in an IDE, run `./build Prepare` and `./build Build-Rslib` first: the former generates the C#
client, without which `Ankit.Native` does not compile, the latter builds the native library its tests load.

### Targets

| Target                  | Description                                                       | Depends on                         |
|-------------------------|-------------------------------------------------------------------|------------------------------------|
| **Prepare**             |                                                                   |                                    |
| `Restore`               | Restore dotnet tools, Paket and NuGet packages                    |                                    |
| `Setup-Rslib`           | Provide `protoc` and prepare the Anki sources for building rslib  | (after `Restore`)                  |
| `Generate-Rslib-Client` | Generate the typed C# rslib client from Anki's descriptor set     | `Setup-Rslib`                      |
| `Generate-Rslib-Header` | Generate the C header of the rslib bindings with cbindgen         | (after `Restore`)                  |
| `Prepare`               | Run all of the above                                              | all of the above                   |
| **Build**               |                                                                   |                                    |
| `Clean`                 | Remove `bin` and `obj` directories; with `--full-clean`, also the outputs of Prepare and coverage results | (after `Prepare`) |
| `Build-Rslib`           | Build the native rslib bindings                                   | (after `Prepare`)                  |
| `Build-Solution`        | Build the solution                                                | (after `Clean`)                    |
| `Build`                 | Clean and build everything                                        | `Clean`, `Build-Rslib`, `Build-Solution` |
| `Test`                  | Run tests                                                         | `Build`                            |
| `Test-Rslib`            | Run the rslib bindings tests                                      | `Build-Rslib`                      |
| `Coverage`              | Run tests with code coverage, results go to `coverage-results/`  |                                    |
| **Run**                 |                                                                   |                                    |
| `Run`                   | Build the server image and start it with Docker Compose           |                                    |
| `Stop`                  | Stop the server started by `Run`                                  |                                    |
| **Other**               |                                                                   |                                    |
| `All`                   | Prepare, build and test everything                                | `Prepare`, `Build`, `Test`, `Test-Rslib` |
| `Info`                  | Print project and build information                               |                                    |
| `Format`                | Format sources with Fantomas                                      | `Restore`                          |
| `Verify-Encoding`       | Check that all repository files are UTF-8 without BOM             |                                    |
| `Remove-BOM`            | Strip UTF-8 BOM from repository files                             |                                    |
| `Prepare-Sources`       | Format sources and remove BOMs                                    | `Format`, `Remove-BOM` (only if any file has a BOM) |
| `Gen-Workflow`          | Generate the CI workflow with `Scripts/gen-workflow.fsx`          |                                    |
| `Verify-Workflow`       | Check that the CI workflow is up to date with `Scripts/gen-workflow.fsx` |                             |
| `Before-Push`           | Format, verify and test before pushing                            | `Prepare-Sources`, `Verify-Encoding`, `Verify-Workflow`, `Test` |

"(after X)" only orders the targets when both run, it does not run X.

`./build -f Clean` additionally removes `packages`, `paket-files`, `coverage-results` and, in `src/rslib-bindings`,
`target`, `tools` and `include`: everything Restore and Prepare produce. Run `./build Prepare` afterwards.

`Before-Push` runs `Test`, and with it `Build`, but not `Prepare`: run `Prepare` first on a fresh clone.

To get an HTML coverage report, install ReportGenerator and run it on the results:

```bash
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:coverage-results/**/coverage.cobertura.xml -targetdir:coverage-report -reporttypes:Html
```

See [`src/rslib-bindings/README.md`](src/rslib-bindings/README.md) for the native bridge between `Ankit.Native` and rslib.

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
