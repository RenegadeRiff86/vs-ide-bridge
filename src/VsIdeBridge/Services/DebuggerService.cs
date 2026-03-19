using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Threading.Tasks;
using System;
using VsIdeBridge.Infrastructure;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace VsIdeBridge.Services;

internal sealed class DebuggerService
{
    private const int DefaultBreakWaitTimeoutMilliseconds = 10_000;
    private const int DebuggerPollIntervalMilliseconds = 100;

    public async Task<JObject> GetStateAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var debugger = dte.Debugger;
        var debugState = new JObject
        {
            ["mode"] = debugger.CurrentMode.ToString(),
            ["currentProcess"] = debugger.CurrentProcess?.Name ?? string.Empty,
            ["processes"] = GetDebuggedProcessNames(debugger),
            ["threads"] = GetThreadSummaries(debugger.CurrentProgram),
        };

        if (debugger.CurrentMode == dbgDebugMode.dbgBreakMode && debugger.CurrentStackFrame is StackFrame frame)
        {
            debugState["currentStackFrame"] = new JObject
            {
                ["function"] = frame.FunctionName ?? string.Empty,
                ["language"] = frame.Language ?? string.Empty,
            };

            if (TryGetActiveSourceLocation(dte, out var filePath, out var lineNumber, out var columnNumber))
            {
                debugState["currentStackFrame"]!["file"] = filePath;
                debugState["currentStackFrame"]!["line"] = lineNumber;
                debugState["currentStackFrame"]!["column"] = columnNumber;
            }
        }

        return debugState;
    }

    public async Task<JObject> GetThreadsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var debugger = dte.Debugger;
        var threads = GetThreadSummaries(debugger.CurrentProgram);
        return new JObject
        {
            ["mode"] = debugger.CurrentMode.ToString(),
            ["count"] = threads.Count,
            ["threads"] = threads,
        };
    }

    public async Task<JObject> GetStackAsync(DTE2 dte, int? threadId, int maxFrames)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var debugger = dte.Debugger;
        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new CommandErrorException("not_in_break_mode", "Debugger is not currently in break mode.");
        }

        var frames = new JArray();
        var targetThread = ResolveThread(debugger.CurrentProgram, threadId) ?? throw new CommandErrorException("thread_not_found", $"Thread '{threadId}' was not found in the current debug program.");
        var limit = maxFrames <= 0 ? 100 : maxFrames;
        var collected = 0;
        foreach (StackFrame frame in targetThread.StackFrames)
        {
            if (collected >= limit)
            {
                break;
            }

            var frameInfo = new JObject
            {
                ["function"] = frame.FunctionName ?? string.Empty,
                ["language"] = frame.Language ?? string.Empty,
            };

            try
            {
                var lineValue = frame.GetType().GetProperty("LineNumber")?.GetValue(frame);
                if (lineValue is int lineNumber)
                    frameInfo["line"] = lineNumber;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Stack frame line read failed: {ex.Message}");
            }

            try
            {
                var fileValue = frame.GetType().GetProperty("FileName")?.GetValue(frame)?.ToString();
                if (!string.IsNullOrWhiteSpace(fileValue))
                    frameInfo["file"] = fileValue;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Stack frame file read failed: {ex.Message}");
            }

            frames.Add(frameInfo);
            collected++;
        }

        return new JObject
        {
            ["mode"] = debugger.CurrentMode.ToString(),
            ["threadId"] = targetThread.ID,
            ["count"] = frames.Count,
            ["frames"] = frames,
        };
    }

    public async Task<JObject> GetLocalsAsync(DTE2 dte, int maxItems)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var debugger = dte.Debugger;
        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode || debugger.CurrentStackFrame is not StackFrame frame)
        {
            throw new CommandErrorException("not_in_break_mode", "Debugger is not currently in break mode.");
        }

        var locals = new JArray();
        var limit = maxItems <= 0 ? 200 : maxItems;
        var count = 0;

        try
        {
            foreach (Expression expression in frame.Locals)
            {
                if (count >= limit) break;
                locals.Add(SerializeExpression(expression));
                count++;
            }
        }
        catch (System.Exception ex)
        {
            return new JObject
            {
                ["mode"] = debugger.CurrentMode.ToString(),
                ["count"] = locals.Count,
                ["locals"] = locals,
                ["warning"] = $"Failed to enumerate all locals: {ex.Message}",
            };
        }

        return new JObject
        {
            ["mode"] = debugger.CurrentMode.ToString(),
            ["count"] = locals.Count,
            ["locals"] = locals,
        };
    }

    public async Task<JObject> GetModulesAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var debugger = dte.Debugger;
        var modules = new JArray();
        var unsupportedReason = string.Empty;

        foreach (Process process in debugger.DebuggedProcesses)
        {
            var processInfo = new JObject
            {
                ["name"] = process.Name ?? string.Empty,
                ["id"] = process.ProcessID,
                ["modules"] = new JArray(),
            };

            try
            {
                var modulesProperty = process.GetType().GetProperty("Modules");
                var reason = TryPopulateProcessModules(processInfo, process, modulesProperty);
                if (!string.IsNullOrEmpty(reason))
                    unsupportedReason = reason;
            }
            catch (System.Exception ex) { unsupportedReason = ex.Message; }

            modules.Add(processInfo);
        }

        return new JObject
        {
            ["mode"] = debugger.CurrentMode.ToString(),
            ["count"] = modules.Count,
            ["items"] = modules,
            ["countKnown"] = string.IsNullOrWhiteSpace(unsupportedReason),
            ["reason"] = unsupportedReason,
        };
    }

    private static void PopulateProcessModules(JArray moduleItems, System.Collections.IEnumerable items)
    {
        foreach (var moduleEntry in items)
            moduleItems.Add(CreateModuleEntry(moduleEntry));
    }

    private static JObject CreateModuleEntry(object? moduleEntry)
    {
        return new JObject
        {
            ["name"] = moduleEntry?.GetType().GetProperty("Name")?.GetValue(moduleEntry)?.ToString() ?? string.Empty,
            ["path"] = moduleEntry?.GetType().GetProperty("Path")?.GetValue(moduleEntry)?.ToString() ?? string.Empty,
        };
    }

    private static string? TryPopulateProcessModules(JObject processInfo, object dteProcess, System.Reflection.PropertyInfo? modulesProperty)
    {
        if (modulesProperty?.GetValue(dteProcess) is not IEnumerable moduleItems)
            return "The active debugger engine does not expose modules via EnvDTE automation.";
        PopulateProcessModules((JArray)processInfo["modules"]!, moduleItems);
        return null;
    }

    private static void CollectExceptionGroups(JArray groups, IEnumerable exceptionGroups)
    {
        foreach (var group in exceptionGroups)
        {
            groups.Add(new JObject
            {
                ["name"] = group?.GetType().GetProperty("Name")?.GetValue(group)?.ToString() ?? string.Empty,
            });
        }
    }

    public async Task<JObject> EvaluateWatchAsync(DTE2 dte, string expression, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var debugger = dte.Debugger;
        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new CommandErrorException("not_in_break_mode", "Debugger is not currently in break mode.");
        }

        var timeout = timeoutMs <= 0 ? 1000 : timeoutMs;
        var debugResult = debugger.GetExpression(expression, true, timeout);

        var expressionData = SerializeExpression(debugResult);
        expressionData["expression"] = expression;
        expressionData["timeoutMs"] = timeout;
        return expressionData;
    }

    public async Task<JObject> GetExceptionsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var debugger = dte.Debugger;
        var groups = new JArray();
        string reason = string.Empty;

        try
        {
            var property = debugger.GetType().GetProperty("ExceptionGroups");
            if (property?.GetValue(debugger) is not IEnumerable exceptionGroups)
                reason = "Exception group automation is not supported by the active debugger engine.";
            else
                CollectExceptionGroups(groups, exceptionGroups);
        }
        catch (System.Exception ex)
        {
            reason = ex.Message;
        }

        return new JObject
        {
            ["count"] = groups.Count,
            ["groups"] = groups,
            ["featureNotSupported"] = groups.Count == 0 && !string.IsNullOrWhiteSpace(reason),
            ["reason"] = reason,
        };
    }

    public async Task<JObject> StartAsync(DTE2 dte, bool waitForBreak, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.Debugger.Go(false);
        return waitForBreak
            ? await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true)
            : await GetStateAsync(dte).ConfigureAwait(true);
    }

    public async Task<JObject> StopAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.Debugger.Stop(false);
        return await GetStateAsync(dte).ConfigureAwait(true);
    }

    public async Task<JObject> BreakAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.Debugger.Break(false);
        return await WaitForBreakOrDesignModeAsync(dte, DefaultBreakWaitTimeoutMilliseconds, throwOnTimeout: true).ConfigureAwait(true);
    }

    public async Task<JObject> ContinueAsync(DTE2 dte, bool waitForBreak, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.Debugger.Go(false);
        return waitForBreak
            ? await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true)
            : await GetStateAsync(dte).ConfigureAwait(true);
    }

    public async Task<JObject> StepOverAsync(DTE2 dte, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnsureBreakMode(dte);
        dte.Debugger.StepOver(false);
        return await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true);
    }

    public async Task<JObject> StepIntoAsync(DTE2 dte, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnsureBreakMode(dte);
        dte.Debugger.StepInto(false);
        return await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true);
    }

    public async Task<JObject> StepOutAsync(DTE2 dte, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnsureBreakMode(dte);
        dte.Debugger.StepOut(false);
        return await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true);
    }

    private static void EnsureBreakMode(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new CommandErrorException("not_in_break_mode", "Debugger is not currently in break mode.");
        }
    }

    private static JArray GetDebuggedProcessNames(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var items = new JArray();
        foreach (Process process in debugger.DebuggedProcesses)
        {
            items.Add(process.Name ?? string.Empty);
        }

        return items;
    }

    private static JArray GetThreadSummaries(Program? program)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var items = new JArray();
        if (program is null)
        {
            return items;
        }

        foreach (Thread thread in program.Threads)
        {
            items.Add(new JObject
            {
                ["id"] = thread.ID,
                ["name"] = thread.Name ?? string.Empty,
            });
        }

        return items;
    }

    private static Thread? ResolveThread(Program? program, int? threadId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (program is null)
        {
            return null;
        }

        foreach (Thread thread in program.Threads)
        {
            if (threadId is null || thread.ID == threadId.Value)
            {
                return thread;
            }
        }

        return null;
    }

    private static JObject SerializeExpression(Expression expression)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var expressionData = new JObject
        {
            ["name"] = expression.Name ?? string.Empty,
            ["type"] = expression.Type ?? string.Empty,
            ["value"] = expression.Value ?? string.Empty,
            ["isValid"] = expression.IsValidValue,
        };

        try
        {
            expressionData["dataMemberCount"] = expression.DataMembers?.Count ?? 0;
        }
        catch
        {
            expressionData["dataMemberCount"] = 0;
        }

        return expressionData;
    }

    private static bool TryGetActiveSourceLocation(DTE2 dte, out string filePath, out int lineNumber, out int columnNumber)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        filePath = string.Empty;
        lineNumber = 0;
        columnNumber = 0;

        if (dte.ActiveDocument?.Object("TextDocument") is not TextDocument textDocument)
        {
            return false;
        }

        var selection = textDocument.Selection;
        filePath = dte.ActiveDocument.FullName ?? string.Empty;
        lineNumber = selection.ActivePoint.Line;
        columnNumber = selection.ActivePoint.DisplayColumn;
        return !string.IsNullOrWhiteSpace(filePath);
    }

    private async Task<JObject> WaitForBreakOrDesignModeAsync(DTE2 dte, int timeoutMs, bool throwOnTimeout)
    {
        var timeout = timeoutMs <= 0 ? DefaultBreakWaitTimeoutMilliseconds : timeoutMs;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeout)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var mode = dte.Debugger.CurrentMode;
            if (mode != dbgDebugMode.dbgRunMode)
            {
                var breakState = await GetStateAsync(dte).ConfigureAwait(true);
                breakState["timeoutMs"] = timeout;
                breakState["waitedForBreak"] = true;
                breakState["timedOut"] = false;
                return breakState;
            }

            await Task.Delay(DebuggerPollIntervalMilliseconds).ConfigureAwait(true);
        }

        if (throwOnTimeout)
        {
            throw new CommandErrorException("timeout", $"Debugger did not reach break or design mode within {timeout} ms.");
        }

        var timedOutData = await GetStateAsync(dte).ConfigureAwait(true);
        timedOutData["timeoutMs"] = timeout;
        timedOutData["waitedForBreak"] = true;
        timedOutData["timedOut"] = true;
        return timedOutData;
    }
}
