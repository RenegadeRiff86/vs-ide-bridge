using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Threading.Tasks;
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
        existing?.Delete();

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

    public async Task<JObject> ListBreakpointsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var items = new JArray(dte.Debugger.Breakpoints.Cast<Breakpoint>().Select(SerializeBreakpoint));
        return new JObject
        {
            ["count"] = items.Count,
            ["items"] = items,
        };
    }

    public async Task<JObject> RemoveBreakpointAsync(DTE2 dte, string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        var matches = dte.Debugger.Breakpoints
            .Cast<Breakpoint>()
            .Where(breakpoint => MatchesBreakpointLocation(breakpoint, normalizedPath, line))
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

    public async Task<JObject> EnableBreakpointAsync(DTE2 dte, string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        var bp = FindBreakpoint(dte, normalizedPath, line) ?? throw new CommandErrorException("not_found", $"No breakpoint found at {normalizedPath}:{line}");
        bp.Enabled = true;
        return SerializeBreakpoint(bp);
    }

    public async Task<JObject> DisableBreakpointAsync(DTE2 dte, string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        var bp = FindBreakpoint(dte, normalizedPath, line) ?? throw new CommandErrorException("not_found", $"No breakpoint found at {normalizedPath}:{line}");
        bp.Enabled = false;
        return SerializeBreakpoint(bp);
    }

    public async Task<JObject> EnableAllBreakpointsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var count = 0;
        foreach (Breakpoint bp in dte.Debugger.Breakpoints)
        {
            bp.Enabled = true;
            count++;
        }

        return new JObject
        {
            ["enabledCount"] = count,
            ["totalCount"] = dte.Debugger.Breakpoints.Count,
        };
    }

    public async Task<JObject> DisableAllBreakpointsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var count = 0;
        foreach (Breakpoint bp in dte.Debugger.Breakpoints)
        {
            bp.Enabled = false;
            count++;
        }

        return new JObject
        {
            ["disabledCount"] = count,
            ["totalCount"] = dte.Debugger.Breakpoints.Count,
        };
    }

    private static Breakpoint? FindBreakpoint(DTE2 dte, string normalizedPath, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return dte.Debugger.Breakpoints
            .Cast<Breakpoint>()
            .FirstOrDefault(breakpoint => MatchesBreakpointLocation(breakpoint, normalizedPath, line));
    }

    private static bool MatchesBreakpointLocation(Breakpoint breakpoint, string normalizedPath, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var file = breakpoint.File;
        var fileLine = breakpoint.FileLine;
        return PathNormalization.AreEquivalent(file, normalizedPath) && fileLine == line;
    }

    private static JObject SerializeBreakpoint(Breakpoint breakpoint)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var file = breakpoint.File ?? string.Empty;
        var line = breakpoint.FileLine;
        var column = breakpoint.FileColumn;
        var function = breakpoint.FunctionName ?? string.Empty;
        var enabled = breakpoint.Enabled;
        var condition = breakpoint.Condition ?? string.Empty;
        var conditionType = breakpoint.ConditionType.ToString();
        var hitCountTarget = breakpoint.HitCountTarget;
        var hitCountType = breakpoint.HitCountType.ToString();
        var name = breakpoint.Name ?? string.Empty;
        return new JObject
        {
            ["file"] = file,
            ["line"] = line,
            ["column"] = column,
            ["function"] = function,
            ["enabled"] = enabled,
            ["condition"] = condition,
            ["conditionType"] = conditionType,
            ["hitCountTarget"] = hitCountTarget,
            ["hitCountType"] = hitCountType,
            ["name"] = name,
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
