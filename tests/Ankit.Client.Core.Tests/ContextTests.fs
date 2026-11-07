module Ankit.Client.Core.Tests.ContextTests

open Ankit.Client.Core.Context
open Xunit

/// A capture of the first occurrence of selection in text
let private capture (text: string) (selection: string) =
    let start = text.IndexOf selection
    Assert.True(start >= 0, $"\"%s{selection}\" isn't in the text")

    {
        Text = text
        SelectionStart = start
        SelectionLength = selection.Length
    }

let private extracted text selection =
    match extract (capture text selection) with
    | Some result ->
        Assert.Equal(result.Term, result.Context.Substring(result.TermStart, result.Term.Length))
        result
    | None -> failwith "Nothing was extracted"

let private sentenceTexts (text: string) =
    sentences text |> List.map (fun (s, l) -> text.Substring(s, l))

[<Fact>]
let ``Partial word is widened to the whole word`` () =
    let result = extracted "She kept running along the river." "unni"
    Assert.Equal("running", result.Term)

[<Fact>]
let ``Punctuation and spaces around the selection are dropped`` () =
    let result = extracted "He said: \"serendipity,\" and smiled." " \"serendipity,\" "
    Assert.Equal("serendipity", result.Term)

[<Fact>]
let ``Apostrophes and hyphens inside words are kept`` () =
    Assert.Equal("don't", (extracted "I don't know." "on").Term)
    Assert.Equal("well-known", (extracted "A well-known fact." "known").Term)

[<Fact>]
let ``Selection without letters gives nothing`` () = Assert.Equal(None, extract (capture "Wait... what?" "..."))

[<Fact>]
let ``Several selected words stay one term`` () =
    let result =
        extracted "They had to call off the meeting at the last minute." "call off"

    Assert.Equal("call off", result.Term)

[<Fact>]
let ``Text is split at sentence ends`` () =
    Assert.Equal<string list>(
        [ "It rained."; "We stayed home!"; "Did you?" ],
        sentenceTexts "It rained. We stayed home! Did you?"
    )

[<Fact>]
let ``Abbreviations and initials don't end a sentence`` () =
    Assert.Equal<string list>(
        [ "Dr. Smith met J. R. R. Tolkien, e.g. at a pub."; "Then they left." ],
        sentenceTexts "Dr. Smith met J. R. R. Tolkien, e.g. at a pub. Then they left."
    )

[<Fact>]
let ``Closing quotes stay with their sentence`` () =
    Assert.Equal<string list>([ "\"Is it late?\""; "She nodded." ], sentenceTexts "\"Is it late?\" She nodded.")

[<Fact>]
let ``A dot followed by a lowercase word doesn't end a sentence`` () =
    Assert.Equal<string list>(
        [ "Prices rose by approx. ten percent... and then fell." ],
        sentenceTexts "Prices rose by approx. ten percent... and then fell."
    )

[<Fact>]
let ``Decimals don't end a sentence`` () =
    Assert.Equal<string list>([ "Pi is 3.14 roughly."; "Yes." ], sentenceTexts "Pi is 3.14 roughly. Yes.")

[<Fact>]
let ``Full-width marks end a sentence without a space`` () =
    Assert.Equal<string list>([ "今日は雨です。"; "明日は晴れ？" ], sentenceTexts "今日は雨です。明日は晴れ？")

[<Fact>]
let ``A blank line ends a sentence, a single line break doesn't`` () =
    Assert.Equal<string list>(
        [ "A heading"; "The text wraps\nhere and ends." ],
        sentenceTexts "A heading\n\nThe text wraps\nhere and ends."
    )

[<Fact>]
let ``Context is the sentence with the term`` () =
    let text =
        "The committee met on Monday to discuss the budget for next year. Its members were reluctant to approve the new expenses without seeing a far more detailed plan first. Nobody expected a quick decision."

    let result = extracted text "reluctant"

    Assert.Equal(
        "Its members were reluctant to approve the new expenses without seeing a far more detailed plan first.",
        result.Context
    )

[<Fact>]
let ``A short sentence gets its neighbours`` () =
    let text =
        "The storm had raged all night and flooded the lower streets of the town. Then it stopped. The morning was quiet."

    let result = extracted text "stopped"

    Assert.Equal(
        "The storm had raged all night and flooded the lower streets of the town. Then it stopped.",
        result.Context
    )

[<Fact>]
let ``The occurrence that was selected is the one found`` () =
    let text =
        "A bank of the river. She went to the bank to open an account, pay her bills on time and ask about a loan for the house."

    let second = text.LastIndexOf "bank"

    let result =
        extract {
            Text = text
            SelectionStart = second
            SelectionLength = 4
        }
        |> Option.get

    Assert.StartsWith("She went to the bank", result.Context)
    Assert.Equal(16, result.TermStart)

[<Fact>]
let ``Whitespace in the context is collapsed`` () =
    let result =
        extracted "The   old\r\n  lighthouse\tkeeper waved at every passing ship from the rocky shore." "lighthouse"

    Assert.Equal("The old lighthouse keeper waved at every passing ship from the rocky shore.", result.Context)
    Assert.Equal(8, result.TermStart)

[<Fact>]
let ``A long sentence is cut around the term`` () =
    let words = List.init 200 (fun i -> $"word%d{i}") |> String.concat " "
    let text = words + " target " + words + "."

    let result =
        extractWith
            {
                MaxLength = 100
                MinLength = 10
            }
            (capture text "target")

    Assert.Equal("target", result.Value.Term)
    Assert.True(result.Value.Context.Length <= 102, $"%d{result.Value.Context.Length} characters")
    Assert.StartsWith("…", result.Value.Context)
    Assert.EndsWith("…", result.Value.Context)
    Assert.Equal("target", result.Value.Context.Substring(result.Value.TermStart, 6))

// The texts below are what UI Automation returned in Chromium (web pages, Edge's PDF viewer) and Qt apps

[<Fact>]
let ``Single line breaks between web page blocks end the context`` () =
    let text =
        "Hackatime is licensed under the MIT License. Go wild!\nAbout\nFree, open source, WakaTime-compatible coding time tracker\nhackatime.hackclub.com\nTopics"

    let result = extracted text "open source"
    Assert.Equal("Free, open source, WakaTime-compatible coding time tracker", result.Context)

[<Fact>]
let ``A block without an end mark is a sentence`` () =
    let text =
        "￼\nContributor\nExtracted exact game loop constants, physics formulas, and timing parameters from original binaries to achieve 100% interoperability and parity\nCloses #125\nCloses #131"

    let result = extracted text "binaries "

    Assert.Equal(
        "Extracted exact game loop constants, physics formulas, and timing parameters from original binaries to achieve 100% interoperability and parity",
        result.Context
    )

[<Fact>]
let ``Embedded objects are spaces and a short block gets no neighbours from other blocks`` () =
    let text = "￼\n￼\n￼\nThis is a promt in a textbox\nDeepThink\n￼\nSearch"
    let result = extracted text "promt"
    Assert.Equal("This is a promt in a textbox", result.Context)

[<Fact>]
let ``A hard-wrapped line continues in lowercase or after a colon`` () =
    let text =
        "One plausible definition is this:\ntype system is a tractable syntactic\nmethod for proving the absence of certain program behaviors."

    let result = extracted text "tractable"

    Assert.Equal(
        "One plausible definition is this: type system is a tractable syntactic method for proving the absence of certain program behaviors.",
        result.Context
    )

[<Fact>]
let ``A carriage return alone separates paragraphs`` () =
    let text =
        "Chapter One\rIt was a bright cold day in April, and the clocks were striking thirteen."

    let result = extracted text "clocks"
    Assert.Equal("It was a bright cold day in April, and the clocks were striking thirteen.", result.Context)

[<Fact>]
let ``A selection cutting words isn't aligned`` () =
    let text = "A type system is a tractable syntactic method for proving things."
    let at (selection: string) = isWordAligned (capture text selection)

    Assert.True(at "tractable syntactic method")
    Assert.False(at "stem is a tractable syntac")
    Assert.False(at "A type")
    Assert.False(at "things.")

[<Fact>]
let ``Relocating moves the selection to the nearest occurrence`` () =
    let text =
        "define a type system in general. One plausible definition is this:\nA type system is a tractable method."

    let reported = text.IndexOf "this:"

    let moved =
        relocate "type system" {
            Text = text
            SelectionStart = reported
            SelectionLength = 3
        }

    Assert.Equal(Some(text.LastIndexOf "type system"), moved |> Option.map _.SelectionStart)
    Assert.Equal(Some 11, moved |> Option.map _.SelectionLength)

[<Fact>]
let ``Relocating text that isn't there gives nothing`` () =
    Assert.Equal(None, relocate "absent" (capture "Some text here." "text"))

[<Fact>]
let ``Leading colons and bullets are dropped from the context`` () =
    let text =
        "1\n: to bring to a successful issue : COMPLETE, ACCOMPLISH\ncarried out the assignment\n￼\n￼\n2\n: to put into execution"

    let result = extracted text "carried out"
    Assert.Equal("to bring to a successful issue : COMPLETE, ACCOMPLISH carried out the assignment", result.Context)

[<Fact>]
let ``A bullet inside a paragraph starts a list item`` () =
    let text =
        "Mermaid diagrams and charts are now rendered using our new library Pele, compatible with the latest Mermaid 12.1.0 syntax. • Diagrams now appear faster and use the colors and fonts of your theme. • Diagrams are now responsive to the width of a note."

    let result = extracted text "appear"
    Assert.Equal("Diagrams now appear faster and use the colors and fonts of your theme.", result.Context)

    Assert.Equal<string list>(
        [
            "Mermaid diagrams and charts are now rendered using our new library Pele, compatible with the latest Mermaid 12.1.0 syntax."
            "Diagrams now appear faster and use the colors and fonts of your theme."
            "Diagrams are now responsive to the width of a note."
        ],
        sentenceTexts text
    )
