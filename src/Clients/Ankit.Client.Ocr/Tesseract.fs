namespace Ankit.Client.Ocr

open System
open System.IO
open Ankit.Client.Core.ScreenText
open TesseractOCR
open TesseractOCR.Enums

type TesseractSettings = {
    /// The folder with the .traineddata files
    TessdataPath: string
    /// Tesseract languages, e.g. "eng+rus"
    Languages: string
}

module TesseractSettings =
    /// tessdata next to the app, where the build puts it
    let defaults = {
        TessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata")
        Languages = "eng+rus"
    }

module TesseractLanguages =
    let private scripts =
        dict [
            for code in [ "eng"; "deu"; "fra"; "spa"; "ita"; "por"; "nld"; "pol"; "ces"; "tur" ] do
                code, "Latin"
            for code in [ "rus"; "ukr"; "bel"; "bul"; "srp"; "kaz" ] do
                code, "Cyrillic"
        ]

    let private scriptOf (c: char) =
        if c >= 'Ѐ' && c <= 'ԯ' then Some "Cyrillic"
        elif Char.IsLetter c && c < 'ɐ' then Some "Latin"
        else None

    /// The configured languages written in the sample's scripts. With both loaded, Tesseract may read Latin text as
    /// look-alike Cyrillic letters ("I'd" as "Та"). Languages of unknown script are kept; all of them when the sample
    /// has no letters or no configured language fits
    let forSample (configured: string) (sample: string) =
        let found = sample |> Seq.choose scriptOf |> Set.ofSeq

        let fits (code: string) =
            match scripts.TryGetValue code with
            | true, script -> found.Contains script
            | _ -> true

        let codes = configured.Split('+', StringSplitOptions.RemoveEmptyEntries)

        match codes |> Array.filter fits with
        | chosen when not found.IsEmpty && chosen.Length > 0 -> String.Join("+", chosen)
        | _ -> configured

/// Tesseract through TesseractOCR, which bundles the native libraries for Windows; macOS and Linux need the system's
/// libtesseract. An engine per language set loads its trained data once; one capture at a time uses them
type TesseractOcr(settings: TesseractSettings) =
    /// Screen text is smaller than the 30 px Tesseract reads best: upscaling helps more than it costs
    let scale = 2.0f

    let engines = Collections.Generic.Dictionary<string, Engine>()

    let engineFor (languages: string) =
        match engines.TryGetValue languages with
        | true, engine -> engine
        | _ ->
            let engine = new Engine(settings.TessdataPath, languages, EngineMode.LstmOnly)
            // Tesseract writes diagnostics such as "Detected 26 diacritics" to the console otherwise
            engine.SetVariable("debug_file", (if OperatingSystem.IsWindows() then "NUL" else "/dev/null"))
            |> ignore

            engines[languages] <- engine
            engine

    let gate = obj ()

    let box (rect: Rect) = {
        Left = int (float32 rect.X1 / scale)
        Top = int (float32 rect.Y1 / scale)
        Right = int (float32 rect.X2 / scale)
        Bottom = int (float32 rect.Y2 / scale)
    }

    let lineOf (line: Layout.TextLine) =
        match Option.ofObj line.Text, Option.ofNullable line.BoundingBox with
        | Some text, Some rect when not (String.IsNullOrWhiteSpace text) ->
            Some {
                Text = text.Trim()
                Box = box rect
            }
        | _ -> None

    let paragraphOf (paragraph: Layout.Paragraph) = paragraph.TextLines |> Seq.choose lineOf |> List.ofSeq

    let blockOf (block: Layout.Block) = { Paragraphs = block.Paragraphs |> Seq.map paragraphOf |> List.ofSeq }

    let recognize (bytes: byte[]) (sample: string) =
        let engine = engineFor (TesseractLanguages.forSample settings.Languages sample)
        use image = Pix.Image.LoadFromMemory bytes
        use scaled = image.Scale(scale, scale)
        use page = engine.Process(scaled, Nullable PageSegMode.Auto)
        let blocks = page.Layout |> Seq.map blockOf |> List.ofSeq

        {
            Area = {
                Left = 0
                Top = 0
                Right = image.Width
                Bottom = image.Height
            }
            Blocks = blocks
        }

    /// Whether the trained data is where the settings say
    member _.IsAvailable = Directory.Exists settings.TessdataPath

    interface IOcrEngine with
        member _.Recognize(image: byte[], sample: string) = lock gate (fun () -> recognize image sample)

        member _.Dispose() =
            lock gate (fun () ->
                for engine in engines.Values do
                    engine.Dispose()

                engines.Clear())
