using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class ErrorListService
{
    private static readonly Regex ExplicitCodePattern = new(
        @"\b(?:LINK|LNK|MSB|VCR|E|C)\d+\b|\blnt-[a-z0-9-]+\b|\bInt-make\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ReadinessService _readinessService;

    public ErrorListService(ReadinessService readinessService)
    {
        _readinessService = readinessService;
    }

    public async Task<JObject> GetErrorListAsync(IdeCommandContext context, bool waitForIntellisense, int timeoutMilliseconds)
    {
        if (waitForIntellisense)
        {
            await _readinessService.WaitForReadyAsync(context, timeoutMilliseconds).ConfigureAwait(true);
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var window = context.Dte.Windows
            .Cast<Window>()
            .FirstOrDefault(candidate => string.Equals(candidate.Caption, "Error List", StringComparison.OrdinalIgnoreCase));
        if (window?.Object is not ErrorList errorList)
        {
            throw new CommandErrorException("unsupported_operation", "Error List window is not available.");
        }

        var items = errorList.ErrorItems;
        var rows = new JArray();
        for (var i = 1; i <= items.Count; i++)
        {
            var item = items.Item(i);
            rows.Add(new JObject
            {
                ["severity"] = MapSeverity(item.ErrorLevel),
                ["code"] = InferCode(item.Description ?? string.Empty, item.Project ?? string.Empty, item.FileName ?? string.Empty, item.Line),
                ["message"] = item.Description ?? string.Empty,
                ["project"] = item.Project ?? string.Empty,
                ["file"] = item.FileName ?? string.Empty,
                ["line"] = item.Line,
                ["column"] = item.Column,
            });
        }

        return new JObject
        {
            ["count"] = rows.Count,
            ["rows"] = rows,
        };
    }

    private static string MapSeverity(vsBuildErrorLevel level)
    {
        return level switch
        {
            vsBuildErrorLevel.vsBuildErrorLevelHigh => "Error",
            vsBuildErrorLevel.vsBuildErrorLevelMedium => "Warning",
            _ => "Message",
        };
    }

    private static string InferCode(string description, string project, string fileName, int line)
    {
        var explicitCode = ExtractExplicitCode(description);
        if (!string.IsNullOrWhiteSpace(explicitCode))
        {
            return explicitCode;
        }

        if (description.IndexOf("identifier \"", StringComparison.OrdinalIgnoreCase) >= 0 &&
            description.IndexOf("\" is undefined", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "E0020";
        }

        if (description.IndexOf("can be made static", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "VCR003";
        }

        if (description.IndexOf("can be made const", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "VCR001";
        }

        if (description.IndexOf("PCH warning:", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Int-make";
        }

        if (description.IndexOf("cannot open file '", StringComparison.OrdinalIgnoreCase) >= 0 &&
            IsLinkerContext(project, fileName, line))
        {
            return "LNK1104";
        }

        return string.Empty;
    }

    private static string ExtractExplicitCode(string description)
    {
        var match = ExplicitCodePattern.Match(description);
        return match.Success ? NormalizeCode(match.Value) : string.Empty;
    }

    private static string NormalizeCode(string code)
    {
        if (code.StartsWith("LINK", StringComparison.OrdinalIgnoreCase) &&
            code.Length > 4 &&
            int.TryParse(code.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return "LNK" + code.Substring(4);
        }

        if (code.StartsWith("lnt-", StringComparison.OrdinalIgnoreCase))
        {
            return code.ToLowerInvariant();
        }

        return code.ToUpperInvariant();
    }

    private static bool IsLinkerContext(string project, string fileName, int line)
    {
        var normalizedFile = (fileName ?? string.Empty).Replace('/', '\\');
        if (normalizedFile.EndsWith("\\LINK", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(project) && string.IsNullOrWhiteSpace(fileName) && line <= 0;
    }
}
