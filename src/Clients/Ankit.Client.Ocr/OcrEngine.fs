namespace Ankit.Client.Ocr

open System
open Ankit.Client.Core.ScreenText

/// Text recognition on an image, whatever the engine: Tesseract everywhere, a platform's own engine later
[<Interface>]
type IOcrEngine =
    inherit IDisposable

    /// Reads a BMP or PNG image; boxes are in image pixels. The sample is text known to be in the image (the
    /// clipboard's selection), which an engine may use to choose languages
    abstract Recognize: image: byte[] * sample: string -> ScreenText
