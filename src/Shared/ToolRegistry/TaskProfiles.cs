namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    private static TaskProfile CreateTaskProfile(string task)
    {
        string normalizedTask = task.Trim().ToLowerInvariant();
        string[] tokens = normalizedTask.Split(
            [' ', '_', '-', '.', ',', ':', ';', '?', '!', '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);
        return new TaskProfile(
            tokens,
            ContainsAny(normalizedTask,
                "find", "search", "symbol", "definition", "reference", "read", "where",
                "inspect", "navigate", "understand", "outline", "trace", "code"),
            ContainsAny(normalizedTask,
                "solution explorer", "filename", "file name", "path fragment", "path", "folder"),
            ContainsAny(normalizedTask,
                "error", "warning", "diagnostic", "build", "broken", "failing"),
            ContainsAny(normalizedTask,
                "change", "edit", "write", "patch", "refactor", "rename", "update",
                "fix", "replace", "overwrite"),
            ContainsAny(normalizedTask,
                "build", "compile", "installer", "publish", "msbuild", "rebuild"),
            ContainsAny(normalizedTask,
                "powershell", "cmd", "command line", "process", "exe", "script", "iscc", "terminal"),
            ContainsAny(normalizedTask,
                "which tool", "what tool", "recommend", "discover", "connect", "instance",
                "bridge", "available tools", "tool list", "list tools", "find tool", "what should",
                "how do i", "getting started", "setup", "mcp"),
            ContainsAny(normalizedTask,
                "python", "venv", "virtualenv", "virtual environment", "pip", "conda",
                "interpreter"),
            ContainsAny(normalizedTask,
                "git", "commit", "branch", "stash", "merge", "rebase", "checkout",
                "push", "pull", "fetch", "remote", "staged", "unstaged"),
            ContainsAny(normalizedTask,
                "nuget", "dotnet add", "dotnet remove", "dotnet package"),
            ContainsAny(normalizedTask,
                "breakpoint", "debug", "debugger", "watch", "step into", "step over",
                "step out", "stack frame", "callstack", "call stack", "locals", "exception"));
    }

    private readonly record struct TaskProfile(
        string[] Tokens,
        bool LooksLikeNavigationTask,
        bool LooksLikeSolutionExplorerTask,
        bool LooksLikeDiagnosticTask,
        bool LooksLikeEditTask,
        bool LooksLikeBuildTask,
        bool LooksLikeExternalProcessTask,
        bool LooksLikeDiscoveryTask,
        bool LooksLikePythonTask,
        bool LooksLikeGitTask,
        bool LooksLikeNuGetTask,
        bool LooksLikeDebugTask);
}
