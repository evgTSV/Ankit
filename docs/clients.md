# Clients

Ankit clients turn text you are reading into notes: select a word, press a hotkey (or pick an entry in the selection
menu), and a popup shows the suggestions of the server's `POST /suggestions` for that word in its sentence. They also
cover server settings and managing decks. The server stays the place for Anki and the LLM; clients are front ends.

Status: the shared context extraction and the Windows capture exist, with a probe to try the capture. The desktop app,
Android and the browser extension are planned. iOS is out of scope for now.

## Architecture

```
src/Clients/
  Ankit.Client.Core             F#, Fable-compatible: what every client shares
  Ankit.Client.Windows          Windows capture: global hotkey, UI Automation, clipboard
  Ankit.Client.Windows.Probe    console that prints what the capture gets, to try it in real apps
planned:
  Ankit.Client.Http             .NET implementation of the server API (HttpClient)
  Ankit.Client.Ui               Avalonia.FuncUI views over the core's Elmish programs
  Ankit.Client.Desktop          Avalonia desktop app: tray, hotkey, popup
  Ankit.Client.Android          activities and intent filters, minSdk 26
  browser-extension/            Fable + Vite, Manifest V3
```

- **F# and Elmish.** The core holds the logic as Elmish programs (model, messages, update): capture → suggest →
  review → save, decks, server settings. Platforms supply the views and adapters (HTTP, secure storage, capture).
- **Fable-compatible core.** The browser extension compiles the same core to JavaScript with Fable, so the core avoids
  .NET-only APIs: no `Task` (use `Async`), no System.Text.Json, no reflection. Its API models are plain records of its
  own, not the server's `Dto.fs` (DataAnnotations, `[<CLIMutable>]`); a test will check them against
  `/openapi/v1.json`.
- **Avalonia** draws the desktop and Android UI from the same views. The Android popup is a second, dialog-themed
  activity, which Avalonia's single-activity model has to be checked for before the Android work starts.

## Capturing the context

A note needs the term and the sentence it was read in. Each platform captures the selection and some text around it as
a `Context.Capture` (text, selection start and length); the core's `Context.extract` turns that into the term and its
context the same way everywhere:

- The selection is widened to whole words ("unni" → "running") and trimmed of punctuation and spaces; apostrophes and
  hyphens inside a word stay (don't, well-known).
- The text is split into sentences by rules: `. ! ? …` followed by a space, and `。！？` without one; closing quotes and
  brackets stay with their sentence; a dot after an abbreviation (Dr., e.g., z. B., т. е.) or an initial doesn't end a
  sentence, nor does an end mark followed by a lowercase word.
- Line breaks separate blocks (headings, list items, buttons: web pages put a single line break between them), except
  where the lines read as one hard-wrapped sentence, as in a PDF: the next line starts in lowercase or the previous one
  ends with `, ; : -`, and the next line has more than one word or ends a sentence. A blank line always separates.
  Embedded objects (U+FFFC) and zero-width characters count as spaces.
- The context is the sentence with the selection, whitespace collapsed. A sentence shorter than 80 characters gets
  the sentence before it, then the one after, from the same block only, while the context stays within 600; a longer
  sentence is cut around the term with ellipses. The server truncates context at 4000 characters, but a short one keeps the prompt focused.
- The result keeps where the term starts in the context, so the selected occurrence is the one shown when a word
  appears twice.

### Windows

Windows has no context menu for selected text in every app, so a global hotkey (`RegisterHotKey`, Ctrl+Alt+A for now)
starts the capture. The methods are tried in order:

1. **UI Automation.** The focused element, or its nearest ancestor with a text pattern (browsers focus a link or the
   document), gives the selection as a text range. The range expanded to its paragraph is the text around it; when that
   paragraph is short, possibly one line of a hard-wrapped PDF, two lines on each side join it. The selection's offset
   is the length of the text from the paragraph start to the selection, then checked against the text, since providers
   count line breaks and embedded objects differently. Password fields are skipped. Calls run on their own MTA thread
   with 1-second UI Automation timeouts, so a hung app can't hang the client.

   Some providers report a wrong selection: Edge's PDF viewer gives a range a few characters off, with the right text
   around it. A selection that cuts words (or touches an edge of the text, after one more paragraph on each side) is
   checked with the clipboard: the copied text is looked up in the UI Automation text, nearest to the reported offset.
   A wrong selection that happens to fall between words goes unnoticed.
2. **Clipboard.** Saves the clipboard, sends Ctrl+C once the hotkey's modifiers are released, reads the text, and puts
   the previous content back. It only gets the selection, so the context is whatever was selected; browsers add the
   page URL in the HTML format's `SourceURL`, which becomes the note's source (only http and https: Rider puts
   `about:blank`). Apps without UI Automation text, as tried with the probe: JetBrains IDEs (Java AWT), Telegram's
   message list (Qt; its input field has it), Acrobat Reader. The copy is the app's own, so it shows in
   the clipboard history (Win+V): this is why it is the fallback.
3. **Manual.** The popup takes a typed or pasted sentence and a click on the term. Selecting a whole sentence before
   the hotkey works with the clipboard in any app that can copy.

The source of a note is the URL when there is one, otherwise the window title and process.

To see what an app gives, run the probe, select text and press Ctrl+Alt+A:

```bash
dotnet run --project src/Clients/Ankit.Client.Windows.Probe
```

It prints both methods' results, the one the client would choose, and what `Context.extract` makes of them.

### Android

`ACTION_PROCESS_TEXT` adds an entry to the text selection menu and `ACTION_SEND` to the share sheet; both open the
popup activity. `ACTION_PROCESS_TEXT` only gives the selected words, not the sentence: the popup lets the user paste or
edit the context, and sharing a whole sentence works as on Windows' manual path. An accessibility service could read
the text around the selection, but Play restricts it and it reads every screen, so it's not planned.

### Browser extension

A `contextMenus` entry for selections and a content script: the selection's `Range` and the text of its enclosing
block element are the capture, so extension contexts are the same as desktop ones. The page URL is the source.

## Server

- Clients need an API key per device; today keys come from `./build New-Api-Key`, a pairing flow may follow.
- The Manifest V3 extension calls the server from extension pages with host permissions, which needs no CORS.
- Android blocks plain HTTP by default: self-hosted servers need HTTPS, or the app opts in to cleartext for the host the
  user configured.
