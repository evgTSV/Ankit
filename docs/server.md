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
| `Llm:Provider`             | (empty)                 | Base URL of an OpenAI-compatible API, empty for OpenAI   |
| `Llm:ApiKey`               | (empty)                 | Key for that API, set it through the environment; local servers need none |
| `Llm:Model`                | (empty)                 | Model id; card suggestions are off while it's empty      |
| `Llm:ResponseFormat`       | `JsonSchema`            | `JsonSchema`, `JsonObject` or `Prompt`, see [Card suggestions](#card-suggestions) |
| `Llm:TimeoutSeconds`       | `60`                    | How long to wait for the model                           |
| `Llm:Module`               | `Language`              | Suggestion module used when a request names none, see [Modules and styles](#modules-and-styles) |
| `Llm:Modules:Language:NativeLanguage` | (empty)      | The learner's native language, e.g. `Russian`, for hints and explanations |
| `Llm:Instructions`         | `[]`                    | Rules added to every prompt, e.g. `["Explain in Russian", "Keep cards short"]`; `Llm__Instructions__0`, `__1`, … in the environment |

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
| `GET /note-types`       |                                                                  | `200` `[{ "id", "name", "fields", "isCloze" }]` |
| `GET /notes?query=…`    | Anki search, e.g. `query=deck:Japanese tag:verb`; `deck:*` for all | `200` `[id, …]`       |
| `GET /notes/{id}`       |                                                                  | `200` `{ "id", "noteTypeId", "fields", "tags" }` |
| `POST /notes`           | `{ "deckId": 1, "noteTypeId": 1, "fields": ["front", "back"], "tags": ["optional"] }` | `201` `{ "id" }` |
| `GET /suggestion-modules` |                                                                | `200` `[{ "id", "description", "isDefault", "styles" }]` |
| `POST /notes/suggestions` | `{ "term", "context", "noteTypeId", … }`, see [Card suggestions](#card-suggestions) | `200` `{ "notes": [{ "fields" }], "existingNoteIds" }` |
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
- `502` when a full sync fails, or the LLM server fails or answers with nothing usable
- `503` when the server shuts down before applying the command, or card suggestions are off

## Card suggestions

`POST /notes/suggestions` drafts notes about a term with an LLM. It takes the context the term was found in into
account, for example the sentence or paragraph, so the cards are about the sense meant there. Nothing is added to the
collection: the client shows the drafts, the user edits or picks them, and adds them with `POST /notes`.

```json
{
  "term": "bank",
  "context": "We had a picnic on the bank of the river.",
  "source": "A walk in the park",
  "noteTypeId": 1700000000000,
  "deckId": 1,
  "count": 2,
  "module": "language",
  "style": "guided_cloze",
  "instructions": ["add the pronunciation", "no idioms"]
}
```

Only `term` and `noteTypeId` are required. The answer has one value per field of the note type in its order, and the
ids of notes that already contain the term:

```json
{
  "noteTypeId": 1700000000000,
  "notes": [{ "fields": ["A picnic on the {{c1::bank}} of the river", "the land beside a river"] }],
  "existingNoteIds": [1700000000123]
}
```

### Modules and styles

What the notes are about comes from a module, how each one is written from a style. `Llm:Module` picks the module
used by default, and a request can name another with `module`. `GET /suggestion-modules` lists the modules with their
styles; names are matched ignoring case and underscores (`guided_cloze`, `GuidedCloze`).

Every module has the plain styles, used when a request names no `style`:

- `CLOZE`, for a cloze note type (`isCloze` in `GET /note-types`): the term hidden in a sentence, `{{c1::bank}}`, with
  a hint only when it helps.
- `CARD`, for a note type with a front and a back, such as Basic: a question on the front, the answer on the back.

`AUTO` lets the model pick, note by note, one of the module's styles that fits the note type, or the plain one for any
other term. A style that doesn't fit the note type, or that the module doesn't have, is rejected with `422`. Drafts
that don't follow the style are dropped, and so are drafts with cloze deletions for a note type without cloze, Anki
would show them as they are.

| Module     | Styles                                       |
|------------|----------------------------------------------|
| `LANGUAGE` | `GUIDED_CLOZE`, `COLLOCATION_IN_CONTEXT` (the default module) |
| `GENERAL`  | only the plain ones, for any subject         |

`LANGUAGE`, learning a foreign language: the model is told the term is a word or phrase of the language being learned,
and the learner's native language from `Llm:Modules:Language:NativeLanguage`.

- `GUIDED_CLOZE`, for a cloze note type: for a high-value word (a verb, adjective or adverb) best learned in its
  natural context. Every deletion has a hint that identifies the word uniquely, so no close synonym fits it, in the
  native language or in simple words of the language being learned: `{{c1::bank::берег реки}}`. The back of the card
  (`Back Extra`) has the full sentence, the IPA of the word and its common collocation. Drafts with a deletion
  without a hint are dropped.
- `COLLOCATION_IN_CONTEXT`, for a note type with a front and a back: teaches which words the term combines with. The
  front has a natural sentence with a common collocation of the term in bold, `She <b>made a decision</b> quickly.`,
  the back the collocation, its meaning (in the native language or in simple words) and one more example sentence.
  Drafts without both a front and a back are dropped.

A new module is a `SuggestionModule` value (an id, a description, lines about the subject for the prompt, and its
styles) in `src/Ankit.Server/Modules/`, added to the list in `Program.fs`.

The prompt includes the notes that contain the term, so the model doesn't repeat them, and the three most recent
notes of the note type (in `deckId`, when given) as examples of your style. The context is treated as untrusted:
the model is told not to follow instructions in it, and fields keep only `<b>`, `<i>`, `<u>`, `<br>` and a few
similar tags without attributes, any other markup is escaped.

The server talks to any API that implements OpenAI's chat completions, so the model can come from a hosted provider
or run locally:

| Provider            | `Llm:Provider`                  |
|---------------------|---------------------------------|
| OpenAI              | (empty)                         |
| OpenRouter          | `https://openrouter.ai/api/v1`  |
| Ollama              | `http://localhost:11434/v1`, from the Docker container `http://host.docker.internal:11434/v1` |
| LM Studio           | `http://localhost:1234/v1`      |

`Llm:ResponseFormat` says how the model is held to the shape of the answer. `JsonSchema` (the default) sends a strict
JSON schema built from the note type's fields, which is the most reliable. For a server that rejects or ignores it,
use `JsonObject` (JSON mode without a schema) or `Prompt`, which only describes the shape in the prompt and also
accepts JSON wrapped in Markdown fences.

## First sync

A collection that has never been synced with the account can't be synced normally: after the first change the
background sync logs `FullSyncRequired` and keeps failing. Resolve it once with `POST /sync/full`:

- `upload` replaces the server's collection with the local one, use it for a new account
- `download` replaces the local collection with the server's, use it to join an account that already has data
