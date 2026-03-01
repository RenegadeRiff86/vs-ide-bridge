using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class BreakpointService
{
    public async Task<JObject> SetBreakpointAsync(
        DTE2 dte,
        string filePath,
        int line,
        int column,
        string? condition,
        string conditionType,
        int hitCount,
        string hitType)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        var existing = FindBreakpoint(dte, normalizedPath, line);
        if (existing is not null)
        {
            existing.Delete();
        }

        dte.Debugger.Breakpoints.Add(
            File: normalizedPath,
            Line: line,
            Column: column,
            Condition: condition ?? string.Empty,
            ConditionType: MapConditionType(conditionType),
            HitCount: hitCount,
            HitCountType: MapHitCountType(hitType));

        existing = FindBreakpoint(dte, normalizedPath, line);
        if (existing is null)
        {
            throw new CommandErrorException("internal_error", "Breakpoint was created but could not be resolved afterward.");
        }

        existing.Enabled = true;
        return SerializeBreakpoint(existing);
    }

    public async Task<JArray> ListBreakpointsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        return new JArray(dte.Debugger.Breakpoints.Cast<Breakpoint>().Select(SerializeBreakpoint));
    }

    public async Task<JObject> RemoveBreakpointAsync(DTE2 dte, string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        var matches = dte.Debugger.Breakpoints
            .Cast<Breakpoint>()
            .Where(breakpoint => PathNormalization.AreEquivalent(breakpoint.File, normalizedPath) && breakpoint.FileLine == line)
            .ToList();

        foreach (var breakpoint in matches)
        {
            breakpoint.Delete();
        }

        return new JObject
        {
            ["removedCount"] = matches.Count,
            ["remainingCount"] = dte.Debugger.Breakpoints.Count,
            ["file"] = normalizedPath,
            ["line"] = line,
        };
    }

    public async Task<JObject> ClearAllBreakpointsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var count = dte.Debugger.Breakpoints.Count;
        foreach (Breakpoint breakpoint in dte.Debugger.Breakpoints.Cast<Breakpoint>().ToList())
        {
            breakpoint.Delete();
        }

        return new JObject
        {
            ["removedCount"] = count,
            ["remainingCount"] = dte.Debugger.Breakpoints.Count,
        };
    }

    private static Breakpoint? FindBreakpoint(DTE2 dte, string normalizedPath, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return dte.Debugger.Breakpoints
            .Cast<Breakpoint>()
            .FirstOrDefault(breakpoint => PathNormalization.AreEquivalent(breakpoint.File, normalizedPath) && breakpoint.FileLine == line);
    }

    private static JObject SerializeBreakpoint(Breakpoint breakpoint)
    {
        return new JObject
        {
            ["file"] = breakpoint.File ?? string.Empty,
            ["line"] = breakpoint.FileLine,
            ["column"] = breakpoint.FileColumn,
            ["function"] = breakpoint.FunctionName ?? string.Empty,
            ["enabled"] = breakpoint.Enabled,
            ["condition"] = breakpoint.Condition ?? string.Empty,
            ["conditionType"] = breakpoint.ConditionType.ToString(),
            ["hitCountTarget"] = breakpoint.HitCountTarget,
            ["hitCountType"] = breakpoint.HitCountType.ToString(),
            ["name"] = breakpoint.Name ?? string.Empty,
        };
    }

    private static dbgBreakpointConditionType MapConditionType(string value)
    {
        return value switch
        {
            "changed" => dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenChanged,
            _ => dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
        };
    }

    private static dbgHitCountType MapHitCountType(string value)
    {
        return value switch
        {
            "equal" => dbgHitCountType.dbgHitCountTypeEqual,
            "multiple" => dbgHitCountType.dbgHitCountTypeMultiple,
            "greater-or-equal" => dbgHitCountType.dbgHitCountTypeGreaterOrEqual,
            _ => dbgHitCountType.dbgHitCountTypeNone,
        };
    }
}
