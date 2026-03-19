using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using LibGit2Sharp;

namespace VsIdeBridgeCli.Git;

internal static class ManagedGitToolProvider
{
    private const string BackendName = "libgit2sharp";
    private static readonly HashSet<string> SupportedToolNames = new(StringComparer.Ordinal)
    {
        "git_status",
        "git_current_branch",
        "git_remote_list",
        "git_tag_list",
        "git_log",
        "git_branch_list",
        "git_stash_list",
    };

    internal static JsonObject? TryExecute(string workingDirectory, string toolName, JsonObject? args)
    {
        if (!SupportedToolNames.Contains(toolName))
        {
            return null;
        }

        string? repositoryPath = Repository.Discover(workingDirectory);
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return CreateError(workingDirectory, toolName, "No Git repository was found for the current solution path.");
        }

        try
        {
            using Repository repository = new(repositoryPath);
            return toolName switch
            {
                "git_status" => CreateStatusResult(repository),
                "git_current_branch" => CreateCurrentBranchResult(repository),
                "git_remote_list" => CreateRemoteListResult(repository),
                "git_tag_list" => CreateTagListResult(repository),
                "git_log" => CreateLogResult(repository, GetIntOrDefault(args, "max_count", 20)),
                "git_branch_list" => CreateBranchListResult(repository),
                "git_stash_list" => CreateStashListResult(repository),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            return CreateError(workingDirectory, toolName, ex.Message);
        }
    }

    private static JsonObject CreateStatusResult(Repository repository)
    {
        StatusOptions statusOptions = new()
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            IncludeIgnored = false,
        };
        RepositoryStatus repositoryStatus = repository.RetrieveStatus(statusOptions);
        IOrderedEnumerable<StatusEntry> orderedEntries = repositoryStatus
            .OrderBy(static entry => entry.FilePath, StringComparer.OrdinalIgnoreCase);
        StringBuilder stdoutBuilder = new();
        JsonArray entries = new();

        stdoutBuilder.Append("## ");
        stdoutBuilder.Append(BuildBranchHeader(repository));

        foreach (StatusEntry entry in orderedEntries)
        {
            string porcelainCode = GetPorcelainCode(entry.State);
            if (string.IsNullOrEmpty(porcelainCode))
            {
                continue;
            }

            stdoutBuilder.AppendLine();
            stdoutBuilder.Append(porcelainCode);
            stdoutBuilder.Append(' ');
            stdoutBuilder.Append(entry.FilePath);

            entries.Add(new JsonObject
            {
                ["path"] = entry.FilePath,
                ["status"] = porcelainCode,
                ["state"] = entry.State.ToString(),
            });
        }

        JsonObject data = new()
        {
            ["branch"] = BuildBranchJson(repository),
            ["entries"] = entries,
        };

        return CreateSuccess(repository, "git_status", stdoutBuilder.ToString(), data);
    }

    private static JsonObject CreateCurrentBranchResult(Repository repository)
    {
        Branch head = repository.Head;
        bool isDetached = repository.Info.IsHeadDetached;
        string branchName = isDetached ? "HEAD" : head.FriendlyName;
        JsonObject data = new()
        {
            ["branch"] = branchName,
            ["isDetached"] = isDetached,
            ["canonicalName"] = head.CanonicalName,
        };

        return CreateSuccess(repository, "git_current_branch", branchName, data);
    }

    private static JsonObject CreateRemoteListResult(Repository repository)
    {
        IOrderedEnumerable<Remote> orderedRemotes = repository.Network.Remotes
            .OrderBy(static remote => remote.Name, StringComparer.OrdinalIgnoreCase);
        StringBuilder stdoutBuilder = new();
        JsonArray remotes = new();
        bool isFirstLine = true;

        foreach (Remote remote in orderedRemotes)
        {
            string fetchUrl = remote.Url ?? string.Empty;
            string pushUrl = string.IsNullOrWhiteSpace(remote.PushUrl) ? fetchUrl : remote.PushUrl;

            if (!isFirstLine)
            {
                stdoutBuilder.AppendLine();
            }

            stdoutBuilder.Append(remote.Name);
            stdoutBuilder.Append('\t');
            stdoutBuilder.Append(fetchUrl);
            stdoutBuilder.Append(" (fetch)");
            stdoutBuilder.AppendLine();
            stdoutBuilder.Append(remote.Name);
            stdoutBuilder.Append('\t');
            stdoutBuilder.Append(pushUrl);
            stdoutBuilder.Append(" (push)");

            remotes.Add(new JsonObject
            {
                ["name"] = remote.Name,
                ["fetchUrl"] = fetchUrl,
                ["pushUrl"] = pushUrl,
            });

            isFirstLine = false;
        }

        JsonObject data = new()
        {
            ["remotes"] = remotes,
        };

        return CreateSuccess(repository, "git_remote_list", stdoutBuilder.ToString(), data);
    }

    private static JsonObject CreateTagListResult(Repository repository)
    {
        IOrderedEnumerable<Tag> orderedTags = repository.Tags
            .OrderBy(static tag => tag.FriendlyName, StringComparer.OrdinalIgnoreCase);
        StringBuilder stdoutBuilder = new();
        JsonArray tags = new();
        bool isFirst = true;

        foreach (Tag tag in orderedTags)
        {
            if (!isFirst)
            {
                stdoutBuilder.AppendLine();
            }

            stdoutBuilder.Append(tag.FriendlyName);
            tags.Add(new JsonObject
            {
                ["name"] = tag.FriendlyName,
                ["isAnnotated"] = tag.IsAnnotated,
                ["message"] = tag.Annotation?.Message,
            });
            isFirst = false;
        }

        JsonObject data = new()
        {
            ["tags"] = tags,
        };

        return CreateSuccess(repository, "git_tag_list", stdoutBuilder.ToString(), data);
    }

    private static JsonObject CreateLogResult(Repository repository, int maxCount)
    {
        int boundedMaxCount = Math.Clamp(maxCount, 1, 100);
        CommitFilter commitFilter = new()
        {
            IncludeReachableFrom = repository.Head,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
        };
        StringBuilder stdoutBuilder = new();
        JsonArray commits = new();
        bool isFirst = true;
        int count = 0;

        foreach (Commit commit in repository.Commits.QueryBy(commitFilter))
        {
            if (count >= boundedMaxCount)
            {
                break;
            }

            string commitLine = string.Concat(
                commit.Sha,
                "\t",
                commit.Committer.When.ToString("O", CultureInfo.InvariantCulture),
                "\t",
                commit.Committer.Name,
                "\t",
                commit.MessageShort);

            if (!isFirst)
            {
                stdoutBuilder.AppendLine();
            }

            stdoutBuilder.Append(commitLine);
            commits.Add(new JsonObject
            {
                ["sha"] = commit.Sha,
                ["author"] = commit.Author.Name,
                ["committer"] = commit.Committer.Name,
                ["when"] = commit.Committer.When.ToString("O", CultureInfo.InvariantCulture),
                ["summary"] = commit.MessageShort,
            });

            isFirst = false;
            count++;
        }

        JsonObject data = new()
        {
            ["commits"] = commits,
        };

        return CreateSuccess(repository, "git_log", stdoutBuilder.ToString(), data);
    }

    private static JsonObject CreateBranchListResult(Repository repository)
    {
        IOrderedEnumerable<Branch> orderedBranches = repository.Branches
            .OrderBy(static branch => branch.IsRemote ? 1 : 0)
            .ThenBy(static branch => branch.FriendlyName, StringComparer.OrdinalIgnoreCase);
        StringBuilder stdoutBuilder = new();
        JsonArray branches = new();
        bool isFirst = true;

        foreach (Branch branch in orderedBranches)
        {
            string displayName = branch.IsRemote ? $"remotes/{branch.FriendlyName}" : branch.FriendlyName;
            string marker = branch.IsCurrentRepositoryHead ? "*" : " ";
            string sha = branch.Tip?.Sha ?? string.Empty;
            string summary = branch.Tip?.MessageShort ?? string.Empty;

            if (!isFirst)
            {
                stdoutBuilder.AppendLine();
            }

            stdoutBuilder.Append(marker);
            stdoutBuilder.Append(' ');
            stdoutBuilder.Append(displayName);
            if (!string.IsNullOrWhiteSpace(sha))
            {
                stdoutBuilder.Append(' ');
                stdoutBuilder.Append(sha);
            }

            if (!string.IsNullOrWhiteSpace(summary))
            {
                stdoutBuilder.Append(' ');
                stdoutBuilder.Append(summary);
            }

            branches.Add(new JsonObject
            {
                ["name"] = branch.FriendlyName,
                ["displayName"] = displayName,
                ["isRemote"] = branch.IsRemote,
                ["isCurrent"] = branch.IsCurrentRepositoryHead,
                ["canonicalName"] = branch.CanonicalName,
                ["tipSha"] = sha,
                ["tipSummary"] = summary,
            });

            isFirst = false;
        }

        JsonObject data = new()
        {
            ["branches"] = branches,
        };

        return CreateSuccess(repository, "git_branch_list", stdoutBuilder.ToString(), data);
    }

    private static JsonObject CreateStashListResult(Repository repository)
    {
        StringBuilder stdoutBuilder = new();
        JsonArray stashes = new();
        bool isFirst = true;
        int index = 0;

        foreach (Stash stash in repository.Stashes)
        {
            string line = string.Concat(
                "stash@{",
                index.ToString(CultureInfo.InvariantCulture),
                "}: ",
                stash.Message ?? string.Empty);

            if (!isFirst)
            {
                stdoutBuilder.AppendLine();
            }

            stdoutBuilder.Append(line);
            stashes.Add(new JsonObject
            {
                ["index"] = index,
                ["message"] = stash.Message,
                ["committer"] = null,
                ["when"] = null,
            });

            isFirst = false;
            index++;
        }

        JsonObject data = new()
        {
            ["stashes"] = stashes,
        };

        return CreateSuccess(repository, "git_stash_list", stdoutBuilder.ToString(), data);
    }

    private static JsonObject CreateSuccess(Repository repository, string commandName, string stdout, JsonObject data)
    {
        return new JsonObject
        {
            ["success"] = true,
            ["exitCode"] = 0,
            ["command"] = BackendName,
            ["workingDirectory"] = repository.Info.WorkingDirectory,
            ["args"] = commandName,
            ["stdout"] = stdout,
            ["stderr"] = string.Empty,
            ["backend"] = BackendName,
            ["repositoryRoot"] = repository.Info.WorkingDirectory,
            ["data"] = data,
        };
    }

    private static JsonObject CreateError(string workingDirectory, string commandName, string message)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["exitCode"] = -1,
            ["command"] = BackendName,
            ["workingDirectory"] = workingDirectory,
            ["args"] = commandName,
            ["stdout"] = string.Empty,
            ["stderr"] = message,
            ["backend"] = BackendName,
        };
    }

    private static JsonObject BuildBranchJson(Repository repository)
    {
        Branch head = repository.Head;
        Branch? trackedBranch = head.TrackedBranch;
        bool isDetached = repository.Info.IsHeadDetached;
        return new JsonObject
        {
            ["name"] = isDetached ? "HEAD" : head.FriendlyName,
            ["canonicalName"] = head.CanonicalName,
            ["isDetached"] = isDetached,
            ["trackedBranch"] = trackedBranch?.FriendlyName,
            ["aheadBy"] = head.TrackingDetails.AheadBy,
            ["behindBy"] = head.TrackingDetails.BehindBy,
        };
    }

    private static string BuildBranchHeader(Repository repository)
    {
        Branch head = repository.Head;
        Branch? trackedBranch = head.TrackedBranch;
        bool isDetached = repository.Info.IsHeadDetached;
        StringBuilder builder = new();
        builder.Append(isDetached ? "HEAD" : head.FriendlyName);

        if (trackedBranch is not null)
        {
            builder.Append("...");
            builder.Append(trackedBranch.FriendlyName);

            int? aheadBy = head.TrackingDetails.AheadBy;
            int? behindBy = head.TrackingDetails.BehindBy;
            bool hasAhead = aheadBy.GetValueOrDefault() > 0;
            bool hasBehind = behindBy.GetValueOrDefault() > 0;

            if (hasAhead || hasBehind)
            {
                builder.Append(" [");
                if (hasAhead)
                {
                    builder.Append("ahead ");
                    builder.Append(aheadBy!.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (hasAhead && hasBehind)
                {
                    builder.Append(", ");
                }

                if (hasBehind)
                {
                    builder.Append("behind ");
                    builder.Append(behindBy!.Value.ToString(CultureInfo.InvariantCulture));
                }

                builder.Append(']');
            }
        }

        return builder.ToString();
    }

    private static string GetPorcelainCode(FileStatus state)
    {
        if (state.HasFlag(FileStatus.Ignored))
        {
            return "!!";
        }

        if (state.HasFlag(FileStatus.NewInWorkdir) && state == FileStatus.NewInWorkdir)
        {
            return "??";
        }

        if (state.HasFlag(FileStatus.Conflicted))
        {
            return "UU";
        }

        char indexCode = GetIndexCode(state);
        char workTreeCode = GetWorkTreeCode(state);
        return indexCode == ' ' && workTreeCode == ' '
            ? string.Empty
            : string.Create(2, (indexCode, workTreeCode), static (span, value) =>
            {
                span[0] = value.Item1;
                span[1] = value.Item2;
            });
    }

    private static char GetIndexCode(FileStatus state)
    {
        if (state.HasFlag(FileStatus.NewInIndex))
        {
            return 'A';
        }

        if (state.HasFlag(FileStatus.ModifiedInIndex))
        {
            return 'M';
        }

        if (state.HasFlag(FileStatus.DeletedFromIndex))
        {
            return 'D';
        }

        if (state.HasFlag(FileStatus.RenamedInIndex))
        {
            return 'R';
        }

        if (state.HasFlag(FileStatus.TypeChangeInIndex))
        {
            return 'T';
        }

        return ' ';
    }

    private static char GetWorkTreeCode(FileStatus state)
    {
        if (state.HasFlag(FileStatus.ModifiedInWorkdir))
        {
            return 'M';
        }

        if (state.HasFlag(FileStatus.DeletedFromWorkdir))
        {
            return 'D';
        }

        if (state.HasFlag(FileStatus.RenamedInWorkdir))
        {
            return 'R';
        }

        if (state.HasFlag(FileStatus.TypeChangeInWorkdir))
        {
            return 'T';
        }

        return ' ';
    }

    private static int GetIntOrDefault(JsonObject? args, string name, int defaultValue)
    {
        JsonNode? node = args?[name];
        if (node is null)
        {
            return defaultValue;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (FormatException)
        {
            return defaultValue;
        }
        catch (InvalidOperationException)
        {
            return defaultValue;
        }
    }
}
