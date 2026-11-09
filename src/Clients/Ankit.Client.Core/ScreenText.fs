/// Text recognized on the screen, and the selection found in it: for apps whose text can only be read from pixels. The
/// clipboard gives the exact selection, the recognized text where it is and what's around it
module Ankit.Client.Core.ScreenText

open System
open Ankit.Client.Core.Context

/// In screen pixels
type Box = {
    Left: int
    Top: int
    Right: int
    Bottom: int
}

type ScreenLine = {
    Text: string
    Box: Box
}

/// A column or another region the OCR engine kept apart, e.g. a project tree next to an editor
type ScreenBlock = { Paragraphs: ScreenLine list list }

type ScreenText = {
    /// What was read
    Area: Box
    Blocks: ScreenBlock list
}

type Located = {
    /// The text of the block with the selection, the clipboard's text in place of what was recognized there
    Capture: Capture
    Matches: int
    /// The paragraph with the selection touches the top or the bottom of the area: reading more may complete it
    CutByEdge: bool
}

module Box =
    let center (box: Box) = (box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2

    let offset (dx: int) (dy: int) (box: Box) = {
        Left = box.Left + dx
        Top = box.Top + dy
        Right = box.Right + dx
        Bottom = box.Bottom + dy
    }

let offset (dx: int) (dy: int) (screen: ScreenText) = {
    Area = Box.offset dx dy screen.Area
    Blocks =
        screen.Blocks
        |> List.map (fun block -> {
            Paragraphs =
                block.Paragraphs
                |> List.map (List.map (fun line -> { line with Box = Box.offset dx dy line.Box }))
        })
}

type private Placed = {
    Start: int
    Line: ScreenLine
    Paragraph: ScreenLine list
}

/// The block's text: paragraphs separated by blank lines so that Context keeps them apart, lines by spaces. The OCR
/// engine found the paragraphs, so a line break inside one is a wrap, even before an uppercase word
let private layout (block: ScreenBlock) =
    let text = Text.StringBuilder()
    let placed = ResizeArray<Placed>()

    for paragraph in block.Paragraphs do
        let lines =
            paragraph |> List.filter (fun line -> not (String.IsNullOrWhiteSpace line.Text))

        if not lines.IsEmpty then
            if text.Length > 0 then
                text.Append "\n\n" |> ignore

            lines
            |> List.iteri (fun i line ->
                if i > 0 then
                    text.Append ' ' |> ignore

                placed.Add {
                    Start = text.Length
                    Line = line
                    Paragraph = lines
                }

                text.Append(line.Text.Trim()) |> ignore)

    text.ToString(), List.ofSeq placed

let private isWordChar (c: char) = Char.IsLetterOrDigit c

/// Where the words of selected occur in text, in any case and with any whitespace between them (a selection may wrap
/// to the next line), not inside longer words: (start, length)
let matches (selected: string) (text: string) =
    let words =
        selected.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray

    let same (a: char) (b: char) = Char.ToLowerInvariant a = Char.ToLowerInvariant b

    let rec matchWords (i: int) (words: string list) =
        match words with
        | [] -> Some i
        | word :: rest ->
            if
                i + word.Length <= text.Length
                && Seq.forall2 same word (text.Substring(i, word.Length))
            then
                let after = i + word.Length

                match rest with
                | [] -> Some after
                | _ ->
                    let mutable j = after

                    while j < text.Length && Char.IsWhiteSpace text[j] do
                        j <- j + 1

                    if j = after then None else matchWords j rest
            else
                None

    match words with
    | [] -> []
    | first :: _ ->
        let last = List.last words
        let startsWord = isWordChar first[0]
        let endsWord = isWordChar last[last.Length - 1]

        [
            for i in 0 .. text.Length - 1 do
                if not (startsWord && i > 0 && isWordChar text[i - 1]) then
                    match matchWords i words with
                    | Some e when not (endsWord && e < text.Length && isWordChar text[e]) -> yield i, e - i
                    | _ -> ()
        ]

/// The selection in the recognized text nearest to the anchor (the caret or the mouse cursor), with the text of its
/// block around it. Lines weigh more than columns: the anchor is on the selected line, not necessarily at the word
let locate (selected: string) (anchor: int * int) (screen: ScreenText) =
    let selected = selected.Trim()
    let ax, ay = anchor

    let candidates = [
        for block in screen.Blocks do
            let text, placed = layout block

            for start, length in matches selected text do
                let at = placed |> List.findBack (fun p -> p.Start <= start)

                let x, y = Box.center at.Line.Box
                let dx = float (x - ax)
                let dy = float (y - ay)
                yield (dx * dx + 16.0 * dy * dy), text, start, length, at
    ]

    match candidates with
    | [] -> None
    | _ ->
        let _, text, start, length, at = candidates |> List.minBy (fun (d, _, _, _, _) -> d)
        let lineHeight = at.Line.Box.Bottom - at.Line.Box.Top
        let top = (List.head at.Paragraph).Box.Top
        let bottom = (List.last at.Paragraph).Box.Bottom

        Some {
            Capture = {
                Text = text.Substring(0, start) + selected + text.Substring(start + length)
                SelectionStart = start
                SelectionLength = selected.Length
            }
            Matches = candidates.Length
            CutByEdge = top - screen.Area.Top < lineHeight || screen.Area.Bottom - bottom < lineHeight
        }
