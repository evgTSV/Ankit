using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Anki.Backend;
using Anki.Decks;
using Anki.Notes;
using Anki.Search;
using Xunit;

namespace Ankit.Native.Tests;

public sealed class RslibBackendTests : IDisposable
{
    private readonly TempCollection _collection = new();

    private RslibBackend Backend => _collection.Backend;

    public void Dispose() => _collection.Dispose();

    [Fact]
    public void BuildHashIsReadable()
    {
        // Empty for rslib built from the Paket sources, but must be valid UTF-8 from a static native string
        Assert.NotNull(RslibBackend.BuildHash);
    }

    [Fact]
    public void FtlMessagesCanBeTranslated()
    {
        Assert.Equal("Email or password was incorrect; please try again.", Backend.Translate("sync-wrong-pass"));
        Assert.Throws<ArgumentException>(() => Backend.Translate("no-such-message"));
    }

    [Fact]
    public void AddedDeckIsListed()
    {
        var deck = Backend.NewDeck();
        deck.Name = "Ankit::Tests";

        var added = Backend.AddDeck(deck);

        var names = Backend.GetDeckNames(new GetDeckNamesRequest { IncludeFiltered = true });
        Assert.Contains(names.Entries, e => e.Id == added.Id && e.Name == "Ankit::Tests");
        Assert.Equal(added.Id, Backend.GetDeckIdByName(new Anki.Generic.String { Val = "Ankit::Tests" }).Did);
    }

    [Fact]
    public void AddedNoteCanBeSearched()
    {
        var basic = Backend.GetNotetypeNames().Entries.Single(e => e.Name == "Basic");
        var note = Backend.NewNote(new Anki.Notetypes.NotetypeId { Ntid = basic.Id });
        note.Fields[0] = "猫";
        note.Fields[1] = "cat";

        var added = Backend.AddNote(new AddNoteRequest { Note = note, DeckId = 1 });

        var found = Backend.SearchNotes(new SearchRequest { Search = "猫" });
        Assert.Equal([added.NoteId], found.Ids);
        Assert.Equal(["猫", "cat"], Backend.GetNote(new NoteId { Nid = added.NoteId }).Fields);
    }

    [Fact]
    public void BackendErrorsAreThrownWithDetails()
    {
        var error = Assert.Throws<BackendException>(() => Backend.GetDeck(new DeckId { Did = 424242 }));

        Assert.Equal(BackendError.Types.Kind.NotFoundError, error.Kind);
        Assert.Contains("424242", error.Message);
    }

    [Fact]
    public void InvalidServiceIndexIsRejected()
    {
        var error = Assert.Throws<BackendException>(() => Backend.Command(uint.MaxValue, 0, []));

        Assert.Equal(BackendError.Types.Kind.InvalidInput, error.Kind);
    }

    [Fact]
    public void DbCommandRunsSql()
    {
        var request = """{"kind":"query","sql":"select name from decks where id = 1","args":[],"first_row_only":false}""";

        var response = Backend.DbCommand(Encoding.UTF8.GetBytes(request));

        Assert.Equal([["Default"]], JsonSerializer.Deserialize<string[][]>(response));
    }

    [Fact]
    public void CallsCanBeNadeFromSeveralThreads()
    {
        Parallel.For(0, 32, i =>
        {
            var deck = Backend.NewDeck();
            deck.Name = $"Parallel::{i}";
            Backend.AddDeck(deck);
        });

        var names = Backend.GetDeckNames(new GetDeckNamesRequest());
        Assert.Equal(32, names.Entries.Count(e => e.Name.StartsWith("Parallel::")));
    }

    [Fact]
    public void DisposedBackendCannotBeUsed()
    {
        var backend = RslibBackend.Open("en");
        backend.Dispose();
        backend.Dispose();

        Assert.Throws<ObjectDisposedException>(backend.GetNotetypeNames);
    }
}
