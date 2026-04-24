namespace VsIdeBridge.Shared;

public static partial class BridgeCommandCatalog
{
    private const string ExampleFilePath = @"C:\repo\src\foo.cpp";

    private static bool TryGetNavigationCommandDetail(string commandName, out (string Description, string Example) detail)
    {
        switch (commandName)
        {
            case "find_text":
            case "find-text":
                detail = ("Find text across the solution, project, or current document, with optional subtree filtering. Related tools: find_files, search_symbols.", ExampleCommand("find_text", @"{""query"":""OnInit"",""path"":""src\\libslic3r""}"));
                return true;
            case "find_text_batch":
            case "find-text-batch":
                detail = ("Find text for multiple queries in one bridge round-trip, internally chunked when needed. Related tools: find_text, search_symbols.", ExampleCommand("find_text_batch", @"{""queries"": [""OnInit"", ""RunAsync"", ""BridgeHealth""], ""path"": ""src\\VsIdeBridge"", ""max_queries_per_chunk"": 5}"));
                return true;
            case "find_files":
            case "find-files":
                detail = ("Search Solution Explorer-style files by name or path fragment and return ranked matches. Related tools: glob, search_symbols.", ExampleCommand("find_files", @"{""query"":""CMakeLists.txt""}"));
                return true;
            case "glob":
            case "glob_files":
            case "find_by_pattern":
            case "list_files":
                detail = ("Find files by glob pattern under the solution root or a specific subtree. Related tools: find_files, read_file.", ExampleCommand("glob", @"{""pattern"":""src/**/*.cs"",""path"":""src\\VsIdeBridge""}"));
                return true;
            case "find_references":
            case "find-references":
                detail = ("Run Find All References for the symbol at a file, line, and column. Related tools: call_hierarchy, goto_definition.", ExampleCommand("find_references", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            case "count_references":
            case "count-references":
                detail = ("Run Find All References and return exact count when Visual Studio exposes one, or explicit unknown otherwise. Related tools: find_references, call_hierarchy.", ExampleCommand("count_references", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            case "call_hierarchy":
            case "call-hierarchy":
                detail = ("Open Call Hierarchy for the symbol at a file, line, and column. For managed languages, also return a bounded caller tree in the command result. Related tools: find_references, search_symbols.", ExampleCommand("call_hierarchy", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13,""max_depth"":2}"));
                return true;
            case "smart_context":
            case "smart-context":
                detail = ("Collect focused code context for a natural-language query. Related tools: search_symbols, find_text.", ExampleCommand("smart_context", @"{""query"":""where is GUI_App::OnInit used"",""max_contexts"":3}"));
                return true;
            case "goto_definition":
            case "goto-definition":
                detail = ("Navigate to the definition of the symbol at a file, line, and column. Related tools: find_references, file_outline.", ExampleCommand("goto_definition", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            case "goto_implementation":
            case "goto-implementation":
                detail = ("Navigate to one implementation of the symbol at a file, line, and column. Related tools: find_references, goto_definition.", ExampleCommand("goto_implementation", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            case "peek_definition":
            case "peek-definition":
                detail = ("Read the definition source for the symbol at a file, line, and column without navigating away. Related tools: symbol_info, goto_definition.", ExampleCommand("peek_definition", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            case "file_outline":
            case "file-outline":
                detail = ("List a file outline from the code model. Related tools: search_symbols, find_files.", ExampleCommand("file_outline", @"{""file"":""" + ExampleFilePath + @"""}"));
                return true;
            case "file_symbols":
            case "file-symbols":
                detail = ("List symbols in one file with optional kind filtering. Related tools: file_outline, search_symbols.", ExampleCommand("file_symbols", @"{""file"":""" + ExampleFilePath + @""",""kind"":""function""}"));
                return true;
            case "search_symbols":
            case "search-symbols":
                detail = ("Search symbol definitions by name across solution scope. Related tools: find_files, call_hierarchy.", ExampleCommand("search_symbols", @"{""query"":""RunAsync"",""kind"":""function"",""path"":""src\\VsIdeBridge""}"));
                return true;
            case "quick_info":
            case "quick-info":
                detail = ("Resolve symbol information at file, line, and column with surrounding context. Related tools: goto_definition, find_references.", ExampleCommand("quick_info", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            default:
                detail = default;
                return false;
        }
    }
}
