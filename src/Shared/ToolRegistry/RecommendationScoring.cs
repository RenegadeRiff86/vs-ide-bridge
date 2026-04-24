namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    private (int Score, string Reason) ScoreTool(ToolDefinition tool, TaskProfile profile)
    {
        int score = 0;
        string reason = string.Empty;

        score += ScoreRecommendedMatches(tool, profile, ref reason);
        score += ScoreCategoryMatches(tool, profile, ref reason);
        score += ScoreKeywordMatches(tool, profile.Tokens, ref reason);
        score += ScoreShellExecPenalty(tool, profile, ref reason);

        if (score <= 0)
            return (0, string.Empty);

        return (score, reason == string.Empty ? "Broadly relevant tool" : reason);
    }

    private int ScoreRecommendedMatches(ToolDefinition tool, TaskProfile profile, ref string reason)
    {
        int score = 0;

        if (profile.LooksLikeNavigationTask && _featuredTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 40;
            reason = ChooseReason(reason, "Featured code navigation tool");
        }

        if (profile.LooksLikeNavigationTask && DefaultRecommendedNavigationToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 45;
            reason = ChooseReason(reason, "Top navigation tool for code understanding");
        }

        if (profile.LooksLikeSolutionExplorerTask && string.Equals(tool.Name, "find_files", StringComparison.OrdinalIgnoreCase))
        {
            score += 70;
            reason = ChooseReason(reason, "Matches Solution Explorer-style file search task");
        }

        if (profile.LooksLikeBuildTask && DefaultRecommendedBuildToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 45;
            reason = ChooseReason(reason, "Primary Visual Studio build tool");
        }

        if (profile.LooksLikeEditTask && DefaultRecommendedEditToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 45;
            reason = ChooseReason(reason, "Primary bridge editing tool");
        }

        if (profile.LooksLikeDiscoveryTask && DefaultRecommendedDiscoveryToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 50;
            reason = ChooseReason(reason, "Primary tool discovery tool");
        }

        return score;
    }

    private static int ScoreCategoryMatches(ToolDefinition tool, TaskProfile profile, ref string reason)
    {
        int score = 0;

        if (profile.LooksLikeNavigationTask && string.Equals(tool.Category, "search", StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
            reason = ChooseReason(reason, "Search category matches code-navigation task");
        }

        if (profile.LooksLikeDiagnosticTask && string.Equals(tool.Category, "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            score += 22;
            reason = ChooseReason(reason, "Diagnostics category matches error/build task");
        }

        if (profile.LooksLikeBuildTask && string.Equals(tool.Category, "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
            reason = ChooseReason(reason, "Diagnostics category matches build task");
        }

        if (profile.LooksLikeEditTask && string.Equals(tool.Category, "documents", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
            reason = ChooseReason(reason, "Documents category matches file-edit task");
        }

        if (profile.LooksLikeEditTask && tool.Mutating)
        {
            score += 10;
            reason = ChooseReason(reason, "Mutating tool matches requested change");
        }

        if (profile.LooksLikeDiscoveryTask && string.Equals(tool.Category, "system", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
            reason = ChooseReason(reason, "System category matches tool-discovery task");
        }

        if (profile.LooksLikePythonTask && string.Equals(tool.Category, "python", StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
            reason = ChooseReason(reason, "Python category matches python task");
        }

        if (profile.LooksLikeGitTask && string.Equals(tool.Category, "git", StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
            reason = ChooseReason(reason, "Git category matches version control task");
        }

        if (profile.LooksLikeNuGetTask && string.Equals(tool.Category, "project", StringComparison.OrdinalIgnoreCase)
            && tool.Name.StartsWith("nuget_", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
            reason = ChooseReason(reason, "NuGet tool matches package-management task");
        }

        if (profile.LooksLikeDebugTask && string.Equals(tool.Category, "debug", StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
            reason = ChooseReason(reason, "Debug category matches debugger task");
        }

        return score;
    }

    private static int ScoreKeywordMatches(ToolDefinition tool, string[] tokens, ref string reason)
    {
        int score = 0;

        if (tool.Tags.Count > 0)
        {
            int tagHits = tool.Tags.Count(tag => tokens.Contains(tag, StringComparer.OrdinalIgnoreCase));
            if (tagHits > 0)
            {
                score += tagHits * 12;
                reason = ChooseReason(reason, "Tool tags match task keywords");
            }
        }

        int nameHits = tokens.Count(token => token.Length > 2
            && tool.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (nameHits > 0)
        {
            score += nameHits * 8;
            reason = ChooseReason(reason, "Tool name matches task keywords");
        }

        int aliasHits = tokens.Count(token => token.Length > 2
            && tool.Aliases.Any(alias => alias.Contains(token, StringComparison.OrdinalIgnoreCase)));
        if (aliasHits > 0)
        {
            score += aliasHits * 10;
            reason = ChooseReason(reason, "Tool aliases match common IDE wording");
        }

        int summaryHits = tokens.Count(token => token.Length > 3
            && tool.Summary.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (summaryHits > 0)
        {
            score += summaryHits * 4;
            reason = ChooseReason(reason, "Summary matches task context");
        }

        int descriptionHits = tokens.Count(token => token.Length > 3
            && tool.Description.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (descriptionHits > 0)
        {
            score += descriptionHits * 3;
            reason = ChooseReason(reason, "Description matches task context");
        }

        return score;
    }

    private static int ScoreShellExecPenalty(ToolDefinition tool, TaskProfile profile, ref string reason)
    {
        if (!string.Equals(tool.Name, "shell_exec", StringComparison.OrdinalIgnoreCase))
            return 0;

        int score = 0;
        if (profile.LooksLikeExternalProcessTask)
        {
            score -= 10;
            reason = ChooseReason(reason, "External process task may require shell execution");
        }
        else
        {
            score -= 80;
        }

        if (profile.LooksLikeBuildTask)
            score -= 40;

        return score;
    }
}
