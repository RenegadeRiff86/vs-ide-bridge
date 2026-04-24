using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal static class SolutionFileLocator
{
    internal sealed class Match
    {
        public string Path { get; set; } = string.Empty;

        public string ProjectUniqueName { get; set; } = string.Empty;

        public int Score { get; set; }

        public string Source { get; set; } = "solution";
    }

    public static IReadOnlyList<Match> FindMatches(
        DTE2 dte,
        string query,
        string? pathFilter = null,
        IReadOnlyCollection<string>? extensions = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string trimmedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return [];
        }

        string normalizedQuery = NormalizeQuery(trimmedQuery);
        string? queryFileName = Path.GetFileName(trimmedQuery.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

        return [.. EnumerateSolutionFiles(dte)
            .Where(item => IsAccessibleSolutionPath(dte, item.Path))
            .Select(item => new Match
            {
                Path = item.Path,
                ProjectUniqueName = item.ProjectUniqueName,
                Score = ScoreMatch(item.Path, trimmedQuery, normalizedQuery, queryFileName),
                Source = "solution",
            })
            .Where(item => item.Score > 0 && MatchesFilters(item.Path, pathFilter, extensions))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path.Length)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            ];
    }

    public static string? TryFindProjectUniqueName(DTE2 dte, string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string normalizedPath;
        try
        {
            normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        }
        catch
        {
            return null;
        }

        foreach ((string Path, string ProjectUniqueName) item in EnumerateSolutionFiles(dte))
        {
            if (string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(item.ProjectUniqueName) ? null : item.ProjectUniqueName;
            }
        }

        return null;
    }

    private static bool IsAccessibleSolutionPath(DTE2 dte, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (File.Exists(path))
        {
            return true;
        }

        foreach (Document document in dte.Documents)
        {
            string? fullName = document?.FullName;
            if (!string.IsNullOrWhiteSpace(fullName) &&
                PathNormalization.AreEquivalent(fullName, path))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<Match> FindDiskMatches(
        DTE2 dte,
        string query,
        string? pathFilter = null,
        IReadOnlyCollection<string>? extensions = null,
        int maxResults = 200)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string trimmedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return [];
        }

        string? solutionDirectory = GetSolutionDirectory(dte);
        if (string.IsNullOrWhiteSpace(solutionDirectory) || !Directory.Exists(solutionDirectory))
        {
            return [];
        }

        string normalizedQuery = NormalizeQuery(trimmedQuery);
        string? queryFileName = Path.GetFileName(trimmedQuery.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        List<Match> results = [];
        HashSet<string> visited = [];

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(solutionDirectory, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return [];
        }

        foreach (var candidate in files)
        {
            string normalizedPath;
            try
            {
                normalizedPath = PathNormalization.NormalizeFilePath(candidate);
            }
            catch
            {
                continue;
            }

            if (!visited.Add(normalizedPath.ToLowerInvariant()))
            {
                continue;
            }

            if (!MatchesFilters(normalizedPath, pathFilter, extensions))
            {
                continue;
            }

            int score = ScoreMatch(normalizedPath, trimmedQuery, normalizedQuery, queryFileName);
            if (score <= 0)
            {
                continue;
            }

            results.Add(new Match
            {
                Path = normalizedPath,
                ProjectUniqueName = string.Empty,
                Score = score,
                Source = "disk",
            });

            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return [.. results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path.Length)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            ];
    }

    private static int ScoreMatch(string candidatePath, string rawQuery, string normalizedQuery, string queryFileName)
    {
        string candidateKey = NormalizeQuery(candidatePath);
        string? candidateName = Path.GetFileName(candidatePath);
        int score = 0;

        if (Path.IsPathRooted(rawQuery) && PathNormalization.AreEquivalent(candidatePath, rawQuery))
        {
            return 1000;
        }

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            if (string.Equals(candidateKey, normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 950);
            }

            if (candidateKey.EndsWith("/" + normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                candidateKey.EndsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 900);
            }

            if (candidateKey.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 500);
            }
        }

        if (!string.IsNullOrWhiteSpace(queryFileName))
        {
            if (string.Equals(candidateName, queryFileName, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 700);
            }
            else if (candidateName.IndexOf(queryFileName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score = Math.Max(score, 400);
            }
        }

        if (candidatePath.IndexOf(rawQuery, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score = Math.Max(score, 300);
        }

        score += GetPathPreferenceScore(candidatePath);

        return score;
    }

    private static int GetPathPreferenceScore(string candidatePath)
    {
        string normalizedPath = NormalizeQuery(candidatePath);
        int score = 0;

        if (normalizedPath.Contains("/src/", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (normalizedPath.Contains("/build/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/out/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/output/", StringComparison.OrdinalIgnoreCase))
        {
            score -= 30;
        }

        return score;
    }

    private static string NormalizeQuery(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string normalized = Path.IsPathRooted(path)
            ? PathNormalization.NormalizeFilePath(path)
            : path.Trim();

        normalized = normalized
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart('.', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

        return normalized.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool MatchesFilters(string candidatePath, string? pathFilter, IReadOnlyCollection<string>? extensions)
    {
        if (!string.IsNullOrWhiteSpace(pathFilter))
        {
            string normalizedFilter = NormalizeQuery(pathFilter!);
            string normalizedCandidate = NormalizeQuery(candidatePath);
            if (!normalizedCandidate.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (extensions is not null && extensions.Count > 0)
        {
            string? ext = Path.GetExtension(candidatePath);
            if (string.IsNullOrWhiteSpace(ext))
            {
                return false;
            }

            bool matched = extensions.Any(item =>
            {
                string normalized = item.StartsWith(".") ? item : "." + item;
                return string.Equals(normalized, ext, StringComparison.OrdinalIgnoreCase);
            });

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static string GetSolutionDirectory(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (dte.Solution?.IsOpen != true || string.IsNullOrWhiteSpace(dte.Solution.FullName))
        {
            return string.Empty;
        }

        return Path.GetDirectoryName(PathNormalization.NormalizeFilePath(dte.Solution.FullName)) ?? string.Empty;
    }

    internal static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateSolutionFiles(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (dte.Solution?.IsOpen != true)
        {
            yield break;
        }

        foreach (Project project in dte.Solution.Projects)
        {
            if (project is null)
                continue;

            foreach (var file in EnumerateProjectFiles(project))
            {
                yield return file;
            }
        }
    }

    internal static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateProjectFiles(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (project is null)
        {
            yield break;
        }

        if (string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ProjectItem item in project.ProjectItems)
            {
                if (item.SubProject is null)
                    continue;
                foreach (var file in EnumerateProjectFiles(item.SubProject))
                    yield return file;
            }

            yield break;
        }

        if (project.ProjectItems is null)
            yield break;

        foreach (ProjectItem item in project.ProjectItems)
        {
            foreach (var file in EnumerateProjectItemFiles(item, project.UniqueName))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateProjectItemFiles(ProjectItem item, string projectUniqueName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (item.FileCount > 0)
        {
            for (short i = 1; i <= item.FileCount; i++)
            {
                string fileName = item.FileNames[i];
                if (!string.IsNullOrWhiteSpace(fileName))
                    yield return (PathNormalization.NormalizeFilePath(fileName), projectUniqueName);
            }
        }

        if (item.ProjectItems is null)
        {
            yield break;
        }

        foreach (ProjectItem child in item.ProjectItems)
        {
            foreach (var file in EnumerateProjectItemFiles(child, projectUniqueName))
            {
                yield return file;
            }
        }
    }
}

