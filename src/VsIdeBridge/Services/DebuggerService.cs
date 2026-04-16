using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        // Collect all DTE data under the narrowest possible main-thread scope.
        string modeString;
        string processName;
        JArray processes;
        JArray threads;
        string? frameFunctionName = null;
        string? frameLanguage = null;
        string? frameFilePath = null;
        int frameLineNumber = 0;
        int frameColumnNumber = 0;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Debugger debugger = dte.Debugger;
        dbgDebugMode mode = debugger.CurrentMode;
        modeString = mode.ToString();
        processName = debugger.CurrentProcess?.Name ?? string.Empty;
        processes = GetDebuggedProcessNames(debugger);
        threads = GetThreadSummaries(debugger.CurrentProgram);
        if (mode == dbgDebugMode.dbgBreakMode && debugger.CurrentStackFrame is StackFrame frame)
        {
            frameFunctionName = frame.FunctionName ?? string.Empty;
            frameLanguage = frame.Language ?? string.Empty;
            TryGetActiveSourceLocation(dte, out frameFilePath, out frameLineNumber, out frameColumnNumber);
        }
        await Task.Yield(); // release the main thread before building the response

        JObject debugState = new()
        {
            ["mode"] = modeString,
            ["currentProcess"] = processName,
            ["processes"] = processes,
            ["threads"] = threads,
        };

        if (frameFunctionName != null)
        {
            debugState["currentStackFrame"] = new JObject
            {
                ["function"] = frameFunctionName,
                ["language"] = frameLanguage ?? string.Empty,
            };
            if (!string.IsNullOrEmpty(frameFilePath))
            {
                debugState["currentStackFrame"]!["file"] = frameFilePath;
                debugState["currentStackFrame"]!["line"] = frameLineNumber;
                debugState["currentStackFrame"]!["column"] = frameColumnNumber;
            }
        }

        return debugState;
    }

    public async Task<JObject> GetThreadsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        Debugger debugger = dte.Debugger;
        JArray threads = GetThreadSummaries(debugger.CurrentProgram);
        return new JObject
        {
            ["mode"] = debugger.CurrentMode.ToString(),
            ["count"] = threads.Count,
            ["threads"] = threads,
        };
    }

    public async Task<JObject> GetStackAsync(DTE2 dte, int? threadId, int maxFrames)
    {
        // Collect stack frames on the main thread, then build the JObject off it.
        string modeString;
        int targetThreadId;
        List<(string function, string language, int? line, string? file)> rawFrames;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Debugger debugger = dte.Debugger;
        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new CommandErrorException("not_in_break_mode", "Debugger is not currently in break mode.");
        }

        modeString = debugger.CurrentMode.ToString();
        Thread targetThread = ResolveThread(debugger.CurrentProgram, threadId)
            ?? throw new CommandErrorException("thread_not_found", $"Thread '{threadId}' was not found in the current debug program.");
        targetThreadId = targetThread.ID;
        int limit = maxFrames <= 0 ? 100 : maxFrames;
        rawFrames = [];

        foreach (StackFrame stackFrame in targetThread.StackFrames)
        {
            if (rawFrames.Count >= limit)
                break;

            string func = stackFrame.FunctionName ?? string.Empty;
            string lang = stackFrame.Language ?? string.Empty;
            int? line = null;
            string? file = null;

            try
            {
                object? lineValue = stackFrame.GetType().GetProperty("LineNumber")?.GetValue(stackFrame);
                if (lineValue is int lineNumber)
                    line = lineNumber;
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Stack frame line read failed: {ex.Message}");
            }

            try
            {
                string? fileValue = stackFrame.GetType().GetProperty("FileName")?.GetValue(stackFrame)?.ToString();
                if (!string.IsNullOrWhiteSpace(fileValue))
                    file = fileValue;
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Stack frame file read failed: {ex.Message}");
            }

            rawFrames.Add((func, lang, line, file));
        }
        await Task.Yield(); // release the main thread

        JArray frames = [];
        foreach ((string func, string lang, int? lineNum, string? fileStr) in rawFrames)
        {
            JObject frameInfo = new()
            {
                ["function"] = func,
                ["language"] = lang,
            };
            if (lineNum.HasValue)
                frameInfo["line"] = lineNum.Value;
            if (!string.IsNullOrEmpty(fileStr))
                frameInfo["file"] = fileStr;
            frames.Add(frameInfo);
        }

        return new JObject
        {
            ["mode"] = modeString,
            ["threadId"] = targetThreadId,
            ["count"] = frames.Count,
            ["frames"] = frames,
        };
    }

    public async Task<JObject> GetLocalsAsync(DTE2 dte, int maxItems)
    {
        // Collect locals on the main thread, then build the response off it.
        string modeString;
        JArray locals = [];
        string? enumerationWarning = null;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Debugger debugger = dte.Debugger;
        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode || debugger.CurrentStackFrame is not StackFrame frame)
        {
            throw new CommandErrorException("not_in_break_mode", "Debugger is not currently in break mode.");
        }

        modeString = debugger.CurrentMode.ToString();
        int limit = maxItems <= 0 ? 200 : maxItems;
        int count = 0;

        try
        {
            foreach (Expression expression in frame.Locals)
            {
                if (count >= limit) break;
                locals.Add(SerializeExpression(expression));
                count++;
            }
        }
        catch (COMException ex)
        {
            enumerationWarning = $"Failed to enumerate all locals: {ex.Message}";
        }
        await Task.Yield(); // release the main thread

        JObject result = new()
        {
            ["mode"] = modeString,
            ["count"] = locals.Count,
            ["locals"] = locals,
        };

        if (enumerationWarning != null)
            result["warning"] = enumerationWarning;

        return result;
    }

    public async Task<JObject> GetModulesAsync(DTE2 dte)
    {
        // Collect module data on the main thread, then build the response off it.
        string modeString;
        JArray modules = [];
        string unsupportedReason = string.Empty;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Debugger debugger = dte.Debugger;
        modeString = debugger.CurrentMode.ToString();

        foreach (Process process in debugger.DebuggedProcesses)
        {
            JObject processInfo = new()
            {
                ["name"] = process.Name ?? string.Empty,
                ["id"] = process.ProcessID,
                ["modules"] = new JArray(),
            };

            try
            {
                System.Reflection.PropertyInfo? modulesProperty = process.GetType().GetProperty("Modules");
                string? reason = TryPopulateProcessModules(processInfo, process, modulesProperty);
                if (!string.IsNullOrEmpty(reason))
                    unsupportedReason = reason!;
            }
            catch (COMException ex) { unsupportedReason = ex.Message; }

            modules.Add(processInfo);
        }
        await Task.Yield(); // release the main thread

        return new JObject
        {
            ["mode"] = modeString,
            ["count"] = modules.Count,
            ["items"] = modules,
            ["countKnown"] = string.IsNullOrWhiteSpace(unsupportedReason),
            ["reason"] = unsupportedReason,
        };
    }

    private static void PopulateProcessModules(JArray moduleItems, System.Collections.IEnumerable items)
    {
        foreach (object? moduleEntry in items)
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
        foreach (object? group in exceptionGroups)
        {
            groups.Add(new JObject
            {
                ["name"] = group?.GetType().GetProperty("Name")?.GetValue(group)?.ToString() ?? string.Empty,
            });
        }
    }

    public async Task<JObject> EvaluateWatchAsync(DTE2 dte, string expression, int timeoutMs)
    {
        // Perform the evaluation on the main thread; build the result object off it.
        JObject expressionData;
        int timeout;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Debugger debugger = dte.Debugger;
        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new CommandErrorException("not_in_break_mode", "Debugger is not currently in break mode.");
        }

        timeout = timeoutMs <= 0 ? 1000 : timeoutMs;
        Expression debugResult = debugger.GetExpression(expression, true, timeout);
        expressionData = SerializeExpression(debugResult);
        await Task.Yield(); // release the main thread

        expressionData["expression"] = expression;
        expressionData["timeoutMs"] = timeout;
        return expressionData;
    }

    public async Task<JObject> GetExceptionsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        (JArray groups, string reason) = CollectExceptionGroupsOnUiThread(dte.Debugger);
        await Task.Yield(); // release the main thread

        return new JObject
        {
            ["count"] = groups.Count,
            ["groups"] = groups,
            ["featureNotSupported"] = groups.Count == 0 && !string.IsNullOrWhiteSpace(reason),
            ["reason"] = reason,
        };
    }

    private static (JArray Groups, string Reason) CollectExceptionGroupsOnUiThread(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        JArray groups = [];
        string reason = string.Empty;
        try
        {
            System.Reflection.PropertyInfo? property = debugger.GetType().GetProperty("ExceptionGroups");
            if (property?.GetValue(debugger) is not IEnumerable exceptionGroups)
                reason = "Exception group automation is not supported by the active debugger engine.";
            else
                CollectExceptionGroups(groups, exceptionGroups);
        }
        catch (COMException ex)
        {
            reason = ex.Message;
        }

        return (groups, reason);
    }

    public async Task<JObject> StartAsync(DTE2 dte, bool waitForBreak, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.Debugger.Go(false);
        return waitForBreak
            ? await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true)
            : await GetPostStartStateAsync(dte).ConfigureAwait(true);
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

        JArray items = [];
        foreach (Process process in debugger.DebuggedProcesses)
        {
            items.Add(process.Name ?? string.Empty);
        }

        return items;
    }

    private static JArray GetThreadSummaries(Program? program)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        JArray items = [];
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

        JObject expressionData = new()
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

        TextSelection selection = textDocument.Selection;
        filePath = dte.ActiveDocument.FullName ?? string.Empty;
        lineNumber = selection.ActivePoint.Line;
        columnNumber = selection.ActivePoint.DisplayColumn;
        return !string.IsNullOrWhiteSpace(filePath);
    }

    private async Task<JObject> WaitForBreakOrDesignModeAsync(DTE2 dte, int timeoutMs, bool throwOnTimeout)
    {
        int timeout = timeoutMs <= 0 ? DefaultBreakWaitTimeoutMilliseconds : timeoutMs;
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeout)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            dbgDebugMode mode = dte.Debugger.CurrentMode;
            await Task.Yield(); // release the main thread between polls

            if (mode != dbgDebugMode.dbgRunMode)
            {
                JObject breakState = await GetStateAsync(dte).ConfigureAwait(true);
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

        JObject timedOutData = await GetStateAsync(dte).ConfigureAwait(true);
        timedOutData["timeoutMs"] = timeout;
        timedOutData["waitedForBreak"] = true;
        timedOutData["timedOut"] = true;
        return timedOutData;
    }

    private async Task<JObject> GetPostStartStateAsync(DTE2 dte)
    {
        JObject state = await GetStateAsync(dte).ConfigureAwait(true);
        state["waitedForBreak"] = false;

        string mode = state["mode"]?.Value<string>() ?? string.Empty;
        string currentProcess = state["currentProcess"]?.Value<string>() ?? string.Empty;
        int processCount = state["processes"] is JArray processes ? processes.Count : 0;

        bool likelyStartupFailure = string.Equals(mode, dbgDebugMode.dbgDesignMode.ToString(), StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(currentProcess)
            && processCount == 0;

        state["likelyBuildOrLaunchFailure"] = likelyStartupFailure;
        if (likelyStartupFailure)
        {
            state["status"] = "did_not_start";
            state["guidance"] = "The debugger returned to design mode without launching a process. The startup build or launch likely failed. Read errors, warnings, messages, or diagnostics_snapshot for details.";
        }
        else
        {
            state["status"] = "started";
        }

        return state;
    }
}
