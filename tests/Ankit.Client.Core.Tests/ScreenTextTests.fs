module Ankit.Client.Core.Tests.ScreenTextTests

open Ankit.Client.Core.ScreenText
open Xunit

let private lineHeight = 20

/// Lines of a paragraph starting at (left, top), one line every lineHeight pixels, 10 px per character
let private paragraph (left: int) (top: int) (lines: string list) =
    lines
    |> List.mapi (fun i text -> {
        Text = text
        Box = {
            Left = left
            Top = top + i * lineHeight
            Right = left + 10 * text.Length
            Bottom = top + i * lineHeight + 16
        }
    })

let private screen (blocks: ScreenLine list list list) = {
    Area = {
        Left = 0
        Top = 0
        Right = 1000
        Bottom = 500
    }
    Blocks = blocks |> List.map (fun paragraphs -> { Paragraphs = paragraphs })
}

let private selectionOf (located: Located) =
    let c = located.Capture
    c.Text.Substring(c.SelectionStart, c.SelectionLength)

[<Fact>]
let ``Words match in any case and across a line break, not inside longer words`` () =
    Assert.Equal<(int * int) list>([ 6, 7 ], matches "Gas oil" "Vegas gas\noil gasoline")
    Assert.Equal<(int * int) list>([ 6, 3; 18, 3 ], matches "gas" "Vegas gas, salts, gas")

[<Fact>]
let ``The match nearest to the anchor is the one taken`` () =
    let text =
        screen [
            [
                paragraph 0 100 [ "Natural gas is cheap." ]
                paragraph 0 300 [ "The region has oil, gas and salts." ]
            ]
        ]

    let located = locate "gas" (150, 310) text |> Option.get
    Assert.Equal(2, located.Matches)
    Assert.Equal("gas", selectionOf located)
    Assert.Equal(located.Capture.Text.LastIndexOf "gas", located.Capture.SelectionStart)

[<Fact>]
let ``The context is the block of the selection, not the columns next to it`` () =
    let text =
        screen [
            [ paragraph 0 100 [ "Ankit.Client.Core"; "Context.fs"; "paragraph.txt" ] ]
            [ paragraph 400 100 [ "A line break ends a line, or a"; "paragraph in Word." ] ]
        ]

    let located = locate "paragraph" (500, 120) text |> Option.get
    Assert.Equal("A line break ends a line, or a paragraph in Word.", located.Capture.Text)

[<Fact>]
let ``The clipboard's text replaces what was recognized`` () =
    let text = screen [ [ paragraph 0 200 [ "A tractable syntactlc method." ] ] ]
    let located = locate "TRACTABLE" (100, 210) text |> Option.get
    Assert.Equal("A TRACTABLE syntactlc method.", located.Capture.Text)

[<Fact>]
let ``A selection that wraps keeps the clipboard's single space`` () =
    let text = screen [ [ paragraph 0 200 [ "is a tractable"; "syntactic method" ] ] ]
    let located = locate "tractable syntactic" (100, 210) text |> Option.get
    Assert.Equal("is a tractable syntactic method", located.Capture.Text)
    Assert.Equal("tractable syntactic", selectionOf located)

[<Fact>]
let ``Paragraphs are separated by blank lines`` () =
    let text =
        screen [
            [
                paragraph 0 100 [ "Saved Messages" ]
                paragraph 0 200 [ "Perm has oil and gas." ]
            ]
        ]

    let located = locate "oil" (50, 210) text |> Option.get
    Assert.Equal("Saved Messages\n\nPerm has oil and gas.", located.Capture.Text)

[<Fact>]
let ``A paragraph at the edge of the area is cut`` () =
    let text =
        screen [
            [
                paragraph 0 100 [ "Away from the edges." ]
                paragraph 0 485 [ "At the bottom edge." ]
            ]
        ]

    Assert.False((locate "Away" (50, 110) text |> Option.get).CutByEdge)
    Assert.True((locate "bottom" (50, 490) text |> Option.get).CutByEdge)

[<Fact>]
let ``A selection that isn't there gives nothing`` () =
    Assert.Equal(None, locate "absent" (0, 0) (screen [ [ paragraph 0 100 [ "Some text." ] ] ]))

[<Fact>]
let ``Offset moves every box`` () =
    let moved = offset 10 20 (screen [ [ paragraph 0 100 [ "Text" ] ] ])
    Assert.Equal(10, moved.Area.Left)
    Assert.Equal(120, moved.Blocks.Head.Paragraphs.Head.Head.Box.Top)

[<Fact>]
let ``Lines of a paragraph are one text, even before an uppercase word`` () =
    let text =
        screen [
            [
                paragraph 0 100 [
                    "I'd like to tell you about my hometown, Perm. In fact, Perm (former"
                    "Molot - USSR) is not just a senseless and hollow word."
                ]
            ]
        ]

    let located = locate "hometown" (200, 110) text |> Option.get

    let context =
        Ankit.Client.Core.Context.extract located.Capture |> Option.map _.Context

    Assert.Equal(
        Some
            "I'd like to tell you about my hometown, Perm. In fact, Perm (former Molot - USSR) is not just a senseless and hollow word.",
        context
    )
