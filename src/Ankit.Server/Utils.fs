module Ankit.Server.Utils

open System.ComponentModel.DataAnnotations
open System.Text.Json
open System.Text.Json.Serialization

/// How union case names are written, e.g. FullUpload as "FULL_UPLOAD"; reading ignores the case
let unionTagNaming = JsonNamingPolicy.SnakeCaseUpper

let jsonOptions =
    let opts = JsonSerializerOptions()
    opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
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
        .WithSkippableOptionFields()
        .AddToJsonSerializerOptions(opts)

    opts

type NumericIdAttribute() =
    inherit ValidationAttribute("Must be a positive id")

    override this.IsValid(value) =
        match value with
        | :? int64 as i -> i >= 1L
        | :? int32 as i -> i >= 1
        | _ -> true
