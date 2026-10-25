module Ankit.Server.Main

open System
open System.IO
open System.Threading.Channels
open Anki.Collection
open Ankit.Server
open Ankit.Server.Handlers.ModelBinder
open Ankit.Server.Env
open Ankit.Server.Services
open Ankit.Native
open Ankit.Server.Utils
open Oxpecker
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options

let endpoints = [
    GET [
        route "/ping" Handlers.ping
        route "/decks" Handlers.listDecks
        route "/notes" <| bindOrDeclineQuery Handlers.searchNotes
        routef "/notes/{%d}" Handlers.getNote
        route "/note-types" Handlers.listNoteTypes
    ]
    POST [
        route "/decks" <| bindOrDeclineJson Handlers.createDeck
        route "/notes" <| bindOrDeclineJson Handlers.addNote
        route "/sync/full" <| bindOrDeclineJson Handlers.fullSync
    ]
    PUT [
        routef "/decks/{%d}" <| bindOrDeclineJson1 Handlers.renameDeck
        routef "/notes/{%d}" <| bindOrDeclineJson1 Handlers.updateNote
    ]
    DELETE [
        routef "/decks/{%d}" Handlers.deleteDeck
        routef "/notes/{%d}" Handlers.deleteNote
    ]
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

let configureServices (configuration: IConfiguration) (services: IServiceCollection) =
    services
        .Configure<AnkitServerSettings>(configuration.GetSection AnkitServerSettingsSection)
        .Configure<SyncServerSettings>(configuration.GetSection SyncServerSettingsSection)
        .AddRouting()
        .AddOxpecker()
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
    |> ignore

let configureApp (app: IApplicationBuilder) =
    app
        .UseRouting()
        .Use(ErrorHandler.errorHandler)
        .UseOxpecker(endpoints)
        .Run(ErrorHandler.notFoundHandler)

let builder = WebApplication.CreateBuilder()
configureServices builder.Configuration builder.Services

let app = builder.Build()
configureApp app

app.Run()
