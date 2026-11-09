namespace Ankit.Client.Http

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Ankit.Client.Core.Api
open Ankit.Client.Core.Settings

type private Created = { Id: int64 }

type private ErrorBody = { Message: string option }

/// IAnkitApi over HttpClient, for .NET clients (desktop, Android)
type AnkitHttpApi(http: HttpClient) =
    /// camelCase like the server; an option that is None is left out, a missing or null field reads as None
    static let json =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull

        JsonFSharpOptions
            .Default()
            .WithSkippableOptionFields(SkippableOptionFields.FromJsonSerializerOptions)
            .AddToJsonSerializerOptions(options)

        options

    /// Suggestions take an LLM call: several seconds, more on a slow model
    static let timeout = TimeSpan.FromSeconds 120.0

    static let failure (response: HttpResponseMessage) =
        async {
            match response.StatusCode with
            | HttpStatusCode.Unauthorized -> return ApiError.Unauthorized
            | status ->
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                let message =
                    try
                        JsonSerializer.Deserialize<ErrorBody>(body, json).Message
                    with _ ->
                        None
                    |> Option.defaultValue $"%d{int status} %s{response.ReasonPhrase}"

                return ApiError.Failed(int status, message)
        }

    static let send (request: unit -> Async<HttpResponseMessage>) (read: HttpResponseMessage -> Async<'T>) =
        async {
            try
                use! response = request ()

                if response.IsSuccessStatusCode then
                    let! value = read response
                    return Ok value
                else
                    let! error = failure response
                    return Error error
            with e ->
                // Async.AwaitTask wraps the task's exception
                match
                    (match e with
                     | :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException
                     | e -> e)
                with
                | :? HttpRequestException as e -> return Error(ApiError.Unreachable e.Message)
                | :? TaskCanceledException -> return Error(ApiError.Unreachable "the request timed out")
                | _ -> return raise e
        }

    let get (path: string) : Async<Result<'T, ApiError>> =
        send (fun () -> http.GetAsync(path) |> Async.AwaitTask) (fun response ->
            response.Content.ReadFromJsonAsync<'T>(json) |> Async.AwaitTask)

    let post (path: string) (body: 'B) (read: HttpResponseMessage -> Async<'T>) : Async<Result<'T, ApiError>> =
        send (fun () -> http.PostAsJsonAsync(path, body, json) |> Async.AwaitTask) read

    /// A client for the server in the settings, with the API key on every request
    static member Create(settings: ServerSettings) =
        let http = new HttpClient(Timeout = timeout)
        http.BaseAddress <- Uri(settings.Url.Trim().TrimEnd('/') + "/")
        http.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim())
        new AnkitHttpApi(http)

    interface IAnkitApi with
        member _.Decks() = (get "decks": Async<Result<Deck list, ApiError>>)
        member _.NoteTypes() = (get "note-types": Async<Result<NoteType list, ApiError>>)

        member _.Suggest(request) =
            post "notes/suggestions" request (fun response ->
                response.Content.ReadFromJsonAsync<Suggestions>(json) |> Async.AwaitTask)

        member _.AddNote(note) =
            post "notes" note (fun response ->
                async {
                    let! created = response.Content.ReadFromJsonAsync<Created>(json) |> Async.AwaitTask
                    return created.Id
                })

    interface IDisposable with
        member _.Dispose() = http.Dispose()
