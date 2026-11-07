/// The popup and the settings, as FuncUI components over the core's programs
module Ankit.Client.Desktop.Views

open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Elmish.ElmishHook
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Avalonia.Media
open Ankit.Client.Core
open Ankit.Client.Core.Api

let private label (text: string) : IView =
    TextBlock.create [
        TextBlock.text text
        TextBlock.fontWeight FontWeight.SemiBold
        TextBlock.margin (0.0, 8.0, 0.0, 2.0)
    ]

let private message (text: string) (isError: bool) : IView =
    TextBlock.create [
        TextBlock.text text
        TextBlock.textWrapping TextWrapping.Wrap
        TextBlock.verticalAlignment VerticalAlignment.Center
        if isError then
            TextBlock.foreground Brushes.IndianRed
    ]

let private field (text: string) (multiline: bool) (onChange: string -> unit) : IView =
    TextBox.create [
        TextBox.text text
        TextBox.onTextChanged onChange
        if multiline then
            TextBox.acceptsReturn true
            TextBox.textWrapping TextWrapping.Wrap
            TextBox.maxHeight 140.0
    ]

let private draftView (model: Popup.Model) (dispatch: Popup.Msg -> unit) (index: int) (draft: Popup.Draft) : IView =
    let names = Popup.fieldNames model draft.NoteTypeId

    Border.create [
        Border.borderThickness 1.0
        Border.borderBrush Brushes.Gray
        Border.cornerRadius 4.0
        Border.padding 8.0
        Border.margin (0.0, 6.0, 0.0, 0.0)
        Border.child (
            StackPanel.create [
                StackPanel.children [
                    CheckBox.create [
                        CheckBox.content (Popup.noteTypeName model draft.NoteTypeId)
                        CheckBox.isChecked draft.Selected
                        CheckBox.onClick (fun _ -> dispatch (Popup.ToggleDraft index))
                    ]
                    for i, value in List.indexed draft.Fields do
                        label (names |> List.tryItem i |> Option.defaultValue $"Field %d{i + 1}")
                        field value true (fun text -> dispatch (Popup.SetField(index, i, text)))
                ]
            ]
        )
    ]

let private statusText (model: Popup.Model) =
    match model.Status with
    | Popup.Status.Loading -> "Loading decks…", false
    | Popup.Status.Ready when model.Drafts.IsEmpty -> "", false
    | Popup.Status.Ready -> $"%d{model.Drafts |> List.filter _.Selected |> List.length} selected", false
    | Popup.Status.Suggesting -> "Asking for suggestions…", false
    | Popup.Status.Adding -> "Adding…", false
    | Popup.Status.Added count ->
        (if count = 1 then
             "Added 1 note"
         else
             $"Added %d{count} notes"),
        false
    | Popup.Status.Failed problem -> problem, true

/// The popup for a captured term; rememberDeck keeps the chosen deck for next time
let popup (api: IAnkitApi) (captured: Popup.Captured) (lastDeck: int64 option) (rememberDeck: int64 -> unit) =
    Component(fun ctx ->
        let init (captured, lastDeck) =
            let model, effects = Popup.init captured lastDeck
            model, Effects.popup api rememberDeck effects

        let update msg model =
            let model, effects = Popup.update msg model
            model, Effects.popup api rememberDeck effects

        let model, dispatch = ctx.useElmish (init, update, (captured, lastDeck))
        let status, isError = statusText model

        DockPanel.create [
            DockPanel.margin 12.0
            DockPanel.children [
                StackPanel.create [
                    StackPanel.dock Dock.Bottom
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 8.0
                    StackPanel.margin (0.0, 12.0, 0.0, 0.0)
                    StackPanel.children [
                        Button.create [
                            Button.content "Suggest"
                            Button.isEnabled (Popup.canSuggest model)
                            Button.onClick (fun _ -> dispatch Popup.Suggest)
                        ]
                        Button.create [
                            Button.content "Add"
                            Button.isEnabled (Popup.canAdd model)
                            Button.onClick (fun _ -> dispatch Popup.Add)
                        ]
                        message status isError
                    ]
                ]
                ScrollViewer.create [
                    ScrollViewer.content (
                        StackPanel.create [
                            StackPanel.children [
                                label "Term"
                                field model.Term false (Popup.SetTerm >> dispatch)
                                label "Context"
                                field model.Context true (Popup.SetContext >> dispatch)
                                match model.Source with
                                | Some source ->
                                    TextBlock.create [
                                        TextBlock.text source
                                        TextBlock.foreground Brushes.Gray
                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                    ]
                                | None -> ()
                                label "Deck"
                                ComboBox.create [
                                    ComboBox.horizontalAlignment HorizontalAlignment.Stretch
                                    ComboBox.dataItems model.Decks
                                    ComboBox.selectedItem (
                                        model.Decks |> List.tryFind (fun d -> Some d.Id = model.DeckId) |> Option.toObj
                                    )
                                    ComboBox.itemTemplate (
                                        DataTemplateView<Deck>.create (fun deck ->
                                            TextBlock.create [ TextBlock.text deck.Name ])
                                    )
                                    ComboBox.onSelectedItemChanged (fun item ->
                                        match item with
                                        | :? Deck as deck when Some deck.Id <> model.DeckId ->
                                            dispatch (Popup.SetDeck deck.Id)
                                        | _ -> ())
                                ]
                                if model.Existing > 0 then
                                    message
                                        (if model.Existing = 1 then
                                             "1 note already contains this term"
                                         else
                                             $"%d{model.Existing} notes already contain this term")
                                        false
                                for i, draft in List.indexed model.Drafts do
                                    draftView model dispatch i draft
                            ]
                        ]
                    )
                ]
            ]
        ])

/// The server settings; persist saves them and closes the window
let settings (current: Settings.ServerSettings) (persist: Settings.ServerSettings -> unit) =
    Component(fun ctx ->
        let init current =
            let model, effects = Settings.init current
            model, Effects.settings persist effects

        let update msg model =
            let model, effects = Settings.update msg model
            model, Effects.settings persist effects

        let model, dispatch = ctx.useElmish (init, update, current)

        let status, isError =
            match model.Status with
            | Settings.Status.Editing
            | Settings.Status.Saved -> "", false
            | Settings.Status.Testing -> "Connecting…", false
            | Settings.Status.Connected decks -> $"Connected: %d{decks} decks", false
            | Settings.Status.Failed problem -> problem, true

        StackPanel.create [
            StackPanel.margin 16.0
            StackPanel.children [
                label "Server address"
                TextBox.create [
                    TextBox.text model.Settings.Url
                    TextBox.placeHolderText "https://ankit.example.com"
                    TextBox.onTextChanged (Settings.SetUrl >> dispatch)
                ]
                label "API key"
                TextBox.create [
                    TextBox.text model.Settings.ApiKey
                    TextBox.passwordChar '•'
                    TextBox.onTextChanged (Settings.SetApiKey >> dispatch)
                ]
                StackPanel.create [
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 8.0
                    StackPanel.margin (0.0, 16.0, 0.0, 0.0)
                    StackPanel.children [
                        Button.create [
                            Button.content "Test"
                            Button.isEnabled (model.Status <> Settings.Status.Testing)
                            Button.onClick (fun _ -> dispatch Settings.Test)
                        ]
                        Button.create [ Button.content "Save"; Button.onClick (fun _ -> dispatch Settings.Save) ]
                        message status isError
                    ]
                ]
            ]
        ])
