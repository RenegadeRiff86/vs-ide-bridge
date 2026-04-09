using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Diagnostics;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Services;

internal sealed partial class PatchService
{
    private static PatchPaths ResolvePatchPaths(DTE2 dte, string baseDirectory, FilePatch patch)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        bool isNewFile = patch.OldPath == DevNullPath;
        bool isDelete = patch.NewPath == DevNullPath;
        string sourceRelativePath = isNewFile ? patch.NewPath : patch.OldPath;
        string targetRelativePath = isDelete ? patch.OldPath : patch.NewPath;
        if (string.IsNullOrWhiteSpace(sourceRelativePath) || sourceRelativePath == DevNullPath)
        {
            throw new CommandErrorException(InvalidArgumentsCode, "Patch entry did not contain a usable source path.");
        }

        if (string.IsNullOrWhiteSpace(targetRelativePath) || targetRelativePath == DevNullPath)
        {
            throw new CommandErrorException(InvalidArgumentsCode, "Patch entry did not contain a usable target path.");
        }

        string sourcePath = ResolvePatchPath(dte, baseDirectory, sourceRelativePath, allowCreate: isNewFile);
        string targetPath = isDelete
            ? sourcePath
            : ResolvePatchPath(dte, baseDirectory, targetRelativePath, allowCreate: true);

        return new PatchPaths
        {
            SourcePath = sourcePath,
            TargetPath = targetPath,
            IsNewFile = isNewFile,
        };
    }

    private static string ResolvePatchPath(DTE2 dte, string baseDirectory, string relativeOrAbsolutePath, bool allowCreate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Path.IsPathRooted(relativeOrAbsolutePath))
        {
            return PathNormalization.NormalizeFilePath(relativeOrAbsolutePath);
        }

        if (TryResolveUniqueSolutionFileByName(dte, relativeOrAbsolutePath, out string? uniqueSolutionPath, out int ambiguousMatchCount))
        {
            return uniqueSolutionPath!;
        }

        if (ambiguousMatchCount > 1)
        {
            throw new CommandErrorException(
                InvalidArgumentsCode,
                $"Patch path '{relativeOrAbsolutePath}' is ambiguous in the active solution. Use a longer relative path to disambiguate it.");
        }

        string solutionDirectory = dte.Solution?.IsOpen == true
            ? Path.GetDirectoryName(dte.Solution.FullName) ?? baseDirectory
            : baseDirectory;

        List<string> searchRoots = [];
        AddDistinctPath(searchRoots, baseDirectory);

        string current = solutionDirectory;
        for (int depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            AddDistinctPath(searchRoots, current);
            current = Path.GetDirectoryName(current);
        }

        foreach (string root in searchRoots)
        {
            string candidate = PathNormalization.NormalizeFilePath(Path.Combine(root, relativeOrAbsolutePath));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (allowCreate)
            {
                string candidateDirectory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(candidateDirectory) && Directory.Exists(candidateDirectory))
                {
                    return candidate;
                }
            }
        }

        // Filesystem walk did not find the file. Search open VS documents as a fallback.
        // This handles bare filenames (e.g. "connect.cpp") and relative paths from projects
        // whose source tree is not under the solution directory.
        string normalizedTarget = relativeOrAbsolutePath.Replace('/', Path.DirectorySeparatorChar);
        string targetFileName = System.IO.Path.GetFileName(normalizedTarget);
        string? filenameMatch = null;

        foreach (Document document in dte.Documents)
        {
            string docPath = document.FullName;
            if (string.IsNullOrWhiteSpace(docPath))
            {
                continue;
            }

            // Prefer a document whose full path ends with the relative path from the patch.
            string normalizedDocPath = docPath.Replace('/', Path.DirectorySeparatorChar);
            if (File.Exists(docPath) &&
                (normalizedDocPath.EndsWith(Path.DirectorySeparatorChar + normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                normalizedDocPath.EndsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            )
            {
                return PathNormalization.NormalizeFilePath(docPath);
            }

            // Keep the first filename-only match as a lower-priority fallback.
            if (filenameMatch is null &&
                File.Exists(docPath) &&
                System.IO.Path.GetFileName(docPath).Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                filenameMatch = docPath;
            }
        }

        if (filenameMatch is not null)
        {
            return PathNormalization.NormalizeFilePath(filenameMatch);
        }

        return PathNormalization.NormalizeFilePath(Path.Combine(baseDirectory, relativeOrAbsolutePath));
    }

    private static bool TryResolveUniqueSolutionFileByName(DTE2 dte, string relativeOrAbsolutePath, out string? resolvedPath, out int matchCount)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        resolvedPath = null;
        matchCount = 0;

        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath) ||
            relativeOrAbsolutePath.IndexOfAny(['\\', '/', ':']) >= 0 ||
            dte.Solution?.IsOpen != true)
        {
            return false;
        }

        string targetFileName = Path.GetFileName(relativeOrAbsolutePath);
        if (string.IsNullOrWhiteSpace(targetFileName))
        {
            return false;
        }

        HashSet<string> matches = CreatePathMatchSet();
        foreach (Project project in dte.Solution.Projects)
        {
            CollectMatchingProjectItemPaths(project, targetFileName, matches);
        }

        matchCount = matches.Count;
        if (matchCount == 1)
        {
            resolvedPath = PathNormalization.NormalizeFilePath(matches.First());
            return true;
        }

        return false;
    }

    private static void CollectMatchingProjectItemPaths(Project project, string targetFileName, HashSet<string> matches)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (project.ProjectItems is not null)
            {
                CollectMatchingProjectItemPaths(project.ProjectItems, targetFileName, matches);
            }

        }
        catch (COMException)
        {
            // Some project types throw while expanding items; ignore and keep searching.
        }
    }

    private static HashSet<string> CreatePathMatchSet()
    {
        return new HashSet<string>([], StringComparer.OrdinalIgnoreCase);
    }

    private static void CollectMatchingProjectItemPaths(ProjectItems items, string targetFileName, HashSet<string> matches)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (ProjectItem item in items)
        {
            try
            {
                if (string.Equals(item.Name, targetFileName, StringComparison.OrdinalIgnoreCase) && item.FileCount > 0)
                {
                    string candidate = item.FileNames[1];
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        matches.Add(candidate);
                    }
                }

                if (item.ProjectItems is { Count: > 0 })
                {
                    CollectMatchingProjectItemPaths(item.ProjectItems, targetFileName, matches);
                }

                if (item.SubProject is not null)
                {
                    CollectMatchingProjectItemPaths(item.SubProject, targetFileName, matches);
                }
            }
            catch (COMException)
            {
                // Ignore transient/unsupported project item states and continue.
            }
        }
    }

    private static void AddDistinctPath(List<string> paths, string? value)
    {
        string? normalizedValue = value;
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return;
        }

        if (!paths.Contains(normalizedValue, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(normalizedValue!);
        }
    }

    public string ResolveFilePath(DTE2 dte, string filePathOrRelative)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string baseDir = ResolveBaseDirectory(dte, null);
        return ResolvePatchPath(dte, baseDir, filePathOrRelative, allowCreate: true);
    }

    private static string ResolveBaseDirectory(DTE2 dte, string? baseDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            return PathNormalization.NormalizeFilePath(baseDirectory);
        }

        if (dte.Solution?.IsOpen == true)
        {
            string? solutionDirectory = Path.GetDirectoryName(dte.Solution.FullName);
            if (!string.IsNullOrWhiteSpace(solutionDirectory))
            {
                return PathNormalization.NormalizeFilePath(solutionDirectory);
            }
        }

        return PathNormalization.NormalizeFilePath(Environment.CurrentDirectory);
    }

    /// <summary>
    /// Reads file content from the VS editor buffer if the file is open, otherwise from disk.
    /// The editor buffer is the source of truth -- it may contain unsaved changes from a
    /// previous apply_diff or user edits that haven't been flushed to disk yet.
    /// </summary>
    private static string ReadContentFromEditorOrDisk(DTE2 dte, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            foreach (Document doc in dte.Documents)
            {
                try
                {
                    if (!PathNormalization.AreEquivalent(doc.FullName, path))
                    {
                        continue;
                    }

                    if (doc.Object("TextDocument") is TextDocument textDoc)
                    {
                        EditPoint start = textDoc.StartPoint.CreateEditPoint();
                        return start.GetText(textDoc.EndPoint);
                    }
                }
                catch (COMException)
                {
                    // Document may be in a transient state; skip it.
                }
            }
        }
        catch (COMException)
        {
            // dte.Documents can throw if VS is shutting down or busy.
        }

        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static bool PatchTargetExists(DTE2 dte, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string normalizedPath = PathNormalization.NormalizeFilePath(path);
        return File.Exists(normalizedPath)
            || TryFindOpenDocumentByPath(dte, normalizedPath) is not null;
    }

    private static (string Path, int Line, int Column)? CaptureActiveDocumentLocation(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Document activeDocument = dte.ActiveDocument;
        string? activeDocumentPath = TryGetActiveDocumentFullName(activeDocument);
        if (activeDocument is null || string.IsNullOrWhiteSpace(activeDocumentPath))
        {
            return null;
        }

        int line = 1;
        int column = 1;
        try
        {
            if (activeDocument.Object("TextDocument") is TextDocument textDocument)
            {
                line = Math.Max(1, textDocument.Selection.ActivePoint.Line);
                column = Math.Max(1, textDocument.Selection.ActivePoint.DisplayColumn);
            }
        }
        catch (Exception ex) when (IsDeferredDocumentLoadFailure(ex))
        {
            return (PathNormalization.NormalizeFilePath(activeDocumentPath), line, column);
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return (PathNormalization.NormalizeFilePath(activeDocumentPath), line, column);
    }

    private static string? TryGetActiveDocumentFullName(Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (document is null)
        {
            return null;
        }

        try
        {
            return document.FullName;
        }
        catch (Exception ex) when (IsDeferredDocumentLoadFailure(ex))
        {
            return null;
        }
    }

    private static bool IsDeferredDocumentLoadFailure(Exception ex)
    {
        return string.Equals(ex.GetType().FullName, "Microsoft.Assumes+InternalErrorException", StringComparison.Ordinal)
            || string.Equals(ex.GetType().Name, "InternalErrorException", StringComparison.Ordinal);
    }

    private static async Task RestoreActiveDocumentAsync(
        DTE2 dte,
        DocumentService documentService,
        (string Path, int Line, int Column)? previousActiveDocument)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (previousActiveDocument is null || string.IsNullOrWhiteSpace(previousActiveDocument.Value.Path))
        {
            return;
        }

        if (!File.Exists(previousActiveDocument.Value.Path))
        {
            return;
        }

        await documentService.OpenDocumentAsync(
            dte,
            previousActiveDocument.Value.Path,
            previousActiveDocument.Value.Line,
            previousActiveDocument.Value.Column).ConfigureAwait(true);
    }
}

