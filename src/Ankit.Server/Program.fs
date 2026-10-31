module Ankit.Server.Main

open System
open System.IO
open System.Text.Json.Serialization
open System.Threading.Channels
open Anki.Collection
open Ankit.Server
open Ankit.Server.Auth
open Ankit.Server.Handlers.ModelBinder
open Ankit.Server.Env
open Ankit.Server.Services
open Ankit.Native
open Ankit.Server.Utils
open Ankit.Server.ApiDocs
open Ankit.Server.Dto
open Oxpecker
open Oxpecker.OpenApi
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

let endpoints = [
    GET [
        route "/ping" Handlers.ping
        |> describe "Ping" "Check that the server is up" None [ okText ]
    ]
    GET [
        route "/decks" Handlers.listDecks
        |> describe "ListDecks" "List all decks" None [ ok<DeckDto[]> ]

        route "/notes" <| bindOrDeclineQuery Handlers.searchNotes
        |> describe "SearchNotes" "Find the ids of the notes matching an Anki search" None [ ok<int64[]>; invalid ]
        |> withQuery "query" "Anki search, e.g. \"deck:Japanese tag:verb\"; \"deck:*\" matches all notes"

        routef "/notes/{%d}" Handlers.getNote
        |> describe "GetNote" "Get a note" None [ ok<NoteDto>; notFound ]

        route "/note-types" Handlers.listNoteTypes
        |> describe "ListNoteTypes" "List note types with their fields in order" None [ ok<NoteTypeDto[]> ]
    ]
    |> requireApiKey
    POST [
        route "/decks" <| bindOrDeclineJson Handlers.createDeck
        |> describe "CreateDeck" "Create a deck, \"::\" nests it under parent decks"
            (Some typeof<CreateDeckDto>) [ created; invalid; shuttingDown ]

        route "/notes" <| bindOrDeclineJson Handlers.addNote
        |> describe "AddNote" "Add a note to a normal deck"
            (Some typeof<AddNoteDto>) [ created; invalid; shuttingDown ]

        route "/sync/full" <| bindOrDeclineJson Handlers.fullSync
        |> describe "FullSync" "Replace the server's collection with the local one (upload) or the other way round (download)"
            (Some typeof<FullSyncDto>) [ noContent; invalid; syncFailed; shuttingDown ]
    ]
    |> requireApiKey
    PUT [
        routef "/decks/{%d}" (fun deckId -> bindOrDeclineJson (fun dto -> Handlers.renameDeck dto deckId))
        |> describe "RenameDeck" "Rename a deck"
            (Some typeof<RenameDeckDto>) [ noContent; notFound; invalid; shuttingDown ]

        routef "/notes/{%d}" (fun noteId -> bindOrDeclineJson (fun dto -> Handlers.updateNote dto noteId))
        |> describe "UpdateNote" "Replace the fields of a note, and its tags when given"
            (Some typeof<UpdateNoteDto>) [ noContent; notFound; invalid; shuttingDown ]
    ]
    |> requireApiKey
    DELETE [
        routef "/decks/{%d}" Handlers.deleteDeck
        |> describe "DeleteDeck" "Delete a deck with its child decks and their cards; the default deck is only emptied"
            None [ noContent; notFound; shuttingDown ]

        routef "/notes/{%d}" Handlers.deleteNote
        |> describe "DeleteNote" "Delete a note with its cards" None [ noContent; notFound; shuttingDown ]
    ]
    |> requireApiKey
]

let openBackend (sp: IServiceProvider) : RslibBackend =
    let collectionPath =
        sp.GetRequiredService<IOptions<AnkitServerSettings>>().Value.CollectionPath
        |> Path.GetFullPath

    Directory.CreateDirectory(Path.GetDirectoryName collectionPath) |> ignore

    let backend = RslibBackend.Open defaultLangs

    backend.OpenCollection(
        OpenCollectionRequest(
            CollectionPath = collectionPath,
            MediaFolderPath = Path.ChangeExtension(collectionPath, ".media"),
            MediaDbPath = Path.ChangeExtension(collectionPath, ".media.db2")
        )
    )

    backend

let configureServices (configuration: IConfiguration) (apiKeys: ApiKeyStore) (services: IServiceCollection) =
    services
        .Configure<AnkitServerSettings>(configuration.GetSection AnkitServerSettingsSection)
        .Configure<SyncServerSettings>(configuration.GetSection SyncServerSettingsSection)
        .AddRouting()
        .AddOxpecker()
        .AddOpenApi(fun options ->
            options
                .AddSchemaTransformer<FSharpOptionSchemaTransformer>()
                .AddSchemaTransformer<FieldlessUnionSchemaTransformer>()
                .AddSchemaTransformer<RecordFieldNullabilitySchemaTransformer>()
                .AddDocumentTransformer<ApiKeySecuritySchemeTransformer>()
            |> ignore)
        // The OpenAPI generator reads these, not jsonOptions: the API doesn't accept numbers as strings
        .ConfigureHttpJsonOptions(fun options -> options.SerializerOptions.NumberHandling <- JsonNumberHandling.Strict)
        .AddSingleton<IJsonSerializer>(SystemTextJsonSerializer(jsonOptions))
        .AddSingleton<RslibBackend>(openBackend)
        .AddSingleton<Channel<AnkiSyncMsg>>(
            Channel.CreateUnbounded<AnkiSyncMsg>(UnboundedChannelOptions(SingleReader = true))
        )
        .AddSingleton<IErrorTextService, AnkiErrorTextService>()
        .AddSingleton<IAuthService, AnkiAuthService>()
        .AddSingleton<ISyncService, AnkiSyncService>()
        .AddSingleton<IDeckService, AnkiDeckService>()
        .AddSingleton<INoteService, AnkiNoteService>()
        .AddSingleton<INotetypeService, AnkiNotetypeService>()
        .AddHostedService<AnkiSyncWorker>()
    |> addApiKeyAuth apiKeys

let configureApp (app: IApplicationBuilder) =
    app
        .UseRouting()
        .UseSwaggerUI(fun options ->
            options.SwaggerEndpoint("/openapi/v1.json", "Ankit")
            options.DocumentTitle <- "Ankit API"
            options.EnablePersistAuthorization())
        .Use(ErrorHandler.errorHandler)
        .UseAuthentication()
        .UseAuthorization()
        .UseOxpecker(endpoints)
        .Run(ErrorHandler.notFoundHandler)

let builder = WebApplication.CreateBuilder()
let settings = builder.Configuration.GetSection(AnkitServerSettingsSection).Get<AnkitServerSettings>()

let apiKeys =
    match ApiKeyStore.load settings with
    | Ok apiKeys -> apiKeys
    | Error message ->
        eprintfn $"%s{message}"
        exit 1

configureServices builder.Configuration apiKeys builder.Services

let app = builder.Build()
configureApp app
app.MapOpenApi() |> ignore

if apiKeys.IsDisabled then
    app.Logger.LogWarning("Ankit:DisableAuth is set, the API is open to anyone who can reach it")

app.Run()
