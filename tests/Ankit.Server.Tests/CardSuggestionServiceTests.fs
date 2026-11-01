namespace Ankit.Server.Tests

open System
open System.ClientModel
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Ankit.Server.Env
open Ankit.Server.Llm
open Ankit.Server.Modules
open Ankit.Server.Services
open Ankit.Server.Tests.CommandAssert
open Microsoft.Extensions.AI
open Microsoft.Extensions.Logging.Abstractions
open Xunit

/// Answers every request with the given text and keeps what it was asked
type FakeChat(answer: unit -> string) =
    member val Requests = ResizeArray<ChatMessage list * ChatOptions>()

    member this.Prompt =
        this.Requests |> Seq.last |> fst |> List.map _.Text |> String.concat "\n"

    interface IChatClient with
        member this.GetResponseAsync(messages, options, _) =
            this.Requests.Add(List.ofSeq messages, options)
            Task.FromResult(ChatResponse(ChatMessage(ChatRole.Assistant, answer ())))

        member _.GetStreamingResponseAsync(_, _, _) = raise (NotSupportedException())
        member _.GetService(_, _) = raise (NotSupportedException())
        member _.Dispose() = ()

type CardSuggestionServiceTests() =
    let collection = new TempCollection()
    let notes: INoteService = AnkiNoteService collection.Backend
    let notetypes: INotetypeService = AnkiNotetypeService collection.Backend

    let notetypeId name =
        collection.Backend.GetNotetypeIdByName(Anki.Generic.String(Val = name)).Ntid

    let cloze = notetypeId "Cloze"
    let basic = notetypeId "Basic"

    let settings format = {
        Provider = ""
        ApiKey = ""
        Model = "test-model"
        ResponseFormat = format
        TimeoutSeconds = 0
        Module = "Language"
        Instructions = [| "Explain in Russian"; "Prefer everyday examples" |]
    }

    let modules nativeLanguage =
        match
            SuggestionModules.create
                [
                    Language.create { NativeLanguage = nativeLanguage }
                    SuggestionModule.general
                ]
                "language"
        with
        | Ok modules -> modules
        | Error message -> failwith message

    let service (chat: IChatClient) format : ICardSuggestionService =
        let llm = {
            Chat = Some chat
            Models = None
            Settings = settings format
        }

        AnkiCardSuggestionService(
            collection.Backend,
            notetypes,
            llm,
            modules "Russian",
            NullLogger<AnkiCardSuggestionService>.Instance
        )

    let request notetypeId = {
        Term = "bank"
        Context = Some "We had a picnic on the bank of the river."
        Source = Some "A walk in the park"
        NotetypeId = Some notetypeId
        DeckId = None
        Count = 1
        Module = None
        Style = None
        Instructions = []
    }

    let guided (request: SuggestRequest) = { request with Style = Some "GUIDED_CLOZE" }

    let suggest (chat: FakeChat) format request =
        (service chat format).Suggest(request, CancellationToken.None).Result

    let suggested result =
        match result with
        | Ok suggestions -> suggestions
        | Error e -> failwith $"Expected suggestions, got %A{e}"

    let fieldsOf (suggestions: Suggestions) = suggestions.Notes |> List.map _.Fields

    let invalidOutput result =
        match result with
        | Error(SuggestFailed.InvalidOutput _) -> ()
        | other -> Assert.Fail $"Expected InvalidOutput, got %A{other}"

    let clozeAnswer =
        """{"notes": [{"Text": "A picnic on the {{c1::bank}} of the river", "Back Extra": "берег"}]}"""

    [<Fact>]
    member _.``Suggestions are off without a model``() =
        let llm =
            match LlmClient.create { settings LlmResponseFormat.JsonSchema with Model = "" } with
            | Ok llm -> llm
            | Error message -> failwith message

        Assert.True(llm.Chat.IsNone)

        let service =
            AnkiCardSuggestionService(
                collection.Backend,
                notetypes,
                llm,
                modules "Russian",
                NullLogger<AnkiCardSuggestionService>.Instance
            )
            :> ICardSuggestionService

        match service.Suggest(request cloze, CancellationToken.None).Result with
        | Error SuggestFailed.NotConfigured -> ()
        | other -> Assert.Fail $"Expected NotConfigured, got %A{other}"

    [<Fact>]
    member _.``Provider must be an http URL``() =
        let create provider =
            LlmClient.create { settings LlmResponseFormat.JsonSchema with Provider = provider }

        Assert.True(Result.isError (create "localhost:11434"))
        Assert.True(Result.isError (create "file:///etc/passwd"))
        Assert.True(Result.isOk (create "http://localhost:11434/v1"))
        Assert.True(Result.isOk (create ""))

    [<Fact>]
    member _.``Cloze suggestion has the fields of the note type in order``() =
        let chat = new FakeChat(fun () -> clozeAnswer)

        let result = suggested (suggest chat LlmResponseFormat.JsonSchema (request cloze))

        Assert.Equal<string list list>([ [ "A picnic on the {{c1::bank}} of the river"; "берег" ] ], (fieldsOf result))

    [<Fact>]
    member _.``Prompt carries the term, its context, the fields and the instructions``() =
        let chat = new FakeChat(fun () -> clozeAnswer)

        suggest chat LlmResponseFormat.JsonSchema {
            request cloze with
                Instructions = [ "Keep it short"; "Add an example" ]
        }
        |> suggested
        |> ignore

        for expected in
            [
                "bank"
                "We had a picnic on the bank of the river."
                "A walk in the park"
                "\"Text\", \"Back Extra\""
                "cloze"
                "The learner's instructions:
- Explain in Russian
- Prefer everyday examples"
                "Instructions for this request:
- Keep it short
- Add an example"
            ] do
            Assert.Contains(expected, chat.Prompt)

    [<Fact>]
    member _.``Notes with the term are reported and shown to the model``() =
        let existing =
            ok (
                notes.Add(
                    1L,
                    basic,
                    {
                        Fields = [ "river bank"; "берег реки" ]
                        Tags = []
                    }
                )
            )

        ok (
            notes.Add(
                1L,
                basic,
                {
                    Fields = [ "cat"; "кошка" ]
                    Tags = []
                }
            )
        )
        |> ignore

        let chat = new FakeChat(fun () -> clozeAnswer)

        let result = suggested (suggest chat LlmResponseFormat.JsonSchema (request cloze))

        Assert.Equal<int64 list>([ existing ], result.ExistingNoteIds)
        Assert.Contains("берег реки", chat.Prompt)
        Assert.DoesNotContain("кошка", chat.Prompt)

    [<Fact>]
    member _.``Recent notes of the note type are shown as examples``() =
        ok (
            notes.Add(
                1L,
                basic,
                {
                    Fields = [ "cat"; "кошка" ]
                    Tags = []
                }
            )
        )
        |> ignore

        let chat =
            new FakeChat(fun () -> """{"notes": [{"Front": "bank (of a river)", "Back": "берег"}]}""")

        let result = suggested (suggest chat LlmResponseFormat.JsonSchema (request basic))

        Assert.Equal<string list list>([ [ "bank (of a river)"; "берег" ] ], (fieldsOf result))
        Assert.Contains("Front: cat | Back: кошка", chat.Prompt)

    [<Fact>]
    member _.``JSON schema requires every field``() =
        let chat = new FakeChat(fun () -> clozeAnswer)
        suggest chat LlmResponseFormat.JsonSchema (request cloze) |> suggested |> ignore

        let options = chat.Requests |> Seq.last |> snd

        match options.ResponseFormat with
        | :? ChatResponseFormatJson as format when format.Schema.HasValue ->
            let schema = format.Schema.Value.GetRawText()
            Assert.Contains("\"Text\"", schema)
            Assert.Contains("\"Back Extra\"", schema)
        | other -> Assert.Fail $"Expected a JSON schema, got %A{other}"

    [<Fact>]
    member _.``Answer in Markdown fences is accepted without a JSON mode``() =
        let chat = new FakeChat(fun () -> $"Here you go:\n```json\n%s{clozeAnswer}\n```")

        let result = suggested (suggest chat LlmResponseFormat.Prompt (request cloze))

        Assert.Null((chat.Requests |> Seq.last |> snd).ResponseFormat)
        Assert.Equal(1, result.Notes.Length)

    [<Fact>]
    member _.``Markup other than simple formatting is escaped``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"Text": "<b>A</b> {{c1::bank}}<br/><img src=x onerror=alert(1)><script>x</script>", "Back Extra": "<b onclick=\"x\">b</b>"}]}""")

        let result = suggested (suggest chat LlmResponseFormat.JsonSchema (request cloze))

        Assert.Equal<string list list>(
            [
                [
                    "<b>A</b> {{c1::bank}}<br>&lt;img src=x onerror=alert(1)&gt;&lt;script&gt;x&lt;/script&gt;"
                    "&lt;b onclick=\"x\"&gt;b</b>"
                ]
            ],
            (fieldsOf result)
        )

    [<Fact>]
    member _.``Cloze notes without a deletion are dropped``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"Text": "A picnic on the bank", "Back Extra": ""}, {"Text": "On the {{c1::bank}}", "Back Extra": ""}]}""")

        let result =
            suggested (suggest chat LlmResponseFormat.JsonSchema { request cloze with Count = 2 })

        Assert.Equal<string list list>([ [ "On the {{c1::bank}}"; "" ] ], (fieldsOf result))

    [<Fact>]
    member _.``Answer without a usable note is rejected``() =
        invalidOutput (
            suggest
                (new FakeChat(fun () -> """{"notes": [{"Text": "no deletion here", "Back Extra": ""}]}"""))
                LlmResponseFormat.JsonSchema
                (request cloze)
        )

        invalidOutput (
            suggest (new FakeChat(fun () -> """{"notes": []}""")) LlmResponseFormat.JsonSchema (request cloze)
        )

        invalidOutput (
            suggest (new FakeChat(fun () -> "I can't help with that")) LlmResponseFormat.JsonSchema (request cloze)
        )

        invalidOutput (
            suggest (new FakeChat(fun () -> """{"cards": []}""")) LlmResponseFormat.JsonSchema (request cloze)
        )

    [<Fact>]
    member _.``No more notes than requested are returned``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"Front": "1", "Back": "1"}, {"Front": "2", "Back": "2"}, {"Front": "3", "Back": "3"}]}""")

        let result =
            suggested (suggest chat LlmResponseFormat.JsonSchema { request basic with Count = 2 })

        Assert.Equal<string list list>([ [ "1"; "1" ]; [ "2"; "2" ] ], (fieldsOf result))

    [<Fact>]
    member _.``Unknown note type is rejected without asking the model``() =
        let chat = new FakeChat(fun () -> clozeAnswer)

        match suggest chat LlmResponseFormat.JsonSchema (request 424242L) with
        | Error(SuggestFailed.Command(CommandFailed.Rejected _)) -> ()
        | other -> Assert.Fail $"Expected Rejected, got %A{other}"

        Assert.Empty(chat.Requests)

    [<Fact>]
    member _.``Unreachable LLM server is reported``() =
        let chat = new FakeChat(fun () -> raise (HttpRequestException "Connection refused"))

        match suggest chat LlmResponseFormat.JsonSchema (request cloze) with
        | Error(SuggestFailed.LlmFailed message) -> Assert.Contains("Connection refused", message)
        | other -> Assert.Fail $"Expected LlmFailed, got %A{other}"

    [<Fact>]
    member _.``LLM failure after retries is reported``() =
        let chat =
            new FakeChat(fun () ->
                raise (
                    AggregateException(
                        "Retry failed after 2 tries",
                        ClientResultException "Service Unavailable",
                        ClientResultException "Rate limit reached"
                    )
                ))

        match suggest chat LlmResponseFormat.JsonSchema (request cloze) with
        | Error(SuggestFailed.LlmFailed message) -> Assert.Contains("Rate limit reached", message)
        | other -> Assert.Fail $"Expected LlmFailed, got %A{other}"

    [<Fact>]
    member _.``Cloze note type gets plain cloze by default``() =
        let chat = new FakeChat(fun () -> clozeAnswer)

        suggest chat LlmResponseFormat.JsonSchema (request cloze) |> suggested |> ignore

        Assert.DoesNotContain("guided", chat.Prompt)

    [<Fact>]
    member _.``Guided cloze asks for a unique hint in the native language and the back of the card``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"Text": "We sat on the {{c1::bank::берег реки}} of the river", "Back Extra": "We sat on the bank of the river<br>/bæŋk/<br>river bank"}]}""")

        let result =
            suggested (suggest chat LlmResponseFormat.JsonSchema (guided (request cloze)))

        Assert.Equal<string list list>(
            [
                [
                    "We sat on the {{c1::bank::берег реки}} of the river"
                    "We sat on the bank of the river<br>/bæŋk/<br>river bank"
                ]
            ],
            (fieldsOf result)
        )

        for expected in
            [
                "{{c1::term::hint}}"
                "in Russian, or in simple words"
                "uniquely"
                "IPA"
                "collocation"
            ] do
            Assert.Contains(expected, chat.Prompt)

    [<Fact>]
    member _.``Guided cloze without a native language hints in the learned language``() =
        let chat =
            new FakeChat(fun () -> """{"notes": [{"Text": "the {{c1::bank::land by a river}}", "Back Extra": ""}]}""")

        let llm = {
            Chat = Some(chat :> IChatClient)
            Models = None
            Settings = settings LlmResponseFormat.JsonSchema
        }

        let service =
            AnkiCardSuggestionService(
                collection.Backend,
                notetypes,
                llm,
                modules "",
                NullLogger<AnkiCardSuggestionService>.Instance
            )
            :> ICardSuggestionService

        service.Suggest(guided (request cloze), CancellationToken.None).Result
        |> suggested
        |> ignore

        Assert.Contains("hint, in simple words of the language being learned", chat.Prompt)
        Assert.DoesNotContain("native language", chat.Prompt)

    [<Fact>]
    member _.``Guided cloze notes without a hint in every deletion are dropped``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"Text": "the {{c1::bank}} of the river", "Back Extra": ""}, {"Text": "the {{c1::bank::берег}} and {{c2::river}}", "Back Extra": ""}, {"Text": "the {{c1::bank::берег}} of the {{c2::river::река}}", "Back Extra": ""}]}""")

        let result =
            suggested (suggest chat LlmResponseFormat.JsonSchema { guided (request cloze) with Count = 3 })

        Assert.Equal<string list list>(
            [ [ "the {{c1::bank::берег}} of the {{c2::river::река}}"; "" ] ],
            (fieldsOf result)
        )

        invalidOutput (
            suggest
                (new FakeChat(fun () ->
                    """{"notes": [{"Text": "the {{c1::bank::}} of the river", "Back Extra": ""}]}"""))
                LlmResponseFormat.JsonSchema
                (guided (request cloze))
        )

    [<Fact>]
    member _.``Cloze style on a note type without cloze is rejected without asking the model``() =
        let chat = new FakeChat(fun () -> clozeAnswer)

        match suggest chat LlmResponseFormat.JsonSchema (guided (request basic)) with
        | Error(SuggestFailed.Command(CommandFailed.Rejected message)) -> Assert.Contains("Basic", message)
        | other -> Assert.Fail $"Expected Rejected, got %A{other}"

        Assert.Empty(chat.Requests)

    [<Fact>]
    member _.``Collocation in context shows the collocation on the front and explains it on the back``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"Front": "She <b>made a decision</b> quickly.", "Back": "make a decision<br>принять решение<br>We need to make a decision today."}, {"Front": "She {{c1::made}} a decision", "Back": "make a decision"}, {"Front": "She <b>made a decision</b>.", "Back": ""}]}""")

        let result =
            suggested (
                suggest chat LlmResponseFormat.JsonSchema {
                    request basic with
                        Term = "decision"
                        Style = Some "COLLOCATION_IN_CONTEXT"
                        Count = 3
                }
            )

        Assert.Equal<string list list>(
            [
                [
                    "She <b>made a decision</b> quickly."
                    "make a decision<br>принять решение<br>We need to make a decision today."
                ]
            ],
            (fieldsOf result)
        )

        for expected in
            [
                "Write every note as collocation in context"
                "On the front"
                "<b>bold</b>"
                "its meaning in Russian, or in simple words"
                "one more example sentence"
                "don't use cloze deletions"
            ] do
            Assert.Contains(expected, chat.Prompt)

        Assert.DoesNotContain("{{c1::", chat.Prompt)

    [<Fact>]
    member _.``Collocation in context on a cloze note type is rejected without asking the model``() =
        let chat = new FakeChat(fun () -> clozeAnswer)

        match
            suggest chat LlmResponseFormat.JsonSchema { request cloze with Style = Some "COLLOCATION_IN_CONTEXT" }
        with
        | Error(SuggestFailed.Command(CommandFailed.Rejected message)) -> Assert.Contains("Basic", message)
        | other -> Assert.Fail $"Expected Rejected, got %A{other}"

        Assert.Empty(chat.Requests)

    [<Fact>]
    member _.``Cloze deletions are dropped from note types without cloze``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"Front": "the {{c1::bank}} of a river", "Back": "берег"}, {"Front": "bank (of a river)", "Back": "берег"}]}""")

        let result =
            suggested (suggest chat LlmResponseFormat.JsonSchema { request basic with Count = 2 })

        Assert.Equal<string list list>([ [ "bank (of a river)"; "берег" ] ], (fieldsOf result))

    [<Fact>]
    member _.``Auto on a cloze note type chooses between guided and plain cloze``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"Text": "We {{c1::strolled::гуляли не спеша}} along the bank", "Back Extra": "/strəʊld/"}, {"Text": "A picnic on the {{c1::bank}} of the river", "Back Extra": ""}]}""")

        let result =
            suggested (
                suggest chat LlmResponseFormat.JsonSchema {
                    request cloze with
                        Style = Some "AUTO"
                        Count = 2
                }
            )

        Assert.Equal(2, result.Notes.Length)

        for expected in
            [
                "Choose the style of each note"
                "Guided cloze, for a high-value word"
                "IPA"
                "Plain cloze, for any other term"
            ] do
            Assert.Contains(expected, chat.Prompt)

        Assert.DoesNotContain("Collocation in context", chat.Prompt)

    [<Fact>]
    member _.``Auto on a note type without cloze chooses between collocation in context and a plain card``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"Front": "She <b>made a decision</b>.", "Back": "make a decision<br>принять решение"}, {"Front": "bank (of a river)", "Back": "берег"}]}""")

        let result =
            suggested (
                suggest chat LlmResponseFormat.JsonSchema {
                    request basic with
                        Style = Some "AUTO"
                        Count = 2
                }
            )

        Assert.Equal(2, result.Notes.Length)

        for expected in
            [
                "Choose the style of each note"
                "Collocation in context, for a term"
                "Plain card, for any other term"
                "don't use cloze deletions"
            ] do
            Assert.Contains(expected, chat.Prompt)

        Assert.DoesNotContain("Guided cloze", chat.Prompt)

    [<Fact>]
    member _.``Auto still needs a cloze deletion``() =
        invalidOutput (
            suggest
                (new FakeChat(fun () -> """{"notes": [{"Text": "A picnic on the bank", "Back Extra": ""}]}"""))
                LlmResponseFormat.JsonSchema
                { request cloze with Style = Some "AUTO" }
        )

    [<Fact>]
    member _.``Language module is the default and tells the model about the language``() =
        let chat = new FakeChat(fun () -> clozeAnswer)

        suggest chat LlmResponseFormat.JsonSchema (request cloze) |> suggested |> ignore

        Assert.Contains("studies a foreign language", chat.Prompt)
        Assert.Contains("native language is Russian", chat.Prompt)

    [<Fact>]
    member _.``General module has only the plain styles and no language in the prompt``() =
        let chat = new FakeChat(fun () -> clozeAnswer)
        let general (request: SuggestRequest) = { request with Module = Some "general" }

        suggest chat LlmResponseFormat.JsonSchema (general (request cloze))
        |> suggested
        |> ignore

        Assert.DoesNotContain("foreign language", chat.Prompt)
        Assert.DoesNotContain("Russian, or", chat.Prompt)

        suggest chat LlmResponseFormat.JsonSchema { general (request cloze) with Style = Some "AUTO" }
        |> suggested
        |> ignore

        Assert.DoesNotContain("Choose the style", chat.Prompt)

        match suggest chat LlmResponseFormat.JsonSchema (guided (general (request cloze))) with
        | Error(SuggestFailed.Command(CommandFailed.Rejected message)) ->
            Assert.Contains("GUIDED_CLOZE", message)
            Assert.Contains("CLOZE, CARD, AUTO", message)
        | other -> Assert.Fail $"Expected Rejected, got %A{other}"

    [<Fact>]
    member _.``Styles and modules are matched ignoring case and underscores``() =
        let chat =
            new FakeChat(fun () -> """{"notes": [{"Text": "the {{c1::bank::берег}}", "Back Extra": ""}]}""")

        suggest chat LlmResponseFormat.JsonSchema {
            request cloze with
                Module = Some "Language"
                Style = Some "GuidedCloze"
        }
        |> suggested
        |> ignore

        Assert.Contains("Write every note as guided cloze", chat.Prompt)

    [<Fact>]
    member _.``Unknown module is rejected without asking the model``() =
        let chat = new FakeChat(fun () -> clozeAnswer)

        match suggest chat LlmResponseFormat.JsonSchema { request cloze with Module = Some "medicine" } with
        | Error(SuggestFailed.Command(CommandFailed.Rejected message)) -> Assert.Contains("LANGUAGE, GENERAL", message)
        | other -> Assert.Fail $"Expected Rejected, got %A{other}"

        Assert.Empty(chat.Requests)

    [<Fact>]
    member _.``Default module must exist``() =
        Assert.True(Result.isError (SuggestionModules.create [ SuggestionModule.general ] "language"))
        Assert.True(Result.isOk (SuggestionModules.create [ SuggestionModule.general ] "General"))

    [<Fact>]
    member _.``Without a note type the model picks one for each note``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"noteType": "Cloze", "fields": {"Text": "A picnic on the {{c1::bank}}", "Back Extra": ""}}, {"noteType": "basic", "fields": {"Front": "bank (of a river)", "Back": "берег"}}]}""")

        let result =
            suggested (
                suggest chat LlmResponseFormat.JsonSchema {
                    request cloze with
                        NotetypeId = None
                        Count = 2
                }
            )

        Assert.Equal<SuggestedNote list>(
            [
                {
                    NotetypeId = cloze
                    Fields = [ "A picnic on the {{c1::bank}}"; "" ]
                }
                {
                    NotetypeId = basic
                    Fields = [ "bank (of a river)"; "берег" ]
                }
            ],
            result.Notes
        )

        for expected in
            [
                "Choose the note type of each note"
                "\"Basic\": \"Front\", \"Back\""
                "\"Cloze\": \"Text\", \"Back Extra\""
            ] do
            Assert.Contains(expected, chat.Prompt)

        Assert.DoesNotContain("Image Occlusion", chat.Prompt)

        match (chat.Requests |> Seq.last |> snd).ResponseFormat with
        | :? ChatResponseFormatJson as format when format.Schema.HasValue ->
            let schema = format.Schema.Value.GetRawText()
            Assert.Contains("anyOf", schema)
            Assert.Contains("\"Basic (and reversed card)\"", schema)
        | other -> Assert.Fail $"Expected a JSON schema, got %A{other}"

    [<Fact>]
    member _.``Without a note type only those the style fits are offered``() =
        let chat =
            new FakeChat(fun () ->
                """{"notes": [{"noteType": "Cloze", "fields": {"Text": "She {{c1::made}} a decision", "Back Extra": ""}}, {"noteType": "Vocabulary", "fields": {"Word": "decision"}}, {"noteType": "Basic", "fields": {"Front": "She <b>made a decision</b>.", "Back": "make a decision"}}]}""")

        let result =
            suggested (
                suggest chat LlmResponseFormat.JsonSchema {
                    request basic with
                        NotetypeId = None
                        Term = "decision"
                        Style = Some "COLLOCATION_IN_CONTEXT"
                        Count = 3
                }
            )

        Assert.Equal<SuggestedNote list>(
            [
                {
                    NotetypeId = basic
                    Fields = [ "She <b>made a decision</b>."; "make a decision" ]
                }
            ],
            result.Notes
        )

        Assert.Contains("\"Basic (type in the answer)\"", chat.Prompt)
        Assert.DoesNotContain("\"Cloze\"", chat.Prompt)

    [<Fact>]
    member _.``Without a note type the style fits the request is rejected without asking the model``() =
        collection.Backend.RemoveNotetype(Anki.Notetypes.NotetypeId(Ntid = cloze))
        |> ignore

        let chat = new FakeChat(fun () -> clozeAnswer)

        match suggest chat LlmResponseFormat.JsonSchema { guided (request cloze) with NotetypeId = None } with
        | Error(SuggestFailed.Command(CommandFailed.Rejected message)) -> Assert.Contains("GUIDED_CLOZE", message)
        | other -> Assert.Fail $"Expected Rejected, got %A{other}"

        Assert.Empty(chat.Requests)

    interface IDisposable with
        member _.Dispose() = (collection :> IDisposable).Dispose()
