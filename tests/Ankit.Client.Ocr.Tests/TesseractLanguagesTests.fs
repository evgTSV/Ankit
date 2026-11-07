module Ankit.Client.Ocr.Tests.TesseractLanguagesTests

open Ankit.Client.Ocr
open Xunit

[<Fact>]
let ``Latin text is read with the Latin languages only`` () =
    Assert.Equal("eng", TesseractLanguages.forSample "eng+rus" "hometown")

[<Fact>]
let ``Cyrillic text is read with the Cyrillic languages only`` () =
    Assert.Equal("rus", TesseractLanguages.forSample "eng+rus" "принадлежит")

[<Fact>]
let ``Mixed text is read with both`` () =
    Assert.Equal("eng+rus", TesseractLanguages.forSample "eng+rus" "Пишет на F#")

[<Fact>]
let ``Without letters every language is used`` () =
    Assert.Equal("eng+rus", TesseractLanguages.forSample "eng+rus" "3.14")

[<Fact>]
let ``Languages of unknown script are kept`` () =
    Assert.Equal("eng+jpn", TesseractLanguages.forSample "eng+rus+jpn" "word")

[<Fact>]
let ``All languages are used when none fits`` () =
    Assert.Equal("rus", TesseractLanguages.forSample "rus" "word")
