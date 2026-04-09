namespace VsIdeBridge.Shared;

public static partial class BridgeCommandCatalog
{
    private static bool TryGetDebugCommandDetail(string commandName, out (string Description, string Example) detail)
    {
        if (TryGetBreakpointCommandDetail(commandName, out detail))
        {
            return true;
        }

        if (TryGetDebuggerExecutionCommandDetail(commandName, out detail))
        {
            return true;
        }

        if (TryGetDiagnosticsAndBuildCommandDetail(commandName, out detail))
        {
            return true;
        }

        detail = default;
        return false;
    }

    private static bool TryGetBreakpointCommandDetail(string commandName, out (string Description, string Example) detail)
    {
        switch (commandName)
        {
            case "set-breakpoint":
                detail = ("Set a breakpoint at file/line with optional condition, hit count, and tracepoint behavior. Prefer a unique bare filename like 'BuildService.cs'; use a longer path only when needed.", ExampleCommand("set-breakpoint", @"{""file"":""BuildService.cs"",""line"":42}"));
                return true;
            case "list-breakpoints":
                detail = ("List current breakpoints.", commandName);
                return true;
            case "remove-breakpoint":
                detail = ("Remove breakpoints by file/line, id, or all. Prefer a unique bare filename like 'BuildService.cs'; use a longer path only when needed.", ExampleCommand("remove-breakpoint", @"{""file"":""BuildService.cs"",""line"":42}"));
                return true;
            case "clear-breakpoints":
                detail = ("Clear all breakpoints.", commandName);
                return true;
            case "enable-breakpoint":
                detail = ("Enable a breakpoint by id or file/line. Prefer a unique bare filename like 'BuildService.cs'; use a longer path only when needed.", ExampleCommand("enable-breakpoint", @"{""file"":""BuildService.cs"",""line"":42}"));
                return true;
            case "disable-breakpoint":
                detail = ("Disable a breakpoint by id or file/line. Prefer a unique bare filename like 'BuildService.cs'; use a longer path only when needed.", ExampleCommand("disable-breakpoint", @"{""file"":""BuildService.cs"",""line"":42}"));
                return true;
            case "enable-all-breakpoints":
                detail = ("Enable all breakpoints.", commandName);
                return true;
            case "disable-all-breakpoints":
                detail = ("Disable all breakpoints.", commandName);
                return true;
            default:
                detail = default;
                return false;
        }
    }

    private static bool TryGetDebuggerExecutionCommandDetail(string commandName, out (string Description, string Example) detail)
    {
        switch (commandName)
        {
            case "debug-state":
                detail = ("Get debugger mode and active stack frame info.", commandName);
                return true;
            case "debug-start":
                detail = ("Start debugging the current startup project.", commandName);
                return true;
            case "debug-stop":
                detail = ("Stop the debugger.", commandName);
                return true;
            case "debug-break":
                detail = ("Break execution in the debugger.", commandName);
                return true;
            case "debug-continue":
                detail = ("Continue execution in the debugger.", commandName);
                return true;
            case "debug-step-over":
                detail = ("Step over the current line in the debugger.", commandName);
                return true;
            case "debug-step-into":
                detail = ("Step into the current call in the debugger.", commandName);
                return true;
            case "debug-step-out":
                detail = ("Step out of the current function in the debugger.", commandName);
                return true;
            case "debug-threads":
                detail = ("List debugger threads for the active debug session.", commandName);
                return true;
            case "debug-stack":
                detail = ("Capture stack frames for the current or selected debugger thread.", ExampleCommand("debug-stack", @"{""thread_id"":1,""max_frames"":50}"));
                return true;
            case "debug-locals":
                detail = ("Capture local variables for the active stack frame.", ExampleCommand("debug-locals", @"{""max"":200}"));
                return true;
            case "debug-modules":
                detail = ("Capture debugger module snapshot (best effort by debugger engine).", commandName);
                return true;
            case "debug-watch":
                detail = ("Evaluate one debugger watch expression in break mode.", ExampleCommand("debug-watch", @"{""expression"":""count""}"));
                return true;
            case "debug-exceptions":
                detail = ("Capture debugger exception group/settings snapshot (best effort).", commandName);
                return true;
            default:
                detail = default;
                return false;
        }
    }

    private static bool TryGetDiagnosticsAndBuildCommandDetail(string commandName, out (string Description, string Example) detail)
    {
        switch (commandName)
        {
            case "diagnostics-snapshot":
                detail = ("Aggregate IDE state, debugger state, build state, and current errors/warnings.", ExampleCommand("diagnostics-snapshot", @"{""wait_for_intellisense"":true}"));
                return true;
            case "build-configurations":
                detail = ("List available solution build configurations and platforms.", commandName);
                return true;
            case "set-build-configuration":
                detail = ("Activate one build configuration/platform pair.", ExampleCommand("set-build-configuration", @"{""configuration"":""Debug"",""platform"":""x64""}"));
                return true;
            case "build":
                detail = ("Build the solution or a specific project. Provide project to build one project; omit it to build the whole solution. Set wait_for_completion=false only for solution-wide builds when you want to start the build and return immediately.", ExampleCommand("build", @"{""configuration"":""Release"",""wait_for_completion"":false}"));
                return true;
            case "rebuild":
                detail = ("Rebuild the active solution inside Visual Studio. This performs a clean step before building and is heavier than build. By default it starts in the background and returns immediately.", ExampleCommand("rebuild", @"{""configuration"":""Release"",""wait_for_completion"":false}"));
                return true;
            case "errors":
                detail = ("Read current Error List rows with optional severity and text filters. Set refresh=true only when you want to force the Error List to repopulate first.", ExampleCommand("errors", @"{""severity"":""error"",""max"":50}"));
                return true;
            case "warnings":
                detail = ("Read current warning rows with optional code/path/project filters. Set refresh=true only when you want to force the Error List to repopulate first.", ExampleCommand("warnings", @"{""group_by"":""code""}"));
                return true;
            case "messages":
                detail = ("Read current message rows with optional code/path/project filters. Set refresh=true only when you want to force the Error List to repopulate first.", ExampleCommand("messages", @"{""group_by"":""code""}"));
                return true;
            case "build-errors":
                detail = ("Build then capture Error List rows in one call. By default this refuses to build when diagnostics already exist and fails if any errors, warnings, or messages remain after the build.", ExampleCommand("build-errors", @"{""max"":200,""require_clean_diagnostics"":true}"));
                return true;
            case "run-code-analysis":
                detail = ("Run VS code analysis on the solution using the SDK build infrastructure. By default it starts in the background and returns immediately; set wait_for_completion=true to wait for completion and capture Error List results.", ExampleCommand("run-code-analysis", @"{""timeout_ms"":300000,""wait_for_completion"":false}"));
                return true;
            default:
                detail = default;
                return false;
        }
    }
}
