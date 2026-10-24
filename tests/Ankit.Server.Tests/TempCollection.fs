namespace Ankit.Server.Tests

open System
open System.IO
open Anki.Collection
open Ankit.Native

/// A backend with a fresh collection in a temporary directory, removed on dispose
type TempCollection() =
    let directory = Directory.CreateTempSubdirectory("ankit-server-tests-")
    let backend = RslibBackend.Open [ "en" ]

    do
        backend.OpenCollection(
            OpenCollectionRequest(
                CollectionPath = Path.Combine(directory.FullName, "collection.anki2"),
                MediaFolderPath = Path.Combine(directory.FullName, "collection.media"),
                MediaDbPath = Path.Combine(directory.FullName, "collection.media.db")
            )
        )

    member _.Backend = backend

    interface IDisposable with
        member _.Dispose() =
            backend.CloseCollection(CloseCollectionRequest())
            backend.Dispose()
            directory.Delete(recursive = true)
