module Ankit.Client.Http.Tests.AnkitHttpApiTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Ankit.Client.Core.Api
open Ankit.Client.Http
open Xunit

/// Answers every request with the same status and body, and keeps the last request and its body
type private FakeHandler(status: HttpStatusCode, body: string) =
    inherit HttpMessageHandler()

    member val Request: HttpRequestMessage option = None with get, set
    member val RequestBody = "" with get, set

    override this.SendAsync(request, _) =
        task {
            this.Request <- Some request

            if not (isNull request.Content) then
                let! content = request.Content.ReadAsStringAsync()
                this.RequestBody <- content

            return new HttpResponseMessage(status, Content = new StringContent(body, Encoding.UTF8, "application/json"))
        }

type private ThrowingHandler(error: exn) =
    inherit HttpMessageHandler()
    override _.SendAsync(_, _) = Task.FromException<HttpResponseMessage> error

let private apiWith (handler: HttpMessageHandler) =
    let http = new HttpClient(handler, BaseAddress = Uri "http://ankit.test/")
    new AnkitHttpApi(http) :> IAnkitApi

let private run (call: Async<'T>) = call |> Async.RunSynchronously

[<Fact>]
let ``Decks are read from camelCase JSON`` () =
    let handler =
        new FakeHandler(HttpStatusCode.OK, """[{"id": 1, "name": "Default"}]""")

    let decks = run ((apiWith handler).Decks())

    Assert.Equal(
        Ok [
            {
                Id = 1L
                Name = "Default"
            }
        ],
        decks
    )

    Assert.Equal("http://ankit.test/decks", string handler.Request.Value.RequestUri)

[<Fact>]
let ``Note types are read with their fields`` () =
    let handler =
        new FakeHandler(
            HttpStatusCode.OK,
            """[{"id": 10, "name": "Basic", "fields": ["Front", "Back"], "isCloze": false}]"""
        )

    match run ((apiWith handler).NoteTypes()) with
    | Ok [ noteType ] -> Assert.Equal<string list>([ "Front"; "Back" ], noteType.Fields)
    | other -> failwith $"Unexpected %A{other}"

[<Fact>]
let ``A suggestion request leaves out what isn't set`` () =
    let handler =
        new FakeHandler(HttpStatusCode.OK, """{"notes": [], "existingNoteIds": []}""")

    let request = {
        SuggestRequest.ofTerm "bank" with
            Context = Some "the bank of the river"
            DeckId = Some 1L
    }

    run ((apiWith handler).Suggest request) |> ignore
    Assert.Equal("http://ankit.test/notes/suggestions", string handler.Request.Value.RequestUri)

    Assert.Equal(
        """{"term":"bank","context":"the bank of the river","deckId":1,"instructions":[]}""",
        handler.RequestBody
    )

[<Fact>]
let ``Suggestions are read with the existing notes`` () =
    let handler =
        new FakeHandler(
            HttpStatusCode.OK,
            """{"notes": [{"noteTypeId": 10, "fields": ["{{c1::bank}}", ""]}], "existingNoteIds": [5]}"""
        )

    let expected = {
        Notes = [
            {
                NoteTypeId = 10L
                Fields = [ "{{c1::bank}}"; "" ]
            }
        ]
        ExistingNoteIds = [ 5L ]
    }

    Assert.Equal(Ok expected, run ((apiWith handler).Suggest(SuggestRequest.ofTerm "bank")))

[<Fact>]
let ``Adding a note posts it and gives its id`` () =
    let handler = new FakeHandler(HttpStatusCode.Created, """{"id": 123}""")

    let note = {
        DeckId = 1L
        NoteTypeId = 10L
        Fields = [ "front"; "back" ]
        Tags = []
    }

    Assert.Equal(Ok 123L, run ((apiWith handler).AddNote note))
    Assert.Equal("""{"deckId":1,"noteTypeId":10,"fields":["front","back"],"tags":[]}""", handler.RequestBody)

[<Fact>]
let ``401 means the key isn't accepted`` () =
    let handler = new FakeHandler(HttpStatusCode.Unauthorized, "")
    Assert.Equal(Error ApiError.Unauthorized, run ((apiWith handler).Decks()))

[<Fact>]
let ``Other errors carry the server's message`` () =
    let handler =
        new FakeHandler(HttpStatusCode.UnprocessableEntity, """{"code": "REJECTED", "message": "Unknown deck"}""")

    Assert.Equal(Error(ApiError.Failed(422, "Unknown deck")), run ((apiWith handler).Decks()))

[<Fact>]
let ``An error without a JSON body is described by its status`` () =
    let handler = new FakeHandler(HttpStatusCode.BadGateway, "<html>Bad Gateway</html>")
    Assert.Equal(Error(ApiError.Failed(502, "502 Bad Gateway")), run ((apiWith handler).Decks()))

[<Fact>]
let ``A connection failure means the server is unreachable`` () =
    let api =
        apiWith (new ThrowingHandler(HttpRequestException "No such host is known."))

    Assert.Equal(Error(ApiError.Unreachable "No such host is known."), run (api.Decks()))

[<Fact>]
let ``The settings give the base address and the key`` () =
    use api =
        AnkitHttpApi.Create {
            Url = " https://ankit.example.com/base/ "
            ApiKey = " secret "
        }

    Assert.NotNull api
