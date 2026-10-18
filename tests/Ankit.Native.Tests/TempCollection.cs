using System;
using System.IO;
using Anki.Collection;

namespace Ankit.Native.Tests;

/// <summary>
/// A backend with a fresh collection in a temporary directory, removed on dispose.
/// </summary>
public sealed class TempCollection : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ankit-native-tests-");

    public TempCollection()
    {
        Backend = RslibBackend.Open("en");
        Backend.OpenCollection(new OpenCollectionRequest
        {
            CollectionPath = Path.Combine(_directory.FullName, "collection.anki2"),
            MediaFolderPath = Path.Combine(_directory.FullName, "collection.media"),
            MediaDbPath = Path.Combine(_directory.FullName, "collection.media.db"),
        });
    }

    public RslibBackend Backend { get; }

    public void Dispose()
    {
        Backend.CloseCollection(new CloseCollectionRequest());
        Backend.Dispose();
        _directory.Delete(recursive: true);
    }
}
