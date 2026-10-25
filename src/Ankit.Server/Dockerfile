# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
WORKDIR /app
# The collection lives here, see docs/server.md
RUN mkdir -p /app/data && chown $APP_UID /app/data
USER $APP_UID
EXPOSE 5000

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
# gcompat and libstdc++ run the glibc protoc of Grpc.Tools, which rslib and Ankit.Native generate code with
RUN apk add --no-cache build-base curl gcompat libstdc++

ENV RUSTUP_HOME=/usr/local/rustup \
    CARGO_HOME=/usr/local/cargo \
    PATH=/usr/local/cargo/bin:$PATH
RUN curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain none

WORKDIR /src/src/rslib-bindings
COPY src/rslib-bindings/rust-toolchain.toml .
RUN rustup toolchain install

# Packages and Anki sources, in their own layer so that source changes don't download them again
WORKDIR /src
COPY .config/ .config/
COPY .paket/ .paket/
COPY global.json paket.dependencies paket.lock ./
RUN dotnet tool restore && dotnet paket restore

COPY . .

# The same targets CI runs, the native library and the generated client must exist before publishing
RUN --mount=type=cache,id=ankit-cargo-registry,target=/usr/local/cargo/registry \
    --mount=type=cache,id=ankit-rslib-target-musl,target=/src/src/rslib-bindings/target \
    for target in Restore Setup-Rslib Generate-Rslib-Client Build-Rslib; do \
        dotnet fsi Scripts/build.fsx -- --no-logo -c $BUILD_CONFIGURATION --single-target $target || exit 1; \
    done \
    && dotnet publish src/Ankit.Server/Ankit.Server.fsproj -c $BUILD_CONFIGURATION -o /app/publish -p:UseAppHost=false \
    && test -f /app/publish/libankit_rslib.so

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Ankit.Server.dll"]
