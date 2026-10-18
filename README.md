# Ankit

---

## Building

### Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) (see `global.json`)

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
| `Build`           | Build the solution                                                     | `Restore`, `Clean`      |
| `Test`            | Run tests                                                              | `Build`                 |
| `All`             | Build and test everything                                              | `Test`                  |
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
