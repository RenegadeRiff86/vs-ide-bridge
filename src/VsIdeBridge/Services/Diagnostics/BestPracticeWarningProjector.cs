using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Services.Diagnostics;

internal static class BestPracticeWarningProjector
{
    public static JArray CreateResponseWarnings(IReadOnlyList<JObject> rows)
    {
        return CreateResponseWarnings(rows, null);
    }

    public static JArray CreateResponseWarnings(IReadOnlyList<JObject> rows, string? projectUniqueName)
    {
        JArray warnings = new JArray();

        foreach (JObject row in rows)
        {
            warnings.Add(CreateResponseWarning(row, projectUniqueName));
        }

        return warnings;
    }

    private static JObject CreateResponseWarning(JObject row, string? projectUniqueName)
    {
        string code = GetString(row, ErrorListConstants.CodeKey);
        string message = GetString(row, ErrorListConstants.MessageKey);
        string symbol = GetPrimarySymbol(row);
        string helpUri = GetHelpUri(code, GetString(row, ErrorListConstants.HelpUriKey));
        string project = string.IsNullOrWhiteSpace(projectUniqueName)
            ? GetString(row, ErrorListConstants.ProjectKey)
            : projectUniqueName ?? string.Empty;

        JObject warning = new JObject
        {
            [ErrorListConstants.SeverityKey] = row[ErrorListConstants.SeverityKey] ?? ErrorListConstants.WarningSeverity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.LineKey] = row[ErrorListConstants.LineKey],
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.GuidanceKey] = CreateGuidance(code, message),
            [ErrorListConstants.SuggestedActionKey] = GetSuggestedAction(code),
            [ErrorListConstants.SourceKey] = ErrorListConstants.BestPracticeCategory,
        };

        if (!string.IsNullOrWhiteSpace(project))
        {
            warning[ErrorListConstants.ProjectKey] = project;
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            warning[ErrorListConstants.SymbolKey] = symbol;
        }

        if (!string.IsNullOrWhiteSpace(helpUri))
        {
            warning[ErrorListConstants.HelpUriKey] = helpUri;
        }

        return warning;
    }

    private static string GetPrimarySymbol(JObject row)
    {
        JToken? symbolsToken = row[ErrorListConstants.SymbolsKey];
        if (symbolsToken is JArray symbols && symbols.Count > 0)
        {
            return symbols[0]?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string GetString(JObject row, string key)
    {
        return row[key]?.ToString() ?? string.Empty;
    }

    private static string CreateGuidance(string code, string message)
    {
        string suggestedAction = GetSuggestedAction(code);
        if (string.IsNullOrWhiteSpace(suggestedAction))
        {
            return message;
        }

        return string.Concat(message, " Next step: ", suggestedAction);
    }

    private static string GetSuggestedAction(string code)
    {
        return code switch
        {
            "BP1001" => "Extract the repeated string into a named constant or shared readonly value.",
            "BP1002" => "Replace the repeated number with a named constant that explains its meaning.",
            "BP1003" => "Use integer division or an explicit rounding helper so the intent is obvious.",
            "BP1004" => "Handle the exception explicitly by logging it, translating it, or rethrowing it.",
            "BP1005" => "Return Task instead of async void unless this is a real event handler.",
            "BP1006" => "Replace manual new/delete ownership with RAII or a smart pointer.",
            "BP1007" => "Remove the global using namespace from the header and qualify names instead.",
            "BP1008" => "Replace the C-style cast with a named cast that shows the conversion intent.",
            "BP1009" => "Log the exception details with the logger so failures stay observable.",
            "BP1010" => "Add a docstring and an explicit return contract so the function is easier to use.",
            "BP1011" => "Move imports to the top of the file and group them consistently.",
            "BP1012" => "Split the file into smaller focused types or helpers before adding more code.",
            "BP1013" => "Extract smaller methods so each block has one clear job.",
            "BP1014" => "Rename the symbol so its purpose is obvious without reading surrounding code.",
            "BP1015" => "Flatten the control flow with guard clauses, extracted helpers, or simpler branching.",
            "BP1016" => "Delete the dead code and rely on version control instead of commented-out blocks.",
            "BP1017" => "Normalize indentation to one style and reformat the file.",
            "BP1018" => "Split responsibilities into smaller classes before extending this type further.",
            "BP1019" => "Wrap the disposable resource in using or await using so cleanup is guaranteed.",
            "BP1020" => "Capture the time value once before the loop and reuse it inside the loop body.",
            "BP1021" => "Replace dynamic or object parameters with specific types or generics.",
            "BP1022" => "Use std::make_unique or std::make_shared instead of raw new.",
            "BP1023" => "Replace macros with constexpr values, inline functions, or templates.",
            "BP1024" => "Prefer composition over deep or wide inheritance.",
            "BP1025" => "Pass large values by const reference when you do not need a copy.",
            "BP1026" => "Use truthiness directly instead of comparing to True or False.",
            "BP1027" => "Move the shared state behind a focused service or model instead of growing an accessor-only class.",
            _ => "Fix the pattern before repeating it in more edits.",
        };
    }

    private static string GetHelpUri(string code, string existingHelpUri)
    {
        if (!string.IsNullOrWhiteSpace(existingHelpUri))
        {
            return existingHelpUri;
        }

        return code switch
        {
            "BP1001" => ErrorListConstants.BP1001HelpUri,
            "BP1002" => ErrorListConstants.BP1002HelpUri,
            "BP1003" => ErrorListConstants.BP1003HelpUri,
            "BP1004" => ErrorListConstants.BP1004HelpUri,
            "BP1005" => ErrorListConstants.BP1005HelpUri,
            "BP1006" => ErrorListConstants.BP1006HelpUri,
            "BP1007" => ErrorListConstants.BP1007HelpUri,
            "BP1008" => ErrorListConstants.BP1008HelpUri,
            "BP1009" => ErrorListConstants.BP1009HelpUri,
            "BP1010" => ErrorListConstants.BP1010HelpUri,
            "BP1011" => ErrorListConstants.BP1011HelpUri,
            "BP1012" => ErrorListConstants.BP1012HelpUri,
            "BP1013" => ErrorListConstants.BP1013HelpUri,
            "BP1014" => ErrorListConstants.BP1014HelpUri,
            "BP1015" => ErrorListConstants.BP1015HelpUri,
            "BP1016" => ErrorListConstants.BP1016HelpUri,
            "BP1017" => ErrorListConstants.BP1017HelpUri,
            "BP1018" => ErrorListConstants.BP1018HelpUri,
            "BP1019" => ErrorListConstants.BP1019HelpUri,
            "BP1020" => ErrorListConstants.BP1020HelpUri,
            "BP1021" => ErrorListConstants.BP1021HelpUri,
            "BP1022" => ErrorListConstants.BP1022HelpUri,
            "BP1023" => ErrorListConstants.BP1023HelpUri,
            "BP1024" => ErrorListConstants.BP1024HelpUri,
            "BP1025" => ErrorListConstants.BP1025HelpUri,
            "BP1026" => ErrorListConstants.BP1026HelpUri,
            "BP1027" => ErrorListConstants.BP1027HelpUri,
            _ => string.Empty,
        };
    }
}
