namespace VsIdeBridge.Shared;

public static partial class BridgeCommandCatalog
{
    private const string ExampleSourceFile = "BuildService.cs";

    private static bool TryGetWorkspaceCommandDetail(string commandName, out (string Description, string Example) detail)
    {
        switch (commandName)
        {
            case "help":
                detail = ("Return bridge command catalog metadata and usage examples.", commandName);
                return true;
            case "smoke-test":
                detail = ("Capture smoke-test IDE state to verify bridge command execution.", commandName);
                return true;
            case "state":
                detail = ("Capture IDE state including solution, active document, and bridge identity.", ExampleCommand("state", @"{""out"":""C:\\temp\\ide-state.json""}"));
                return true;
            case "ui-settings":
                detail = ("Read current IDE Bridge UI/security settings without modifying them.", "ui-settings");
                return true;
            case "capture-vs-window":
                detail = ("Activate the bound Visual Studio main window, bring it to the foreground, and capture only that window to a PNG image. If no output path is provided, the bridge saves it under %TEMP%\\vs-ide-bridge\\screenshots.", commandName);
                return true;
            case "ready":
                detail = ("Wait for Visual Studio and IntelliSense to be ready for semantic commands.", ExampleCommand("ready", @"{""timeout_ms"":120000}"));
                return true;
            case "open-solution":
                detail = ("Open a specific existing .sln or .slnx file in the current Visual Studio instance without opening a new window. Use this when you already know the exact solution path.", ExampleCommand("open-solution", @"{""solution"":""C:\\repo\\PinballBot\\PinballBot.sln""}"));
                return true;
            case "create-solution":
                detail = ("Create and open a new solution in the current Visual Studio instance.", ExampleCommand("create-solution", @"{""directory"":""C:\\repo\\Scratch"",""name"":""ScratchApp""}"));
                return true;
            case "close-ide":
                detail = ("Close the current Visual Studio instance through DTE Quit.", commandName);
                return true;
            case "batch":
                detail = ("Run multiple commands in one request.", ExampleCommand("batch", @"{""steps"": [{""id"": ""state"", ""command"": ""state""}]}"));
                return true;
            case "open-document":
                detail = ("Open a document by unique filename, solution-relative path, absolute path, or solution item name.", ExampleCommand("open-document", @"{""file"":""" + ExampleSourceFile + @""",""line"":1,""column"":1}"));
                return true;
            case "list-documents":
                detail = ("List open documents.", commandName);
                return true;
            case "list-tabs":
                detail = ("List open editor tabs and identify the active tab.", commandName);
                return true;
            case "activate-document":
                detail = ("Activate an open document tab by query.", ExampleCommand("activate-document", @"{""query"":""Program.cs""}"));
                return true;
            case "close-document":
                detail = ("Close one or more open tabs by query.", ExampleCommand("close-document", @"{""query"":"".json"",""all"":true}"));
                return true;
            case "save-document":
                detail = ("Save one document by unique filename, solution-relative path, or absolute path, or save all open documents.", ExampleCommand("save-document", @"{""file"":""" + ExampleSourceFile + @"""}"));
                return true;
            case "close-file":
                detail = ("Close one open file tab by unique filename, solution-relative path, or absolute path, or by query.", ExampleCommand("close-file", @"{""file"":""" + ExampleSourceFile + @"""}"));
                return true;
            case "close-others":
                detail = ("Close all tabs except the active tab.", commandName);
                return true;
            case "activate-window":
                detail = ("Activate a Visual Studio tool window by caption or kind.", ExampleCommand("activate-window", @"{""window"":""Error List""}"));
                return true;
            case "list-windows":
                detail = ("List Visual Studio tool windows, optionally filtered by query.", ExampleCommand("list-windows", @"{""query"":""Error""}"));
                return true;
            case "execute-command":
                detail = ("Execute an arbitrary Visual Studio command with optional arguments.", ExampleCommand("execute-command", @"{""name"":""Edit.FormatDocument""}"));
                return true;
            case "document-slice":
                detail = ("Fetch one code slice from a file. For files in the active solution, a unique bare filename like '" + ExampleSourceFile + @"' is usually enough; use a longer relative path only when needed.", ExampleCommand("document-slice", @"{""file"":""" + ExampleSourceFile + @""",""line"":120,""context_before"":8,""context_after"":20}"));
                return true;
            case "document-slices":
                detail = ("Fetch multiple code slices from ranges_file or inline ranges JSON. For files in the active solution, a unique bare filename like '" + ExampleSourceFile + @"' is usually enough; use a longer relative path only when needed.", ExampleCommand("document-slices", @"{""ranges"": [{""file"": """ + ExampleSourceFile + @""", ""line"": 42, ""context_before"": 8, ""context_after"": 20}]}"));
                return true;
            case "apply-diff":
                detail = ("Apply editor patch text through the live editor so changes are visible in Visual Studio. Use directives like '*** Update File:' rather than unified diff headers such as '---' and '+++'. Changed files open by default.", ExampleCommand("apply-diff", @"{""patch_file"":""C:\\temp\\change.diff""}"));
                return true;
            default:
                detail = default;
                return false;
        }
    }
}
