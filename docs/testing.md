# Testing

```bash
./build Test        # .NET tests: Ankit.Native.Tests and Ankit.Server.Tests
./build Test-Rslib  # Rust tests of the rslib bindings
./build Coverage    # .NET tests with code coverage
```

`Test` builds first; see [Building](building.md) for the targets.

## Sync server tests

`Ankit.Server.Tests` runs the sync service against a real Anki sync server in a container, started with
[Testcontainers](https://dotnet.testcontainers.org). They need Docker with Linux containers and are skipped without it;
on CI they run on Ubuntu only.

The sync server image is compiled from source at the Anki tag in `paket.dependencies`, from
`tests/Ankit.Server.Tests/SyncServer/Dockerfile`. The first build takes several minutes, later runs reuse Docker's
cache. Cargo is limited to 4 parallel jobs because one job per core runs out of memory in the Docker VM; set
`ANKIT_TESTS_CARGO_JOBS` to change that.

## Coverage

To get an HTML coverage report, install ReportGenerator and run it on the results:

```bash
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:coverage-results/**/coverage.cobertura.xml -targetdir:coverage-report -reporttypes:Html
```
