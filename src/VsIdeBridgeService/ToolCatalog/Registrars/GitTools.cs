using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridgeService.SystemTools;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const int DefaultDiffContext = 3;
    private const string GitStatusTool = "git_status";
    private const string GitDiffStagedTool = "git_diff_staged";
    private const string GitDiffUnstagedTool = "git_diff_unstaged";
    private const string GitCommitTool = "git_commit";
    private const string GitCheckoutTool = "git_checkout";
    private const string GitPullTool = "git_pull";
    private const string GitPushTool = "git_push";
    private const string GitBranchListTool = "git_branch_list";

    private static IEnumerable<ToolEntry> GitTools()
        =>
        GitStatusAndHistoryTools()
            .Concat(GitDiffAndMetaTools())
            .Concat(GitStagingCommitTools())
            .Concat(GitBranchTools())
            .Concat(GitNetworkTools())
            .Concat(GitMergeTools())
            .Concat(GitStashTools());

    private static IEnumerable<ToolEntry> GitStatusAndHistoryTools()
    {
        yield return new(GitStatusTool,
            "Show working-tree status (porcelain v1 + branch). Use this before committing " +
            "to see modified, staged, and untracked files.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunAsync(id, repo, "status --porcelain=v1 --branch")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("git_add", "Stage modified files"), (GitCommitTool, "Commit staged changes")],
                related: [(GitDiffUnstagedTool, "See unstaged change details"), (GitDiffStagedTool, "See staged change details")]));

        yield return new("git_current_branch",
            "Return the name of the currently checked-out branch.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitSdkReader.GetCurrentBranchAsync(id, repo)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [(GitBranchListTool, "List all branches"), (GitCheckoutTool, "Switch to another branch"), ("git_create_branch", "Create a new branch")]));

        yield return new(GitBranchListTool,
            "List all local and remote branches with full SHAs.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitSdkReader.GetBranchListAsync(id, repo)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitCheckoutTool, "Switch to a listed branch")],
                related: [("git_current_branch", "Get only the active branch"), ("git_create_branch", "Create a new branch")]));

        yield return new("git_log",
            "Show the commit history. Defaults to the last 20 commits in ISO date format.",
            ObjectSchema(OptInt("max_count", "Max number of commits to show (default 20).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int max = args?["max_count"]?.GetValue<int?>() ?? 20;
                return await GitSdkReader.GetLogAsync(id, repo, max).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("git_show", "Inspect a specific commit from the log")],
                related: [(GitDiffUnstagedTool, "See uncommitted changes"), (GitDiffStagedTool, "See staged changes")]));

        yield return new("git_show",
            "Show the diff and metadata for a single commit, tag, or tree-ish revision.",
            ObjectSchema(Req("revision", "Commit SHA, tag, or branch name to show.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string rev = args?["revision"]?.GetValue<string>() ?? "HEAD";
                return await GitRunner.RunAsync(id, repo, $"show --no-color {EscapeArg(rev)}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [("git_log", "Browse commit history"), (GitDiffUnstagedTool, "See current uncommitted changes")]));
    }

    private static IEnumerable<ToolEntry> GitDiffAndMetaTools()
    {
        yield return new(GitDiffUnstagedTool,
            "Show unstaged changes in the working tree (not yet git-added).",
            ObjectSchema(OptInt("context", "Lines of context around each hunk (default 3).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int ctx = args?["context"]?.GetValue<int?>() ?? DefaultDiffContext;
                return await GitRunner.RunAsync(id, repo, $"diff --no-color --unified={ctx}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("git_add", "Stage the changes after reviewing"), (GitCommitTool, "Commit after staging")],
                related: [(GitDiffStagedTool, "See already-staged changes"), (GitStatusTool, "See which files changed")]));

        yield return new(GitDiffStagedTool,
            "Show staged changes ready to commit (git-added but not yet committed).",
            ObjectSchema(OptInt("context", "Lines of context around each hunk (default 3).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                int ctx = args?["context"]?.GetValue<int?>() ?? DefaultDiffContext;
                return await GitRunner.RunAsync(id, repo, $"diff --cached --no-color --unified={ctx}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitCommitTool, "Commit the staged changes")],
                related: [(GitDiffUnstagedTool, "See unstaged changes"), (GitStatusTool, "See overall working-tree state")]));

        yield return new("git_remote_list",
            "List configured remotes with their fetch and push URLs.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitSdkReader.GetRemoteListAsync(id, repo).ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [("git_fetch", "Fetch from a remote"), (GitPushTool, "Push to a remote"), (GitPullTool, "Pull from a remote")]));

        yield return new("git_tag_list",
            "List tags sorted by version (newest first).",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunAsync(id, repo, "tag --list --sort=-version:refname")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [("git_log", "Browse commit history"), ("git_show", "Inspect a tag's commit")]));

        yield return new("git_stash_list",
            "List stash entries.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunAsync(id, repo, "stash list").ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("git_stash_pop", "Apply the most recent stash entry")],
                related: [("git_stash_push", "Save changes to a new stash")]));
    }

    private static IEnumerable<ToolEntry> GitStagingCommitTools()
    {
        yield return new("git_add",
            "Stage files for the next commit. Pass an array of paths relative to the repo root, " +
            "or [\".\" ] to stage everything.",
            ObjectSchema(Req(Paths, "JSON array of file paths or globs to stage, e.g. [\"src/Foo.cs\"] or [\".\" ].")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string pathList = BuildPathList(args, Paths);
                return await GitRunner.RunAsync(id, repo, $"add -- {pathList}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitCommitTool, "Commit after staging"), (GitDiffStagedTool, "Review what was staged")],
                related: [("git_restore", "Discard working-tree changes"), ("git_reset", "Unstage files"), (GitStatusTool, "Check which files are staged")]));

        yield return new("git_restore",
            "Discard working-tree changes for the specified files, restoring them to HEAD. " +
            "Does not touch the index.",
            ObjectSchema(Req(Paths, "JSON array of file paths to restore, e.g. [\"src/Foo.cs\"].")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string pathList = BuildPathList(args, Paths);
                return await GitRunner.RunAsync(id, repo,
                    $"restore --source=HEAD --worktree -- {pathList}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [(GitStatusTool, "Check remaining changes"), (GitDiffUnstagedTool, "Preview changes before discarding"), ("git_reset", "Unstage instead of discard")]));

        yield return new("git_reset",
            "Unstage files (mixed reset). If no paths are given, unstages everything.",
            ObjectSchema(Opt(Paths, "JSON array string of paths to unstage, or omit for all.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string? pathsRaw = args?[Paths]?.GetValue<string>();
                string pathSpec = string.IsNullOrWhiteSpace(pathsRaw)
                    ? string.Empty
                    : $"-- {BuildPathListFromJson(pathsRaw)}";
                return await GitRunner.RunAsync(id, repo, $"reset {pathSpec}".TrimEnd())
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [("git_restore", "Discard working-tree changes"), ("git_diff_staged", "Review staged changes before resetting"), ("git_status", "Check resulting state")]));

        yield return new(GitCommitTool,
            "Create a commit with a message. Stage files with git_add first.",
            ObjectSchema(Req(Message, "Commit message.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string msg = args?[Message]?.GetValue<string>() ?? string.Empty;
                return await GitRunner.RunAsync(id, repo, $"commit -m {EscapeArg(msg)}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitPushTool, "Push after committing"), (GitStatusTool, "Confirm clean working tree")],
                related: [("git_add", "Stage files before committing"), ("git_commit_amend", "Amend the commit message"), (GitDiffStagedTool, "Review staged changes before committing")]));

        yield return new("git_commit_amend",
            "Amend the most recent commit. Pass a new message or set no_edit to true to keep it.",
            ObjectSchema(
                Opt(Message, "New commit message. Omit to use --no-edit."),
                OptBool("no_edit", "Keep the existing commit message (default true when no message given).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string? msg = args?[Message]?.GetValue<string>();
                bool noEdit = string.IsNullOrWhiteSpace(msg) &&
                              (args?["no_edit"]?.GetValue<bool?>() ?? true);
                string msgArg = !string.IsNullOrWhiteSpace(msg)
                    ? $"-m {EscapeArg(msg)}"
                    : noEdit ? "--no-edit" : string.Empty;
                return await GitRunner.RunAsync(id, repo, $"commit --amend {msgArg}".TrimEnd())
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                related: [(GitCommitTool, "Create a new commit instead"), (GitDiffStagedTool, "Review staged changes"), (GitStatusTool, "Check state after amending")]));
    }

    private static IEnumerable<ToolEntry> GitBranchTools()
    {
        yield return new(GitCheckoutTool,
            "Switch to an existing branch, tag, or commit.",
            ObjectSchema(Req("target", "Branch name, tag, or commit SHA to check out.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string target = args?["target"]?.GetValue<string>() ?? string.Empty;
                return await GitRunner.RunAsync(id, repo, $"checkout {EscapeArg(target)}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check working-tree state after switching"), (GitPullTool, "Pull latest changes after switching")],
                related: [(GitBranchListTool, "List available branches"), ("git_create_branch", "Create a new branch")]));

        yield return new("git_create_branch",
            "Create and switch to a new branch, optionally starting from a given ref.",
            ObjectSchema(
                Req("name", "New branch name."),
                Opt("start_point", "Optional commit, tag, or branch to branch from.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string name = args?["name"]?.GetValue<string>() ?? string.Empty;
                string? start = args?["start_point"]?.GetValue<string>();
                string startArg = string.IsNullOrWhiteSpace(start) ? string.Empty : $" {EscapeArg(start)}";
                return await GitRunner.RunAsync(id, repo, $"checkout -b {EscapeArg(name)}{startArg}")
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitCheckoutTool, "Switch to the new branch"), (GitPushTool, "Push the new branch to remote")],
                related: [(GitBranchListTool, "List existing branches")]));
    }

    private static IEnumerable<ToolEntry> GitNetworkTools()
    {
        yield return new("git_fetch",
            "Fetch from a remote without merging. Defaults to all remotes.",
            ObjectSchema(
                Opt("remote", "Remote name (default: all remotes)."),
                OptBool("all", "Fetch all remotes (default true when remote omitted)."),
                OptBool("prune", "Remove stale remote-tracking branches (default false).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string? remote = args?["remote"]?.GetValue<string>();
                bool all = string.IsNullOrWhiteSpace(remote) &&
                           (args?["all"]?.GetValue<bool?>() ?? true);
                bool prune = args?["prune"]?.GetValue<bool?>() ?? false;
                string remoteArg = all ? "--all" : EscapeArg(remote!);
                string pruneArg = prune ? "--prune" : string.Empty;
                return await GitRunner.RunNetworkAsync(id, repo,
                    $"fetch {remoteArg} {pruneArg}".TrimEnd())
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitPullTool, "Merge after fetching"), (GitBranchListTool, "See new remote branches after fetching")],
                related: [("git_remote_list", "List configured remotes"), ("git_merge", "Merge fetched changes manually")]));

        yield return new(GitPullTool,
            "Fetch and merge from a remote branch.",
            ObjectSchema(
                Opt("remote", "Remote name (default: origin)."),
                Opt("branch", "Branch name (default: current tracking branch).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string remote = args?["remote"]?.GetValue<string>() ?? string.Empty;
                string branch = args?["branch"]?.GetValue<string>() ?? string.Empty;
                string remoteArg = string.IsNullOrWhiteSpace(remote) ? string.Empty : EscapeArg(remote);
                string branchArg = string.IsNullOrWhiteSpace(branch) ? string.Empty : EscapeArg(branch);
                return await GitRunner.RunNetworkAsync(id, repo,
                    $"pull {remoteArg} {branchArg}".TrimEnd())
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check state after pulling"), ("git_merge", "Resolve conflicts if pull created a merge")],
                related: [("git_fetch", "Fetch without merging"), (GitPushTool, "Push after pulling")]));

        yield return new(GitPushTool,
            "Push commits to a remote branch.",
            ObjectSchema(
                Opt("remote", "Remote name (default: origin)."),
                Opt("branch", "Branch name (default: current branch)."),
                OptBool("set_upstream", "Set the upstream tracking reference (-u flag).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string remote = args?["remote"]?.GetValue<string>() ?? string.Empty;
                string branch = args?["branch"]?.GetValue<string>() ?? string.Empty;
                bool setUpstream = args?["set_upstream"]?.GetValue<bool?>() ?? false;
                string uFlag = setUpstream ? "-u" : string.Empty;
                string remoteArg = string.IsNullOrWhiteSpace(remote) ? string.Empty : EscapeArg(remote);
                string branchArg = string.IsNullOrWhiteSpace(branch) ? string.Empty : EscapeArg(branch);
                return await GitRunner.RunNetworkAsync(id, repo,
                    $"push {uFlag} {remoteArg} {branchArg}".TrimEnd().Replace("  ", " "))
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Confirm clean state before pushing"), ("git_log", "Review commits being pushed")],
                related: [(GitPullTool, "Pull before pushing to avoid conflicts"), ("git_remote_list", "Check available remotes")]));

    }

    private static IEnumerable<ToolEntry> GitMergeTools()
    {
        yield return new("git_merge",
            "Merge a source branch into the current branch.",
            ObjectSchema(
                Req("source", "Branch or commit to merge."),
                OptBool("ff_only", "Refuse to merge unless fast-forward is possible."),
                OptBool("no_ff", "Always create a merge commit even when fast-forward is possible."),
                OptBool("squash", "Squash all commits into a single staged change."),
                Opt(Message, "Optional merge commit message.")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string source = args?["source"]?.GetValue<string>() ?? string.Empty;
                bool ffOnly = args?["ff_only"]?.GetValue<bool?>() ?? false;
                bool noFf = args?["no_ff"]?.GetValue<bool?>() ?? false;
                bool squash = args?["squash"]?.GetValue<bool?>() ?? false;
                string? msg = args?[Message]?.GetValue<string>();
                string flags = string.Join(" ", new[]
                {
                    ffOnly ? "--ff-only" : string.Empty,
                    noFf   ? "--no-ff"   : string.Empty,
                    squash ? "--squash"  : string.Empty,
                    !string.IsNullOrWhiteSpace(msg) ? $"-m {EscapeArg(msg)}" : string.Empty,
                }.Where(s => !string.IsNullOrEmpty(s)));
                return await GitRunner.RunNetworkAsync(id, repo,
                    $"merge {flags} {EscapeArg(source)}".TrimEnd())
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check for merge conflicts after merging")],
                related: [("git_fetch", "Fetch before merging"), (GitPullTool, "Fetch and merge in one step"), (GitCommitTool, "Commit after resolving conflicts")]));
    }

    private static IEnumerable<ToolEntry> GitStashTools()
    {
        yield return new("git_stash_push",
            "Stash current working-tree and index changes.",
            ObjectSchema(
                Opt(Message, "Optional stash description."),
                OptBool("include_untracked", "Also stash untracked files (default false).")),
            Git,
            async (id, args, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string? msg = args?[Message]?.GetValue<string>();
                bool untracked = args?["include_untracked"]?.GetValue<bool?>() ?? false;
                string msgArg = !string.IsNullOrWhiteSpace(msg) ? $"-m {EscapeArg(msg)}" : string.Empty;
                string untrackedArg = untracked ? "--include-untracked" : string.Empty;
                return await GitRunner.RunAsync(id, repo,
                    $"stash push {untrackedArg} {msgArg}".TrimEnd())
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitCheckoutTool, "Switch branch after stashing"), ("git_stash_pop", "Restore stashed changes later")],
                related: [("git_stash_list", "List saved stash entries"), (GitStatusTool, "Confirm clean working tree after stashing")]));

        yield return new("git_stash_pop",
            "Apply and remove the most recent stash entry.",
            EmptySchema(), Git,
            async (id, _, bridge) =>
            {
                string repo = ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                return await GitRunner.RunAsync(id, repo, "stash pop").ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [(GitStatusTool, "Check working tree after popping")],
                related: [("git_stash_list", "List available stash entries"), ("git_stash_push", "Save more changes to a stash")]));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Wrap a shell argument in double-quotes and escape internal double-quotes.
    /// </summary>
    private static string EscapeArg(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Build a space-separated quoted path list from a JSON array arg.
    /// </summary>
    private static string BuildPathList(JsonObject? args, string argName)
    {
        if (args?[argName] is JsonArray arr)
            return string.Join(" ", arr.Select(n => EscapeArg(n?.GetValue<string>() ?? string.Empty)));

        // Fallback: single string value.
        string? single = args?[argName]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(single) ? "." : EscapeArg(single);
    }

    /// <summary>
    /// Build a space-separated quoted path list from a raw JSON array string.
    /// </summary>
    private static string BuildPathListFromJson(string jsonArray)
    {
        try
        {
            JsonNode? node = System.Text.Json.JsonSerializer.Deserialize<JsonNode>(jsonArray);
            if (node is JsonArray arr)
                return string.Join(" ", arr.Select(n => EscapeArg(n?.GetValue<string>() ?? string.Empty)));
        }
        catch (System.Text.Json.JsonException)
        {
            // Not valid JSON — treat as a plain path.
        }

        return EscapeArg(jsonArray);
    }
}
