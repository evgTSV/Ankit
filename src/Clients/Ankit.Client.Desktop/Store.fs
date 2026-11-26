/// What the app keeps between runs, in a JSON file in the user's application data (%APPDATA%\Ankit on Windows,
/// ~/.config/Ankit on Linux and macOS). The API key is stored as is, readable by the user's account only as far as the
/// folder is
module Ankit.Client.Desktop.Store

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open Ankit.Client.Core.Settings

type AppSettings = {
    Server: ServerSettings
    /// Preselected in the popup
    LastDeckId: int64 option
}

let private json =
    let options =
        JsonSerializerOptions(JsonSerializerDefaults.Web, WriteIndented = true)

    JsonFSharpOptions
        .Default()
        .WithSkippableOptionFields(SkippableOptionFields.Always)
        .AddToJsonSerializerOptions(options)

    options

let private path =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "Ankit", "settings.json")

let empty = {
    Server = ServerSettings.empty
    LastDeckId = None
}

/// The saved settings; empty ones when there are none yet or the file can't be read
let load () =
    try
        if File.Exists path then
            JsonSerializer.Deserialize<AppSettings>(File.ReadAllText path, json)
        else
            empty
    with _ ->
        empty

let save (settings: AppSettings) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, JsonSerializer.Serialize(settings, json))
