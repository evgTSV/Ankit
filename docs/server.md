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
| `SyncServer:Host`          | `localhost`             | Sync server host                                         |
| `SyncServer:Port`          | `8080`                  | Sync server port                                         |
| `SyncServer:UseHttps`      | `false`                 | Connect to the sync server over HTTPS                    |
| `SyncServer:Username`      | (empty)                 | Sync account, set it through the environment             |
| `SyncServer:Password`      | (empty)                 | Sync account password, set it through the environment    |

In Docker the collection lives in `/app/data`, mount a volume there to keep it when the container is recreated.

## API

| Endpoint          | Body                                                             | Success |
|-------------------|------------------------------------------------------------------|---------|
| `GET /ping`       |                                                                  | `200`   |
| `POST /decks`     | `{ "name": "Parent::Child" }`                                    | `204`   |
| `POST /notes`     | `{ "deckId": 1, "noteTypeId": 1, "fields": ["front", "back"], "tags": ["optional"] }` | `204` |
| `POST /sync/full` | `{ "direction": "upload" }` or `{ "direction": "download" }`     | `204`   |

`fields` holds one value per field of the note type, in its order. Deck and note commands answer once they are applied
to the local collection, without waiting for the sync; `/sync/full` answers when the full sync is done.

Errors are JSON with `code`, `message`, `traceId`, `timestamp` and `path`:

- `422` with `fieldErrors` (`field`, `issue`, `rejectedValue`) when the body fails validation
- `422` without them when rslib rejects the command, for example a wrong number of fields or an unknown deck
- `400` when the body is not valid JSON
- `502` when a full sync fails
- `503` when the server shuts down before applying the command

## First sync

A collection that has never been synced with the account can't be synced normally: after the first change the
background sync logs `FullSyncRequired` and keeps failing. Resolve it once with `POST /sync/full`:

- `upload` replaces the server's collection with the local one, use it for a new account
- `download` replaces the local collection with the server's, use it to join an account that already has data
