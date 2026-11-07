/// The term and the sentence around it, from text captured around a selection. Pure and Fable-compatible: every client
/// (desktop, Android, browser extension) extracts the context the same way
module Ankit.Client.Core.Context

open System

/// Text around a selection, as a platform captured it
type Capture = {
    /// The selection and some text before and after it, e.g. its paragraph
    Text: string
    /// Where the selection starts in Text
    SelectionStart: int
    SelectionLength: int
}

type Extracted = {
    /// The selection widened to whole words, without surrounding punctuation
    Term: string
    /// The sentence with the term, whitespace collapsed; may include the sentence before or after it
    Context: string
    /// Where the term starts in Context
    TermStart: int
}

type Limits = {
    /// The context stops growing past this length; a longer sentence is cut around the term
    MaxLength: int
    /// A shorter sentence gets its neighbours from the same block
    MinLength: int
}

module Limits =
    let defaults = {
        MaxLength = 600
        MinLength = 80
    }

let private sentenceEnds = set [ '.'; '!'; '?'; '…'; '。'; '！'; '？' ]

/// CJK end marks need no space after them
let private fullWidthEnds = set [ '。'; '！'; '？' ]

let private leadingMarks = set [ ':'; ';'; ','; '-'; '–'; '—'; '•'; '·'; '*' ]

let private closers = set [ '"'; '\''; '”'; '’'; '»'; ')'; ']'; '」'; '』' ]

/// Lowercase, without the final dot: English, German, French and Spanish, Russian
let private abbreviations =
    [
        "mr mrs ms dr prof st jr sr vs etc e.g i.e cf approx no fig vol p pp ch ed inc ltd co"
        "jan feb mar apr jun jul aug sep sept oct nov dec"
        "z.b u.a d.h bzw usw ca vgl nr s str"
        "mme mlle sra srta ud uds av"
        "т.е т.д т.п т.к и.т.д г гг ул им стр см др"
    ]
    |> List.collect (fun line -> line.Split ' ' |> List.ofArray)
    |> set

/// A line break ends a line, or a paragraph in Word ('\r' alone)
let private isLineBreak (text: string) (i: int) =
    match text[i] with
    | '\n'
    | ' '
    | '\u000B' -> true
    | '\r' -> i + 1 = text.Length || text[i + 1] <> '\n'
    | _ -> false

/// Whitespace, and what UI Automation puts for embedded objects (U+FFFC) or zero-width characters
let private isSpace (c: char) =
    Char.IsWhiteSpace c || c = '￼' || c = '​' || c = '‌' || c = '‍' || c = '﻿'

let private isJoiner (c: char) = c = '\'' || c = '’' || c = '-' || c = '‐'

/// Letters and digits, and an apostrophe or hyphen between them (don't, well-known)
let private isWordAt (text: string) (i: int) =
    let c = text[i]

    Char.IsLetterOrDigit c
    || isJoiner c
       && i > 0
       && i + 1 < text.Length
       && Char.IsLetterOrDigit text[i - 1]
       && Char.IsLetterOrDigit text[i + 1]

/// The selection widened to whole words and trimmed of punctuation and spaces; None without a letter or digit
let termBounds (text: string) (start: int) (length: int) =
    let start = max 0 (min start text.Length)
    let finish = max start (min (start + length) text.Length)
    let mutable s = start
    let mutable e = finish

    while s < e && not (isWordAt text s) do
        s <- s + 1

    while e > s && not (isWordAt text (e - 1)) do
        e <- e - 1

    if s = e then
        None
    else
        while s > 0 && isWordAt text (s - 1) do
            s <- s - 1

        while e < text.Length && isWordAt text e do
            e <- e + 1

        Some(s, e - s)

/// Whether the selection starts and ends between words. Some providers, e.g. Edge's PDF viewer, report a selection
/// off by a few characters, which mostly cuts words; a selection at an edge of the text can't be checked
let isWordAligned (capture: Capture) =
    let text = capture.Text
    let s = capture.SelectionStart
    let e = s + capture.SelectionLength

    let cut i =
        i > 0 && i < text.Length && isWordAt text (i - 1) && isWordAt text i

    s > 0 && e < text.Length && not (cut s) && not (cut e)

/// The capture with the selection moved to the occurrence of selected nearest to where it was; None when the text
/// doesn't contain it. For a selection whose text is known (e.g. from the clipboard) but whose position isn't
let relocate (selected: string) (capture: Capture) =
    let selected = selected.Trim()
    let text = capture.Text

    if selected.Length = 0 then
        None
    else
        let rec occurrences from acc =
            if from > text.Length then
                List.rev acc
            else
                match text.IndexOf(selected, from, StringComparison.Ordinal) with
                | -1 -> List.rev acc
                | found -> occurrences (found + 1) (found :: acc)

        match occurrences 0 [] with
        | [] -> None
        | found ->
            let nearest = found |> List.minBy (fun i -> abs (i - capture.SelectionStart))

            Some {
                capture with
                    SelectionStart = nearest
                    SelectionLength = selected.Length
            }

/// The word ending right before position i, lowercase, with inner dots (e.g, z.b)
let private wordBefore (text: string) (i: int) =
    let mutable s = i

    while s > 0 && (Char.IsLetter text[s - 1] || text[s - 1] = '.') do
        s <- s - 1

    text.Substring(s, i - s).Trim('.').ToLowerInvariant()

let private nextVisible (text: string) (i: int) =
    let mutable j = i

    while j < text.Length && (isSpace text[j] || closers.Contains text[j]) do
        j <- j + 1

    if j < text.Length then Some text[j] else None

/// A dot after an abbreviation or an initial (J. R. R. Tolkien) doesn't end a sentence
let private isAbbreviation (text: string) (dot: int) =
    let word = wordBefore text dot
    abbreviations.Contains word || word.Length = 1 && Char.IsUpper text[dot - 1]

/// A line break that separates blocks: a blank line, a paragraph separator, or one between lines that don't read as one
/// wrapped sentence. Web pages separate headings, list items and buttons by single line breaks, while a hard-wrapped
/// text (a PDF) continues on the next line in lowercase or after a comma
let private isBlockBreak (text: string) (i: int) =
    if text[i] = ' ' then
        true
    elif not (isLineBreak text i) then
        false
    else
        let mutable p = i - 1

        while p >= 0 && isSpace text[p] && not (isLineBreak text p) do
            p <- p - 1

        let mutable n = i + 1

        while n < text.Length && isSpace text[n] && not (isLineBreak text n) do
            n <- n + 1

        let blankAfter = n < text.Length && isLineBreak text n
        let blankBefore = p >= 0 && isLineBreak text p

        if blankAfter || blankBefore || p < 0 || n >= text.Length then
            true
        else
            // A one-word line without an end mark is a link, a label or a tag rather than the rest of a sentence
            let mutable lineEnd = n

            while lineEnd < text.Length && not (isLineBreak text lineEnd) do
                lineEnd <- lineEnd + 1

            let line = text.Substring(n, lineEnd - n).TrimEnd()
            let sentenceLike = line.Contains ' ' || sentenceEnds.Contains line[line.Length - 1]

            let continues =
                text[p] = ','
                || text[p] = ';'
                || text[p] = ':'
                || text[p] = '-'
                || text[p] = '‐'

            not (sentenceLike && (continues || Char.IsLower text[n]))

/// Sentences as (start, length, block): without the whitespace between them, and numbered by the block they are in
let private segments (text: string) =
    let spans = ResizeArray<int * int * int>()
    let mutable start = 0
    let mutable block = 0
    let mutable i = 0

    let add finish =
        let mutable s = start
        let mutable e = finish

        while s < e && isSpace text[s] do
            s <- s + 1

        while e > s && isSpace text[e - 1] do
            e <- e - 1

        if e > s then
            spans.Add(s, e - s, block)

        start <- finish

    while i < text.Length do
        let c = text[i]

        if isBlockBreak text i then
            add i
            block <- block + 1
            i <- i + 1
        elif sentenceEnds.Contains c then
            let mark = i
            let mutable j = i + 1

            while j < text.Length && sentenceEnds.Contains text[j] do
                j <- j + 1

            while j < text.Length && closers.Contains text[j] do
                j <- j + 1

            let spaced = j = text.Length || isSpace text[j]

            let ends =
                (spaced || fullWidthEnds.Contains c)
                && not (c = '.' && j = mark + 1 && isAbbreviation text mark)
                && not (nextVisible text j |> Option.exists Char.IsLower)

            if ends then
                add j

            i <- j
        else
            i <- i + 1

    add text.Length
    Array.ofSeq spans

/// Where the sentences start and end, (start, length) without the whitespace between them
let sentences (text: string) =
    segments text |> Array.map (fun (s, l, _) -> s, l) |> List.ofArray

/// Collapses whitespace runs into one space and moves the term start along
let private collapse (text: string) (termStart: int) =
    let out = Text.StringBuilder()
    let mutable mapped = termStart
    let mutable space = false

    for i in 0 .. text.Length - 1 do
        if i = termStart then
            mapped <- out.Length + (if space && out.Length > 0 then 1 else 0)

        if isSpace text[i] then
            space <- true
        else
            if space && out.Length > 0 then
                out.Append ' ' |> ignore

            space <- false
            out.Append text[i] |> ignore

    out.ToString(), mapped

/// A window of at most maxLength around the term, cut at spaces, with an ellipsis where text was dropped
let private window (maxLength: int) (text: string) (termStart: int) (termLength: int) =
    if text.Length <= maxLength then
        text, termStart
    else
        let length = max maxLength termLength
        let room = length - termLength
        let mutable s = max 0 (termStart - room / 2)
        let mutable e = min text.Length (s + length)
        s <- max 0 (e - length)

        if s > 0 then
            match text.IndexOf(' ', s) with
            | space when space >= 0 && space < termStart -> s <- space + 1
            | _ -> ()

        if e < text.Length then
            match text.LastIndexOf(' ', e - 1) with
            | space when space >= termStart + termLength -> e <- space
            | _ -> ()

        let prefix = if s > 0 then "…" else ""
        let suffix = if e < text.Length then "…" else ""
        prefix + text.Substring(s, e - s) + suffix, termStart - s + prefix.Length

/// The term and its sentence; a short sentence gets the sentences next to it in its block while the context fits
let extractWith (limits: Limits) (capture: Capture) =
    let text = capture.Text

    termBounds text capture.SelectionStart capture.SelectionLength
    |> Option.map (fun (termStart, termLength) ->
        let spans = segments text

        let containing position =
            spans
            |> Array.tryFindIndex (fun (s, l, _) -> position < s + l)
            |> Option.defaultValue (spans.Length - 1)

        let startOf i =
            let s, _, _ = spans[i]
            s

        let endOf i =
            let s, l, _ = spans[i]
            s + l

        let blockOf i =
            let _, _, b = spans[i]
            b

        let mutable first = containing termStart
        let mutable last = containing (termStart + termLength - 1)
        let length () = endOf last - startOf first
        let mutable growing = true

        while growing && length () < limits.MinLength do
            growing <- false

            if
                first > 0
                && blockOf (first - 1) = blockOf first
                && endOf last - startOf (first - 1) <= limits.MaxLength
            then
                first <- first - 1
                growing <- true

            if
                length () < limits.MinLength
                && last + 1 < spans.Length
                && blockOf (last + 1) = blockOf last
                && endOf (last + 1) - startOf first <= limits.MaxLength
            then
                last <- last + 1
                growing <- true

        let mutable s = min (startOf first) termStart

        // Dictionaries and lists start lines with ": " or a bullet
        while s < termStart && (leadingMarks.Contains text[s] || isSpace text[s]) do
            s <- s + 1

        let e = max (endOf last) (termStart + termLength)
        let context, start = collapse (text.Substring(s, e - s)) (termStart - s)
        let term, _ = collapse (text.Substring(termStart, termLength)) 0
        let context, start = window limits.MaxLength context start term.Length

        {
            Term = term
            Context = context
            TermStart = start
        })

let extract (capture: Capture) = extractWith Limits.defaults capture
