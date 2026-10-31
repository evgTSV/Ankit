# Server

`Ankit.Server` applies changes to its local collection immediately and pushes them to the sync server in batches: a
sync runs once `SyncDebounceTime` has passed since the first change of a batch.

## Configuration

Settings come from `src/Ankit.Server/appsettings.json` and can be overridden with environment variables, using `__`
between section and key (for example `SyncServer__Password`).

| Setting                    | Default                 | Description                                              |
|----------------------------|-------------------------|----------------------------------------------------------|
| `Urls`                     | `http://0.0.0.0:5000`   | Addresses the server listens on                          |
| `Ankit:CollectionPath`     | `data/collection.anki2` | Collection file, relative to the working directory; the media folder and database are kept next to it |
| `Ankit:SyncDebounceTime`   | `5000`                  | Milliseconds to collect changes before syncing them      |
| `Ankit:ApiKeys`            | (empty)                 | Clients allowed to use the API: `[{ "Name", "Hash" }]`, see [Authentication](#authentication) |
| `Ankit:DisableAuth`        | `false`                 | Let anyone use the API without a key, for local development only |
| `SyncServer:Host`          | `localhost`             | Sync server host                                         |
| `SyncServer:Port`          | `8080`                  | Sync server port                                         |
| `SyncServer:UseHttps`      | `false`                 | Connect to the sync server over HTTPS                    |
| `SyncServer:Username`      | (empty)                 | Sync account, set it through the environment             |
| `SyncServer:Password`      | (empty)                 | Sync account password, set it through the environment    |

In Docker the collection lives in `/app/data`, mount a volume there to keep it when the container is recreated.
`./build Run` (`docker compose`) passes the variables in a `.env` file in the repository root to the container.

## Authentication

Every endpoint except `GET /ping`, the OpenAPI description and Swagger UI requires an API key, sent as
`Authorization: Bearer <key>`. Requests without a valid key get `401` with a `WWW-Authenticate: Bearer` header.

Give each client its own key, so one can be revoked by removing its entry. Create a key with:

```bash
./build --key-name firefox-extension New-Api-Key   # build.cmd on Windows
```

It prints the key, to hand to the client, and the entry to configure. The server stores only the key's SHA-256 hash,
so the key can't be recovered from the configuration; create a new one if it's lost.

```json
"Ankit": {
  "ApiKeys": [
    { "Name": "firefox-extension", "Hash": "sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08" }
  ]
}
```

or `Ankit__ApiKeys__0__Name` and `Ankit__ApiKeys__0__Hash` (`1`, `2`, … for more keys) in the environment. The server
doesn't start without a key unless `Ankit:DisableAuth` is `true`, and it checks the entries at startup. Keys travel in
every request: outside localhost, serve the API over HTTPS, for example behind a reverse proxy.

In Swagger UI, use **Authorize** to enter a key; it is kept across page reloads.

## API

| Endpoint                | Body or query                                                    | Success                 |
|-------------------------|------------------------------------------------------------------|-------------------------|
| `GET /ping`             |                                                                  | `200`                   |
| `GET /decks`            |                                                                  | `200` `[{ "id", "name" }]` |
| `POST /decks`           | `{ "name": "Parent::Child" }`                                    | `201` `{ "id" }`        |
| `PUT /decks/{id}`       | `{ "name": "New::Name" }`                                        | `204`                   |
| `DELETE /decks/{id}`    |                                                                  | `204`                   |
| `GET /note-types`       |                                                                  | `200` `[{ "id", "name", "fields" }]` |
| `GET /notes?query=…`    | Anki search, e.g. `query=deck:Japanese tag:verb`; `deck:*` for all | `200` `[id, …]`       |
| `GET /notes/{id}`       |                                                                  | `200` `{ "id", "noteTypeId", "fields", "tags" }` |
| `POST /notes`           | `{ "deckId": 1, "noteTypeId": 1, "fields": ["front", "back"], "tags": ["optional"] }` | `201` `{ "id" }` |
| `PUT /notes/{id}`       | `{ "fields": ["front", "back"], "tags": ["optional"] }`          | `204`                   |
| `DELETE /notes/{id}`    |                                                                  | `204`                   |
| `POST /sync/full`       | `{ "direction": "upload" }` or `{ "direction": "download" }`     | `204`                   |

The OpenAPI description of the API is served at `GET /openapi/v1.json`, and Swagger UI at `/swagger` to browse and
try the endpoints. Union values such as `direction` are case-insensitive (`upload`, `UPLOAD`).

`fields` holds one value per field of the note type, in its order; `GET /note-types` lists them. `PUT /notes/{id}`
keeps the tags when `tags` is left out. Deleting a deck deletes its child decks and all of their cards; the default
deck stays: its cards are deleted and its name is reset. Deleting a note deletes its cards.

Changes answer once they are applied to the local collection, without waiting for the sync; `/sync/full` answers when
the full sync is done. Reads don't trigger a sync.

Errors are JSON with `code`, `message`, `traceId`, `timestamp` and `path`:

- `404` when the deck or note in the URL doesn't exist
- `422` with `fieldErrors` (`field`, `issue`, `rejectedValue`) when the body or query fails validation
- `422` without them when rslib rejects the command, for example a wrong number of fields, an unknown or filtered
  deck, an unknown note type or a malformed search
- `400` when the body is not valid JSON or an id in the URL is not a number
- `401` when the API key is missing or not configured
- `502` when a full sync fails
- `503` when the server shuts down before applying the command

## First sync

A collection that has never been synced with the account can't be synced normally: after the first change the
background sync logs `FullSyncRequired` and keeps failing. Resolve it once with `POST /sync/full`:

- `upload` replaces the server's collection with the local one, use it for a new account
- `download` replaces the local collection with the server's, use it to join an account that already has data
