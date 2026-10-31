/// Learning a foreign language: words and phrases in the context they were found in
module Ankit.Server.Modules.Language

open System
open Ankit.Server.Llm

[<Literal>]
let SettingsSection = "Llm:Modules:Language"

[<CLIMutable>]
type LanguageSettings = { NativeLanguage: string }

let create (settings: LanguageSettings) : SuggestionModule =
    let nativeLanguage =
        if isNull (box settings) then
            None
        else
            Option.ofObj settings.NativeLanguage
            |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let inLanguage =
        match nativeLanguage with
        | Some language -> $"in %s{language}, or in simple words of the language being learned"
        | None -> "in simple words of the language being learned"

    let guidedCloze = {
        Id = "GUIDED_CLOZE"
        Name = "Guided cloze"
        Kind = NoteKind.Cloze
        Description = "for a high-value word (a verb, adjective or adverb) best learned in its natural context"
        Rules = [
            $"In the field for the text, write a natural sentence (from the context when there is one) and replace the term with {{{{c1::term::hint}}}}. Every deletion has a hint, %s{inLanguage}. The hint MUST identify the word uniquely, so that none of its close synonyms fits it."
            "In the remaining fields, the back of the card, give the full sentence, the IPA of the term and its common collocation, separated by <br>."
        ]
        Accepts = List.exists CardHtml.hasHints
        Rejection = "no note with a hint in every cloze deletion"
    }

    let collocationInContext = {
        Id = "COLLOCATION_IN_CONTEXT"
        Name = "Collocation in context"
        Kind = NoteKind.FrontBack
        Description =
            "for a term worth learning for the words it naturally combines with, the card teaches a common collocation of it"
        Rules = [
            "On the front (the first field), write a natural sentence (from the context when there is one) with a common collocation of the term, the whole collocation in <b>bold</b>: \"She <b>made a decision</b> quickly.\""
            $"On the back (the remaining fields), give the collocation, its meaning %s{inLanguage}, and one more example sentence with it, separated by <br>."
        ]
        Accepts = Styles.frontAndBack
        Rejection = "no note with a front and a back"
    }

    {
        Id = "LANGUAGE"
        Description = "Learning a foreign language: words and phrases in context, with clozes and collocations"
        Context = [
            "The learner studies a foreign language, the term is a word or phrase of that language."
            match nativeLanguage with
            | Some language -> $"The learner's native language is %s{language}."
            | None -> ()
        ]
        Styles = [ guidedCloze; collocationInContext ]
    }
