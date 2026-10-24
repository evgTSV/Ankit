module Ankit.Server.Utils

open System.ComponentModel.DataAnnotations
open System.Text.Json
open System.Text.Json.Serialization

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
        .WithUnionTagNamingPolicy(JsonNamingPolicy.SnakeCaseUpper)
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
