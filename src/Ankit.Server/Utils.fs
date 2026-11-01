module Ankit.Server.Utils

open System.ComponentModel.DataAnnotations
open System.Text.Json
open System.Text.Json.Serialization

/// How union case names are written, e.g. FullUpload as "FULL_UPLOAD"; reading ignores the case
let unionTagNaming = JsonNamingPolicy.SnakeCaseUpper

let jsonOptions =
    let opts = JsonSerializerOptions()
    opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
    opts.AllowDuplicateProperties <- false
    opts.AllowTrailingCommas <- false
    opts.PropertyNameCaseInsensitive <- true
    opts.WriteIndented <- true

    JsonFSharpOptions
        .Default()
        .WithUnionUnwrapFieldlessTags()
        .WithUnionTagNamingPolicy(unionTagNaming)
        .WithUnionTagCaseInsensitive()
        .WithUnionNamedFields()
        .WithUnionTagName("code")
        .WithUnionInternalTag()
        .WithSkippableOptionFields(SkippableOptionFields.FromJsonSerializerOptions)
        .AddToJsonSerializerOptions(opts)

    opts

/// For an optional count, None passes
type CountRangeAttribute(min: int, max: int) =
    inherit ValidationAttribute($"Must be between {min} and {max}")

    override this.IsValid(value) =
        match value with
        | :? option<int> as count -> count |> Option.forall (fun n -> n >= min && n <= max)
        | :? int as n -> n >= min && n <= max
        | _ -> true

type NumericIdAttribute() =
    inherit ValidationAttribute("Must be a positive id")

    override this.IsValid(value) =
        match value with
        | :? int64 as i -> i >= 1L
        | :? int32 as i -> i >= 1
        | :? option<int64> as id -> id |> Option.forall (fun i -> i >= 1L)
        | _ -> true
