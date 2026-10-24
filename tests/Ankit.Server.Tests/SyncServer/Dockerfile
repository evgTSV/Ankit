# Anki's example sync server image (docs/syncserver in the Anki repo), with a limit on parallel
# compile jobs: by default cargo runs one rustc per core and runs out of memory on big machines
FROM rust:1.97.1-alpine3.23 AS builder

ARG ANKI_VERSION
ARG CARGO_BUILD_JOBS=4

RUN apk update && apk add --no-cache build-base protobuf && rm -rf /var/cache/apk/*

RUN CARGO_BUILD_JOBS=${CARGO_BUILD_JOBS} cargo install --git https://github.com/ankitects/anki.git \
--tag ${ANKI_VERSION} \
--root /anki-server  \
--locked \
anki-sync-server

FROM alpine:3.23

ENV PUID=1000
ENV PGID=1000

COPY --from=builder /anki-server/bin/anki-sync-server /usr/local/bin/anki-sync-server

RUN apk update && apk add --no-cache bash su-exec && rm -rf /var/cache/apk/*

EXPOSE 8080

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
CMD ["anki-sync-server"]

VOLUME /anki_data
