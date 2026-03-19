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
using VsIdeBridge.Services.Diagnostics;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Services;

internal sealed class PatchService
{
    private const string InvalidArgumentsCode = "invalid_arguments";
    private const string DevNullPath = "/dev/null";
    private const int EditorPatchHeaderPrefixLength = 2;
    private static readonly string[] HunkBoundaryPrefixes =
    [
        "@@ ",
        "--- ",
        "diff --git ",
        "index ",
        "Index: ",
        "new file mode ",
        "deleted file mode ",
        "old mode ",
        "new mode ",
        "similarity index ",
        "rename from ",
        "rename to ",
        "Binary files ",
        "GIT binary patch",
    ];

    private sealed class ChangedRange
    {
        public int StartLine { get; set; }

        public int EndLine { get; set; }
    }

    private sealed class FilePatch
    {
        public string OldPath { get; set; } = string.Empty;

        public string NewPath { get; set; } = string.Empty;

        public List<Hunk> Hunks { get; set; } = [];

        public List<SearchBlock> SearchBlocks { get; set; } = [];

        public string Format { get; set; } = "unified-diff";
    }

    private sealed class SearchBlock
    {
        public string Header { get; set; } = string.Empty;

        public List<HunkLine> Lines { get; set; } = [];
    }

    private sealed class Hunk
    {
        public int OriginalStart { get; set; }

        public int OriginalCount { get; set; }

        public int NewStart { get; set; }

        public int NewCount { get; set; }

        public List<HunkLine> Lines { get; set; } = [];
    }

    private sealed class HunkLine
    {
        public char Kind { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    private sealed class ApplyFilePatchResult
    {
        public string Content { get; set; } = string.Empty;

        public int FirstChangedLine { get; set; }

        public bool DeleteFile { get; set; }

        public List<ChangedRange> ChangedRanges { get; set; } = [];

        public List<int> DeletedLineMarkers { get; set; } = [];

        public int MatchedLineCount { get; set; }

        public int MutationLineCount { get; set; }
    }

    private sealed class PatchPaths
    {
        public string SourcePath { get; set; } = string.Empty;

        public string TargetPath { get; set; } = string.Empty;

        public bool IsNewFile { get; set; }

        public bool IsMove =>
            !string.IsNullOrWhiteSpace(SourcePath) &&
            !PathNormalization.AreEquivalent(SourcePath, TargetPath);
    }

    public async Task<JObject> ApplyUnifiedDiffAsync(
        DTE2 dte,
        DocumentService documentService,
        string? patchFilePath,
        string? patchText,
        string? baseDirectory,
        bool openChangedFiles,
        bool saveChangedFiles)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var previousActiveDocument = CaptureActiveDocumentLocation(dte);

        if (string.IsNullOrWhiteSpace(patchFilePath) == string.IsNullOrWhiteSpace(patchText))
        {
            throw new CommandErrorException(InvalidArgumentsCode, "Specify exactly one of --patch-file or --patch-text-base64.");
        }

        string patchSource;
        if (!string.IsNullOrWhiteSpace(patchFilePath))
        {
            patchSource = PathNormalization.NormalizeFilePath(patchFilePath);
            if (!File.Exists(patchSource))
            {
                throw new CommandErrorException("document_not_found", $"Patch file not found: {patchSource}");
            }

            patchText = File.ReadAllText(patchSource);
        }
        else
        {
            patchSource = "inline-base64";
            patchText ??= string.Empty;
        }

        var (filePatches, patchFormat) = ParseSupportedPatchFormat(patchText);
        if (filePatches.Count == 0)
        {
            throw new CommandErrorException(InvalidArgumentsCode, BuildMissingPatchFormatMessage(patchText));
        }

        var resolvedBaseDirectory = ResolveBaseDirectory(dte, baseDirectory);
        var appliedFiles = new JArray();
        var filesToFocus = new List<(string Path, List<ChangedRange> Ranges)>();

        foreach (var filePatch in filePatches)
        {
            var paths = ResolvePatchPaths(dte, resolvedBaseDirectory, filePatch);
            EnsurePatchHasMeaningfulOperations(filePatch, paths);
            EnsureSafeToModifyOpenDocument(dte, paths.SourcePath);
            if (paths.IsMove)
            {
                EnsureSafeToModifyOpenDocument(dte, paths.TargetPath);
                if (File.Exists(paths.TargetPath))
                {
                    throw new CommandErrorException("unsupported_operation", $"Patch move target already exists: {paths.TargetPath}");
                }
            }

            var targetExists = PatchTargetExists(dte, paths.TargetPath);
            var sourceContent = paths.IsNewFile ? string.Empty : ReadContentFromEditorOrDisk(dte, paths.SourcePath);
            var requestedTargetContent = PathNormalization.AreEquivalent(paths.SourcePath, paths.TargetPath) && !paths.IsNewFile
                ? sourceContent
                : ReadContentFromEditorOrDisk(dte, paths.TargetPath);
            ApplyFilePatchResult result;
            var alreadySatisfied = false;
            if (paths.IsNewFile)
            {
                result = ApplyFilePatch(paths.TargetPath, string.Empty, filePatch);
                var addFileDecision = AddFilePatchSemantics.Evaluate(result.Content, targetExists ? requestedTargetContent : null);
                switch (addFileDecision)
                {
                    case AddFilePatchDecision.Create:
                        break;
                    case AddFilePatchDecision.AlreadySatisfied:
                        result = CreateAlreadySatisfiedResult(requestedTargetContent, result);
                        alreadySatisfied = true;
                        break;
                    default:
                        throw new CommandErrorException("unsupported_operation", $"Patch add target already exists with different content: {paths.TargetPath}");
                }
            }
            else
            {
                try
                {
                    result = ApplyFilePatch(paths.SourcePath, sourceContent, filePatch);
                }
                catch (CommandErrorException ex) when (!paths.IsMove && IsPatchContentMismatch(ex))
                {
                    if (!IsCurrentContentAlreadyPatched(paths.TargetPath, requestedTargetContent, filePatch))
                    {
                        throw;
                    }

                    var reverseResult = ApplyFilePatch(paths.TargetPath, requestedTargetContent, CreateReversePatch(filePatch));
                    result = CreateAlreadySatisfiedResult(requestedTargetContent, reverseResult);
                    alreadySatisfied = true;
                }
            }

            if (result.DeleteFile)
            {
                CloseOpenDocumentIfPresent(dte, paths.SourcePath);
                if (File.Exists(paths.SourcePath))
                {
                    File.Delete(paths.SourcePath);
                }

                appliedFiles.Add(new JObject
                {
                    ["path"] = paths.SourcePath,
                    ["status"] = "deleted",
                    ["firstChangedLine"] = result.FirstChangedLine,
                    ["hunkCount"] = filePatch.Hunks.Count + filePatch.SearchBlocks.Count,
                    ["changedRanges"] = CreateChangedRangesArray(result.ChangedRanges),
                });
            }
            else
            {
                var requestedContentChange = !string.Equals(requestedTargetContent, result.Content, StringComparison.Ordinal);
                if (!requestedContentChange && alreadySatisfied)
                {
                    filesToFocus.Add((paths.TargetPath, result.ChangedRanges));

                    appliedFiles.Add(new JObject
                    {
                        ["path"] = paths.TargetPath,
                        ["status"] = "already-satisfied",
                        ["firstChangedLine"] = result.FirstChangedLine,
                        ["hunkCount"] = filePatch.Hunks.Count + filePatch.SearchBlocks.Count,
                        ["changedRanges"] = CreateChangedRangesArray(result.ChangedRanges),
                        ["matchedLineCount"] = result.MatchedLineCount,
                        ["mutationLineCount"] = result.MutationLineCount,
                        ["editorBacked"] = false,
                        ["verified"] = true,
                        ["contentChanged"] = false,
                        ["requestedContentChange"] = false,
                        ["alreadySatisfied"] = true,
                        ["saved"] = true,
                    });
                    continue;
                }

                if (!requestedContentChange && !paths.IsMove && !paths.IsNewFile)
                {
                    alreadySatisfied = IsCurrentContentAlreadyPatched(paths.TargetPath, requestedTargetContent, filePatch);
                    if (!alreadySatisfied && result.MutationLineCount > 0 && result.MatchedLineCount == 0)
                    {
                        throw new CommandErrorException(
                            InvalidArgumentsCode,
                            $"Patch produced no content change for {paths.TargetPath} — {result.MutationLineCount} mutation line(s) but 0 matched context lines. " +
                            "The patch content does not match the file. Fix: call read_file to check the actual content before retrying.",
                            new
                            {
                                path = paths.TargetPath,
                                hunkCount = filePatch.Hunks.Count + filePatch.SearchBlocks.Count,
                                result.MutationLineCount,
                                result.MatchedLineCount,
                            });
                    }

                    filesToFocus.Add((paths.TargetPath, result.ChangedRanges));

                    appliedFiles.Add(new JObject
                    {
                        ["path"] = paths.TargetPath,
                        ["status"] = "already-satisfied",
                        ["firstChangedLine"] = result.FirstChangedLine,
                        ["hunkCount"] = filePatch.Hunks.Count + filePatch.SearchBlocks.Count,
                        ["changedRanges"] = CreateChangedRangesArray(result.ChangedRanges),
                        ["matchedLineCount"] = result.MatchedLineCount,
                        ["mutationLineCount"] = result.MutationLineCount,
                        ["editorBacked"] = false,
                        ["verified"] = true,
                        ["contentChanged"] = false,
                        ["requestedContentChange"] = false,
                        ["alreadySatisfied"] = alreadySatisfied,
                        ["saved"] = true,
                    });
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(paths.TargetPath)!);

                // Pre-write best-practice analysis — surface warnings before the content lands.
                IReadOnlyList<JObject> preWriteWarnings = ErrorListService.AnalyzeContentBeforeWrite(paths.TargetPath, result.Content);
                string? projectUniqueName = SolutionFileLocator.TryFindProjectUniqueName(dte, paths.TargetPath);

                var writeResult = await documentService.WriteDocumentTextAsync(
                    dte,
                    paths.TargetPath,
                    result.Content,
                    result.FirstChangedLine,
                    1,
                    saveChangedFiles,
                    [.. result.ChangedRanges.Select(range => (range.StartLine, range.EndLine))],
                    result.DeletedLineMarkers).ConfigureAwait(true);

                var contentChanged = (bool?)writeResult["contentChanged"] ?? true;
                var verified = (bool?)writeResult["verified"] ?? false;
                alreadySatisfied = requestedContentChange && !contentChanged && verified;

                if (paths.IsMove)
                {
                    CloseOpenDocumentIfPresent(dte, paths.SourcePath);
                    if (File.Exists(paths.SourcePath))
                    {
                        File.Delete(paths.SourcePath);
                    }
                }

                filesToFocus.Add((paths.TargetPath, result.ChangedRanges));

                var fileItem = new JObject
                {
                    ["path"] = paths.TargetPath,
                    ["status"] = paths.IsMove ? "moved" : alreadySatisfied ? "already-satisfied" : paths.IsNewFile ? "added" : "modified",
                    ["firstChangedLine"] = result.FirstChangedLine,
                    ["hunkCount"] = filePatch.Hunks.Count + filePatch.SearchBlocks.Count,
                    ["changedRanges"] = CreateChangedRangesArray(result.ChangedRanges),
                    ["editorBacked"] = writeResult["editorBacked"] ?? false,
                    ["verified"] = verified,
                    ["contentChanged"] = contentChanged,
                    ["requestedContentChange"] = requestedContentChange,
                    ["alreadySatisfied"] = alreadySatisfied,
                    ["saved"] = writeResult["saved"] ?? saveChangedFiles,
                };

                if (preWriteWarnings.Count > 0)
                {
                    fileItem["bestPracticeWarnings"] = BestPracticeWarningProjector.CreateResponseWarnings(preWriteWarnings, projectUniqueName);
                }

                appliedFiles.Add(fileItem);
            }
        }

        if (openChangedFiles && filesToFocus.Count > 0)
        {
            var primaryRange = filesToFocus[0].Ranges.FirstOrDefault();
            if (primaryRange is not null)
            {
                await documentService.RevealDocumentRangeAsync(
                    dte,
                    filesToFocus[0].Path,
                    primaryRange.StartLine,
                    1,
                    primaryRange.EndLine,
                    1).ConfigureAwait(true);
            }
            else
            {
                await documentService.OpenDocumentAsync(dte, filesToFocus[0].Path, 1, 1).ConfigureAwait(true);
            }
        }
        else
        {
            await RestoreActiveDocumentAsync(dte, documentService, previousActiveDocument).ConfigureAwait(true);
        }

        return new JObject
        {
            ["patchSource"] = patchSource,
            ["baseDirectory"] = resolvedBaseDirectory,
            ["count"] = appliedFiles.Count,
            ["patchFormat"] = patchFormat,
            ["openChangedFiles"] = openChangedFiles,
            ["saveChangedFiles"] = saveChangedFiles,
            ["visibleEdits"] = true,
            ["items"] = appliedFiles,
        };
    }

    private static JArray CreateChangedRangesArray(IEnumerable<ChangedRange> ranges)
    {
        return [.. ranges.Select(range => new JObject
        {
            ["startLine"] = range.StartLine,
            ["endLine"] = range.EndLine,
        })];
    }

    private static void EnsurePatchHasMeaningfulOperations(FilePatch patch, PatchPaths paths)
    {
        var hunkCount = patch.Hunks.Count + patch.SearchBlocks.Count;
        if (hunkCount == 0)
        {
            if (paths.IsMove || paths.IsNewFile || patch.NewPath == DevNullPath)
            {
                return;
            }

            throw new CommandErrorException(
                InvalidArgumentsCode,
                $"Patch for {paths.TargetPath} has no hunks or search blocks — it parsed as empty. " +
                "Check your patch format: unified diff needs @@ hunk headers, editor patch needs @@ separators between context/change lines.");
        }

        if (CountPatchMutationLines(patch) > 0 || paths.IsMove)
        {
            return;
        }

        throw new CommandErrorException(
            InvalidArgumentsCode,
            $"Patch for {paths.TargetPath} contains only context lines (' ' prefix) with no additions ('+') or deletions ('-'). " +
            "Every patch must change at least one line.");
    }

    private static int CountPatchMutationLines(FilePatch patch)
    {
        return patch.Hunks.SelectMany(hunk => hunk.Lines)
            .Concat(patch.SearchBlocks.SelectMany(block => block.Lines))
            .Count(line => line.Kind == '+' || line.Kind == '-');
    }

    private static bool IsCurrentContentAlreadyPatched(string path, string currentContent, FilePatch patch)
    {
        try
        {
            var reverseResult = ApplyFilePatch(path, currentContent, CreateReversePatch(patch));
            return reverseResult.MutationLineCount > 0 && reverseResult.MatchedLineCount > 0;
        }
        catch (CommandErrorException)
        {
            return false;
        }
    }

    private static FilePatch CreateReversePatch(FilePatch patch)
    {
        return new FilePatch
        {
            OldPath = patch.NewPath,
            NewPath = patch.OldPath,
            Hunks = [.. patch.Hunks.Select(CreateReverseHunk)],
            SearchBlocks = [.. patch.SearchBlocks.Select(CreateReverseSearchBlock)],
            Format = patch.Format,
        };
    }

    private static Hunk CreateReverseHunk(Hunk hunk)
    {
        return new Hunk
        {
            OriginalStart = hunk.NewStart,
            OriginalCount = hunk.NewCount,
            NewStart = hunk.OriginalStart,
            NewCount = hunk.OriginalCount,
            Lines = [.. hunk.Lines.Select(CreateReverseHunkLine)],
        };
    }

    private static SearchBlock CreateReverseSearchBlock(SearchBlock block)
    {
        return new SearchBlock
        {
            Header = block.Header,
            Lines = [.. block.Lines.Select(CreateReverseHunkLine)],
        };
    }

    private static HunkLine CreateReverseHunkLine(HunkLine line)
    {
        return new HunkLine
        {
            Kind = line.Kind switch
            {
                '+' => '-',
                '-' => '+',
                _ => line.Kind,
            },
            Text = line.Text,
        };
    }

    private static bool IsPatchContentMismatch(CommandErrorException ex)
        => string.Equals(ex.Code, InvalidArgumentsCode, StringComparison.Ordinal)
            && ex.Message.Contains("mismatch", StringComparison.OrdinalIgnoreCase);

    private static ApplyFilePatchResult CreateAlreadySatisfiedResult(string content, ApplyFilePatchResult reverseResult)
        => new()
        {
            Content = content,
            FirstChangedLine = reverseResult.FirstChangedLine,
            DeleteFile = false,
            ChangedRanges = reverseResult.ChangedRanges,
            DeletedLineMarkers = [],
            MatchedLineCount = reverseResult.MatchedLineCount,
            MutationLineCount = reverseResult.MutationLineCount,
        };

    private static void EnsureSafeToModifyOpenDocument(DTE2 dte, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var normalizedPath = PathNormalization.NormalizeFilePath(path);
        var openDocument = TryFindOpenDocumentByPath(dte, normalizedPath);

        if (openDocument is null)
        {
            return;
        }

        if (!openDocument.Saved)
        {
            throw new CommandErrorException("unsupported_operation",
                $"Cannot patch {normalizedPath} — it has unsaved changes in the VS editor. Fix: call save_document first, then retry the patch.");
        }
    }

    private static void CloseOpenDocumentIfPresent(DTE2 dte, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var normalizedPath = PathNormalization.NormalizeFilePath(path);
        var openDocument = TryFindOpenDocumentByPath(dte, normalizedPath);

        if (openDocument is null)
        {
            return;
        }

        if (!openDocument.Saved)
        {
            throw new CommandErrorException("unsupported_operation",
                $"Cannot close {normalizedPath} — it has unsaved changes. Fix: call save_document first, then retry.");
        }

        openDocument.Close(vsSaveChanges.vsSaveChangesNo);
    }

    private static Document? TryFindOpenDocumentByPath(DTE2 dte, string normalizedPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (Document document in dte.Documents)
        {
            var fullName = document.FullName;
            if (string.IsNullOrWhiteSpace(fullName))
            {
                continue;
            }

            if (PathNormalization.AreEquivalent(fullName, normalizedPath))
            {
                return document;
            }
        }

        return null;
    }

    private static PatchPaths ResolvePatchPaths(DTE2 dte, string baseDirectory, FilePatch patch)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var isNewFile = patch.OldPath == DevNullPath;
        var isDelete = patch.NewPath == DevNullPath;
        var sourceRelativePath = isNewFile ? patch.NewPath : patch.OldPath;
        var targetRelativePath = isDelete ? patch.OldPath : patch.NewPath;
        if (string.IsNullOrWhiteSpace(sourceRelativePath) || sourceRelativePath == DevNullPath)
        {
            throw new CommandErrorException(InvalidArgumentsCode, "Patch entry did not contain a usable source path.");
        }

        if (string.IsNullOrWhiteSpace(targetRelativePath) || targetRelativePath == DevNullPath)
        {
            throw new CommandErrorException(InvalidArgumentsCode, "Patch entry did not contain a usable target path.");
        }

        var sourcePath = ResolvePatchPath(dte, baseDirectory, sourceRelativePath, allowCreate: isNewFile);
        var targetPath = isDelete
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

        var solutionDirectory = dte.Solution?.IsOpen == true
            ? Path.GetDirectoryName(dte.Solution.FullName) ?? baseDirectory
            : baseDirectory;

        var searchRoots = new List<string>();
        AddDistinctPath(searchRoots, baseDirectory);

        var current = solutionDirectory;
        for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            AddDistinctPath(searchRoots, current);
            current = Path.GetDirectoryName(current);
        }

        foreach (var root in searchRoots)
        {
            var candidate = PathNormalization.NormalizeFilePath(Path.Combine(root, relativeOrAbsolutePath));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (allowCreate)
            {
                var candidateDirectory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(candidateDirectory) && Directory.Exists(candidateDirectory))
                {
                    return candidate;
                }
            }
        }

        // Filesystem walk did not find the file. Search open VS documents as a fallback.
        // This handles bare filenames (e.g. "connect.cpp") and relative paths from projects
        // whose source tree is not under the solution directory.
        var normalizedTarget = relativeOrAbsolutePath.Replace('/', Path.DirectorySeparatorChar);
        var targetFileName = System.IO.Path.GetFileName(normalizedTarget);
        string? filenameMatch = null;

        foreach (Document document in dte.Documents)
        {
            var docPath = document.FullName;
            if (string.IsNullOrWhiteSpace(docPath))
            {
                continue;
            }

            // Prefer a document whose full path ends with the relative path from the patch.
            var normalizedDocPath = docPath.Replace('/', Path.DirectorySeparatorChar);
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

    private static void AddDistinctPath(List<string> paths, string? value)
    {
        var normalizedValue = value;
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
        var baseDir = ResolveBaseDirectory(dte, null);
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
            var solutionDirectory = Path.GetDirectoryName(dte.Solution.FullName);
            if (!string.IsNullOrWhiteSpace(solutionDirectory))
            {
                return PathNormalization.NormalizeFilePath(solutionDirectory);
            }
        }

        return PathNormalization.NormalizeFilePath(Environment.CurrentDirectory);
    }

    /// <summary>
    /// Reads file content from the VS editor buffer if the file is open, otherwise from disk.
    /// The editor buffer is the source of truth ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â it may contain unsaved changes from a
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
                        var start = textDoc.StartPoint.CreateEditPoint();
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

        var normalizedPath = PathNormalization.NormalizeFilePath(path);
        return File.Exists(normalizedPath)
            || TryFindOpenDocumentByPath(dte, normalizedPath) is not null;
    }

    private static (string Path, int Line, int Column)? CaptureActiveDocumentLocation(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var activeDocument = dte.ActiveDocument;
        if (activeDocument is null || string.IsNullOrWhiteSpace(activeDocument.FullName))
        {
            return null;
        }

        var line = 1;
        var column = 1;
        try
        {
            if (activeDocument.Object("TextDocument") is TextDocument textDocument)
            {
                line = Math.Max(1, textDocument.Selection.ActivePoint.Line);
                column = Math.Max(1, textDocument.Selection.ActivePoint.DisplayColumn);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return (PathNormalization.NormalizeFilePath(activeDocument.FullName), line, column);
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

    private static ApplyFilePatchResult ApplyFilePatch(string path, string existingText, FilePatch patch)
    {
        if (patch.SearchBlocks.Count > 0)
        {
            return ApplySearchBlockPatch(path, existingText, patch);
        }

        var newline = DetectNewline(existingText);
        var existingLines = SplitLines(existingText, out var hadFinalNewline);
        var resultLines = new List<string>();
        var sourceIndex = 0;
        var firstChangedLine = 1;
        var firstChangeCaptured = false;
        var changedRanges = new List<ChangedRange>();
        var deletedLineMarkers = new List<int>();
        var matchedLineCount = 0;
        var mutationLineCount = 0;

        foreach (var hunk in GetOrderedHunks(patch.Hunks))
        {
            var targetIndex = Math.Max(0, hunk.OriginalStart - 1);
            if (targetIndex < sourceIndex)
            {
                throw new CommandErrorException(InvalidArgumentsCode,
                    $"Patch hunks overlap at line {targetIndex + 1} for {path} (previous hunk consumed through line {sourceIndex}). " +
                    "Fix: combine adjacent hunks (within ~3 lines) into a single hunk, or use editor patch format " +
                    "(*** Begin Patch / *** Update File) which uses content matching instead of line numbers.");
            }

            // Fuzzy position search: if the hunk's nominal start line doesn't match
            // the first context/deletion line, scan ±FuzzLines to find the real position.
            const int FuzzLines = 10;
            var firstCheckLine = hunk.Lines.FirstOrDefault(l => l.Kind == ' ' || l.Kind == '-');
            if (firstCheckLine is not null && targetIndex < existingLines.Count
                && !LinesMatchFuzzy(existingLines[targetIndex], firstCheckLine.Text))
            {
                var found = false;
                for (var fuzz = 1; fuzz <= FuzzLines && !found; fuzz++)
                {
                    foreach (var candidate in new[] { targetIndex + fuzz, targetIndex - fuzz })
                    {
                        if (candidate >= sourceIndex && candidate < existingLines.Count
                            && LinesMatchFuzzy(existingLines[candidate], firstCheckLine.Text))
                        {
                            targetIndex = candidate;
                            found = true;
                            break;
                        }
                    }
                }
            }

            while (sourceIndex < targetIndex && sourceIndex < existingLines.Count)
            {
                resultLines.Add(existingLines[sourceIndex]);
                sourceIndex++;
            }

            int? hunkStartLine = null;
            var hunkAddedLineCount = 0;

            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case ' ':
                        EnsureLineMatches(path, existingLines, sourceIndex, line.Text, "context");
                        matchedLineCount++;
                        resultLines.Add(existingLines[sourceIndex]);
                        sourceIndex++;
                        break;
                    case '-':
                        EnsureLineMatches(path, existingLines, sourceIndex, line.Text, "deletion");
                        matchedLineCount++;
                        mutationLineCount++;
                        if (!firstChangeCaptured)
                        {
                            firstChangedLine = Math.Max(1, resultLines.Count + 1);
                            firstChangeCaptured = true;
                        }

                        hunkStartLine ??= Math.Max(1, resultLines.Count + 1);
                        deletedLineMarkers.Add(Math.Max(1, resultLines.Count + 1));

                        sourceIndex++;
                        break;
                    case '+':
                        mutationLineCount++;
                        if (!firstChangeCaptured)
                        {
                            firstChangedLine = Math.Max(1, resultLines.Count + 1);
                            firstChangeCaptured = true;
                        }

                        hunkStartLine ??= Math.Max(1, resultLines.Count + 1);
                        hunkAddedLineCount++;
                        resultLines.Add(line.Text);
                        break;
                    default:
                        throw new CommandErrorException(InvalidArgumentsCode,
                            $"Unsupported line prefix '{line.Kind}' in patch for {path}. Each line must start with ' ' (context), '-' (deletion), or '+' (addition).");
                }
            }

            if (hunkStartLine.HasValue)
            {
                var startLine = hunkStartLine.Value;
                var endLine = hunkAddedLineCount > 0
                    ? startLine + hunkAddedLineCount - 1
                    : startLine;
                changedRanges.Add(new ChangedRange
                {
                    StartLine = startLine,
                    EndLine = endLine,
                });
            }
        }

        while (sourceIndex < existingLines.Count)
        {
            resultLines.Add(existingLines[sourceIndex]);
            sourceIndex++;
        }

        var deleteFile = patch.NewPath == DevNullPath;
        var content = JoinLines(resultLines, newline, !deleteFile && (hadFinalNewline || patch.OldPath == DevNullPath));
        return new ApplyFilePatchResult
        {
            Content = content,
            FirstChangedLine = firstChangedLine,
            DeleteFile = deleteFile,
            ChangedRanges = changedRanges,
            DeletedLineMarkers = deletedLineMarkers,
            MatchedLineCount = matchedLineCount,
            MutationLineCount = mutationLineCount,
        };
    }

    private static ApplyFilePatchResult ApplySearchBlockPatch(string path, string existingText, FilePatch patch)
    {
        var newline = DetectNewline(existingText);
        var existingLines = SplitLines(existingText, out var hadFinalNewline);
        var resultLines = new List<string>();
        var sourceIndex = 0;
        var firstChangedLine = 1;
        var firstChangeCaptured = false;
        var changedRanges = new List<ChangedRange>();
        var deletedLineMarkers = new List<int>();
        var matchedLineCount = 0;
        var mutationLineCount = 0;

        foreach (var block in patch.SearchBlocks)
        {
            var targetIndex = FindSearchBlockStart(path, existingLines, sourceIndex, block);
            while (sourceIndex < targetIndex && sourceIndex < existingLines.Count)
            {
                resultLines.Add(existingLines[sourceIndex]);
                sourceIndex++;
            }

            int? blockStartLine = null;
            var blockAddedLineCount = 0;

            foreach (var line in block.Lines)
            {
                switch (line.Kind)
                {
                    case ' ':
                        EnsureLineMatches(path, existingLines, sourceIndex, line.Text, "context");
                        matchedLineCount++;
                        resultLines.Add(existingLines[sourceIndex]);
                        sourceIndex++;
                        break;
                    case '-':
                        EnsureLineMatches(path, existingLines, sourceIndex, line.Text, "deletion");
                        matchedLineCount++;
                        mutationLineCount++;
                        if (!firstChangeCaptured)
                        {
                            firstChangedLine = Math.Max(1, resultLines.Count + 1);
                            firstChangeCaptured = true;
                        }

                        blockStartLine ??= Math.Max(1, resultLines.Count + 1);
                        deletedLineMarkers.Add(Math.Max(1, resultLines.Count + 1));
                        sourceIndex++;
                        break;
                    case '+':
                        mutationLineCount++;
                        if (!firstChangeCaptured)
                        {
                            firstChangedLine = Math.Max(1, resultLines.Count + 1);
                            firstChangeCaptured = true;
                        }

                        blockStartLine ??= Math.Max(1, resultLines.Count + 1);
                        blockAddedLineCount++;
                        resultLines.Add(line.Text);
                        break;
                    default:
                        throw new CommandErrorException(InvalidArgumentsCode, $"Unsupported editor patch line prefix '{line.Kind}' in patch for {path}.");
                }
            }

            if (blockStartLine.HasValue)
            {
                var startLine = blockStartLine.Value;
                var endLine = blockAddedLineCount > 0
                    ? startLine + blockAddedLineCount - 1
                    : startLine;
                changedRanges.Add(new ChangedRange
                {
                    StartLine = startLine,
                    EndLine = endLine,
                });
            }
        }

        while (sourceIndex < existingLines.Count)
        {
            resultLines.Add(existingLines[sourceIndex]);
            sourceIndex++;
        }

        var deleteFile = patch.NewPath == DevNullPath;
        var content = JoinLines(resultLines, newline, !deleteFile && (hadFinalNewline || patch.OldPath == DevNullPath));
        return new ApplyFilePatchResult
        {
            Content = content,
            FirstChangedLine = firstChangedLine,
            DeleteFile = deleteFile,
            ChangedRanges = changedRanges,
            DeletedLineMarkers = deletedLineMarkers,
            MatchedLineCount = matchedLineCount,
            MutationLineCount = mutationLineCount,
        };
    }

    private static IEnumerable<Hunk> GetOrderedHunks(IEnumerable<Hunk> hunks) =>
        [.. hunks
            .OrderBy(hunk => Math.Max(0, hunk.OriginalStart))
            .ThenBy(hunk => Math.Max(0, hunk.NewStart))
            .ThenBy(hunk => hunk.Lines.Count)];

    private static int FindSearchBlockStart(string path, IReadOnlyList<string> existingLines, int sourceIndex, SearchBlock block)
    {
        var matchLines = block.Lines
            .Where(line => line.Kind != '+')
            .Select(line => line.Text)
            .ToArray();

        if (matchLines.Length == 0)
        {
            // No context or deletion lines: can't locate the insertion point.
            // Default to end-of-file so pure-addition blocks append rather than
            // silently inserting at an arbitrary mid-file position.
            return existingLines.Count;
        }

        var maxStart = existingLines.Count - matchLines.Length;

        // Pass 1: exact match.
        for (var candidate = Math.Max(0, sourceIndex); candidate <= maxStart; candidate++)
        {
            var matches = true;
            for (var offset = 0; offset < matchLines.Length; offset++)
            {
                if (!string.Equals(existingLines[candidate + offset], matchLines[offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return candidate;
            }
        }

        // Second pass: fuzzy match to handle LLM escape artifacts.
        for (var candidate = Math.Max(0, sourceIndex); candidate <= maxStart; candidate++)
        {
            var matches = true;
            for (var offset = 0; offset < matchLines.Length; offset++)
            {
                if (!LinesMatchFuzzy(existingLines[candidate + offset], matchLines[offset]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return candidate;
            }
        }

        var descriptor = string.IsNullOrWhiteSpace(block.Header)
            ? "editor patch block"
            : $"editor patch block '{block.Header}'";
        var firstMatchLine = matchLines.Length > 0 ? Truncate(matchLines[0], 60) : "(empty)";
        throw new CommandErrorException(
            InvalidArgumentsCode,
            $"Could not locate {descriptor} in {path} (searched from line {sourceIndex + 1}). " +
            $"First context line: \"{firstMatchLine}\". " +
            "Fix: call read_file to verify the context lines exist in the file, then regenerate the patch with correct content.",
            new
            {
                block = block.Header,
                sourceIndex = sourceIndex + 1,
                matchLines,
            });
    }

    /// <summary>
    /// Compares two lines with tolerance for JSON/C# escape artifacts that LLMs
    /// commonly introduce.  Tries exact match first, then falls back to a
    /// normalized comparison that strips one level of backslash-escaping and
    /// trims trailing whitespace.
    /// </summary>
    private static bool LinesMatchFuzzy(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return true;
        }

        // Normalize: unescape one layer of backslash sequences and trim trailing ws.
        return string.Equals(
            NormalizeLine(actual),
            NormalizeLine(expected),
            StringComparison.Ordinal);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";

    private static string NormalizeLine(string line)
    {
        // Fast path: no backslashes at all ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â just trim trailing whitespace.
        if (line.IndexOf('\\') < 0)
        {
            return line.TrimEnd();
        }

        // Strip one level of backslash-escaping (\\\" ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ \", \\\\ ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ \\, \\n ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ \n, etc.)
        var sb = new System.Text.StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length)
            {
                var next = line[i + 1];
                if (next == '"' || next == '\\' || next == 'n' || next == 'r' || next == 't')
                {
                    sb.Append(next);
                    i++;
                    continue;
                }
            }

            sb.Append(line[i]);
        }

        return sb.ToString().TrimEnd();
    }

    private static void EnsureLineMatches(string path, IReadOnlyList<string> existingLines, int index, string expected, string operation)
    {
        if (index >= existingLines.Count)
        {
            throw new CommandErrorException(InvalidArgumentsCode,
                $"Patch {operation} at line {index + 1} exceeded file length ({existingLines.Count} lines) in {path}. " +
                "The line numbers in your patch do not match the file. " +
                "Fix: call read_file to check actual line numbers, then regenerate. " +
                "Tip: use editor patch format (*** Begin Patch) which matches by content instead of line numbers.");
        }

        if (!LinesMatchFuzzy(existingLines[index], expected))
        {
            const int ContextRadius = 3;
            var start = Math.Max(0, index - ContextRadius);
            var end = Math.Min(existingLines.Count - 1, index + ContextRadius);
            var context = string.Join("\n", Enumerable.Range(start, end - start + 1)
                .Select(i => $"  {i + 1,4}: {(i == index ? ">>>" : "   ")} {existingLines[i]}"));

            throw new CommandErrorException(
                InvalidArgumentsCode,
                $"Patch {operation} mismatch in {path} at line {index + 1}. " +
                $"Expected: \"{Truncate(expected, 80)}\" but found: \"{Truncate(existingLines[index], 80)}\". " +
                "This usually means line numbers drifted because a prior hunk added or removed lines. " +
                "Fix: call read_file with a tight range around line " + (index + 1) + " to see actual content, then regenerate. " +
                "Tip: use editor patch format (*** Begin Patch) which matches by content instead of line numbers.",
                new { expected, actual = existingLines[index], line = index + 1, fileContext = context });
        }
    }

    private static (List<FilePatch> FilePatches, string PatchFormat) ParseSupportedPatchFormat(string patchText)
    {
        if (LooksLikeEditorPatchEnvelope(patchText))
        {
            return (ParseEditorPatch(patchText), "editor-patch");
        }

        return (ParseUnifiedDiff(patchText), "unified-diff");
    }

    private static string BuildMissingPatchFormatMessage(string patchText)
    {
        if (LooksLikeEditorPatchEnvelope(patchText))
        {
            return "Patch has *** Begin Patch but no file entries. " +
                "Required structure: *** Begin Patch\\n*** Update File: <path>\\n@@\\n <context>\\n-old\\n+new\\n*** End Patch. " +
                "Each file needs *** Add File: <path>, *** Delete File: <path>, or *** Update File: <path>.";
        }

        return "Patch format not recognized. " +
            "Preferred: *** Begin Patch\\n*** Update File: <path>\\n@@\\n <context>\\n-old\\n+new\\n*** End Patch. " +
            "Also accepted: unified diff with --- a/<path> / +++ b/<path> / @@ -line,count +line,count @@ headers.";
    }

    private static bool LooksLikeEditorPatchEnvelope(string patchText)
    {
        if (string.IsNullOrWhiteSpace(patchText))
        {
            return false;
        }

        var normalized = patchText.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart();
        return normalized.StartsWith("*** Begin Patch", StringComparison.Ordinal);
    }

    private static List<FilePatch> ParseEditorPatch(string patchText)
    {
        var normalized = patchText.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0], "*** Begin Patch", StringComparison.Ordinal))
        {
            throw new CommandErrorException(InvalidArgumentsCode, "Editor patch is missing the *** Begin Patch header.");
        }

        var patches = new List<FilePatch>();
        var lineIndex = 1;
        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];
            if (string.Equals(line, "*** End of File", StringComparison.Ordinal))
            {
                // Accept Codex-style EOF sentinels as no-op markers so apply_patch envelopes
                // can be replayed without rewriting them into a second patch dialect.
                lineIndex++;
                continue;
            }

            if (string.Equals(line, "*** End Patch", StringComparison.Ordinal))
            {
                return patches;
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                patches.Add(ParseEditorAddFile(lines, ref lineIndex));
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                patches.Add(ParseEditorDeleteFile(lines, ref lineIndex));
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                patches.Add(ParseEditorUpdateFile(lines, ref lineIndex));
                continue;
            }

            throw new CommandErrorException(InvalidArgumentsCode, $"Unsupported editor patch directive: {line}");
        }

        throw new CommandErrorException(InvalidArgumentsCode, "Editor patch is missing the *** End Patch footer.");
    }

    private static FilePatch ParseEditorAddFile(string[] lines, ref int lineIndex)
    {
        var path = ParseEditorPatchPath(lines[lineIndex], "*** Add File: ");
        lineIndex++;
        var addedLines = new List<HunkLine>();
        while (lineIndex < lines.Length && !IsEditorPatchDirective(lines[lineIndex]))
        {
            var line = lines[lineIndex];
            if (line.Length == 0 || line[0] != '+')
            {
                throw new CommandErrorException(InvalidArgumentsCode, $"Added file entries must use '+' lines only: {line}");
            }

            addedLines.Add(new HunkLine { Kind = '+', Text = line.Length > 1 ? line.Substring(1) : string.Empty });
            lineIndex++;
        }

        if (addedLines.Count == 0)
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Added file patch did not contain any content: {path}");
        }

        return new FilePatch
        {
            OldPath = DevNullPath,
            NewPath = path,
            Hunks =
            [
                new Hunk
                {
                    OriginalStart = 1,
                    OriginalCount = 0,
                    NewStart = 1,
                    NewCount = addedLines.Count,
                    Lines = addedLines,
                },
            ],
            Format = "editor-patch",
        };
    }

    private static FilePatch ParseEditorDeleteFile(string[] lines, ref int lineIndex)
    {
        var path = ParseEditorPatchPath(lines[lineIndex], "*** Delete File: ");
        lineIndex++;
        return new FilePatch
        {
            OldPath = path,
            NewPath = DevNullPath,
            Format = "editor-patch",
        };
    }

    private static FilePatch ParseEditorUpdateFile(string[] lines, ref int lineIndex)
    {
        var oldPath = ParseEditorPatchPath(lines[lineIndex], "*** Update File: ");
        lineIndex++;
        var newPath = oldPath;
        if (lineIndex < lines.Length && lines[lineIndex].StartsWith("*** Move to: ", StringComparison.Ordinal))
        {
            newPath = ParseEditorPatchPath(lines[lineIndex], "*** Move to: ");
            lineIndex++;
        }

        var blocks = new List<SearchBlock>();
        SearchBlock? currentBlock = null;
        while (lineIndex < lines.Length && !IsEditorPatchDirective(lines[lineIndex]))
        {
            var line = lines[lineIndex];
            if (line == "@@" || line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (currentBlock?.Lines.Count > 0)
                {
                    blocks.Add(currentBlock);
                }

                currentBlock = new SearchBlock { Header = line.Length > EditorPatchHeaderPrefixLength ? line.Substring(EditorPatchHeaderPrefixLength).Trim() : string.Empty };
                lineIndex++;
                continue;
            }

            if (line.Length == 0)
            {
                throw new CommandErrorException(InvalidArgumentsCode, "Editor patch lines must start with ' ', '+', '-', or '@@'.");
            }

            var prefix = line[0];
            if (prefix != ' ' && prefix != '+' && prefix != '-')
            {
                throw new CommandErrorException(InvalidArgumentsCode, $"Unsupported editor patch line prefix '{prefix}'.");
            }

            currentBlock ??= new SearchBlock();
            currentBlock.Lines.Add(new HunkLine { Kind = prefix, Text = line.Length > 1 ? line.Substring(1) : string.Empty });
            lineIndex++;
        }

        if (currentBlock?.Lines.Count > 0)
        {
            blocks.Add(currentBlock);
        }

        return new FilePatch
        {
            OldPath = oldPath,
            NewPath = newPath,
            SearchBlocks = blocks,
            Format = "editor-patch",
        };
    }

    private static bool IsEditorPatchDirective(string line)
    {
        return line.StartsWith("*** ", StringComparison.Ordinal);
    }

    private static string ParseEditorPatchPath(string line, string prefix)
    {
        var path = NormalizePatchPath(line.Substring(prefix.Length));
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Editor patch directive is missing a path: {line}");
        }

        return path;
    }

    private static List<FilePatch> ParseUnifiedDiff(string patchText)
    {
        var normalized = patchText.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        var lines = normalized.Split('\n');
        var patches = new List<FilePatch>();
        FilePatch? currentFile = null;
        var lineIndex = 0;

        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var oldPath = NormalizePatchPath(line.Substring(4));
                lineIndex++;
                if (lineIndex >= lines.Length || !lines[lineIndex].StartsWith("+++ ", StringComparison.Ordinal))
                {
                    throw new CommandErrorException(InvalidArgumentsCode, "Unified diff is missing a +++ header.");
                }

                var newPath = NormalizePatchPath(lines[lineIndex].Substring(4));
                currentFile = new FilePatch
                {
                    OldPath = oldPath,
                    NewPath = newPath,
                    Hunks = [],
                };
                patches.Add(currentFile);
                lineIndex++;
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (currentFile is null)
                {
                    throw new CommandErrorException(InvalidArgumentsCode, "Encountered a hunk before a file header.");
                }

                var hunk = ParseHunkHeader(line);
                lineIndex++;
                while (lineIndex < lines.Length)
                {
                    var hunkLine = lines[lineIndex];
                    if (IsHunkBoundaryLine(hunkLine))
                    {
                        break;
                    }

                    if (hunkLine == "\\ No newline at end of file")
                    {
                        lineIndex++;
                        continue;
                    }

                    if (hunkLine.Length == 0)
                    {
                        // Blank line in hunk body = context line for an empty file line.
                        // Treating it as a skip (without advancing sourceIndex) would shift
                        // all subsequent matches off by one.
                        hunk.Lines.Add(new HunkLine { Kind = ' ', Text = string.Empty });
                        lineIndex++;
                        continue;
                    }

                    var prefix = hunkLine[0];
                    if (prefix != ' ' && prefix != '+' && prefix != '-')
                    {
                        throw new CommandErrorException(InvalidArgumentsCode, $"Unsupported hunk line prefix '{prefix}'.");
                    }

                    hunk.Lines.Add(new HunkLine
                    {
                        Kind = prefix,
                        Text = hunkLine.Length > 1 ? hunkLine.Substring(1) : string.Empty,
                    });
                    lineIndex++;
                }

                currentFile.Hunks.Add(hunk);
                continue;
            }

            lineIndex++;
        }

        return patches;
    }

    private static bool IsHunkBoundaryLine(string line)
    {
        foreach (var prefix in HunkBoundaryPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Hunk ParseHunkHeader(string line)
    {
        var match = Regex.Match(line, @"^@@ -(?<oldStart>\d+)(,(?<oldCount>\d+))? \+(?<newStart>\d+)(,(?<newCount>\d+))? @@");
        if (!match.Success)
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Invalid unified diff hunk header: {line}");
        }

        return new Hunk
        {
            OriginalStart = int.Parse(match.Groups["oldStart"].Value),
            OriginalCount = ParseHunkCount(match.Groups["oldCount"].Value),
            NewStart = int.Parse(match.Groups["newStart"].Value),
            NewCount = ParseHunkCount(match.Groups["newCount"].Value),
            Lines = [],
        };
    }

    private static int ParseHunkCount(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? 1 : int.Parse(value);
    }

    private static string NormalizePatchPath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed == DevNullPath)
        {
            return trimmed;
        }

        if ((trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal)) && trimmed.Length > EditorPatchHeaderPrefixLength)
        {
            return trimmed.Substring(EditorPatchHeaderPrefixLength);
        }

        return trimmed;
    }

    private static string DetectNewline(string text)
    {
        return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static List<string> SplitLines(string text, out bool hadFinalNewline)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        hadFinalNewline = normalized.EndsWith("\n", StringComparison.Ordinal);
        if (hadFinalNewline)
        {
            normalized = normalized.Substring(0, normalized.Length - 1);
        }

        return normalized.Length == 0 ? [] : [.. normalized.Split('\n')];
    }

    private static string JoinLines(IReadOnlyList<string> lines, string newline, bool includeTrailingNewline)
    {
        var content = string.Join(newline, lines);
        if (includeTrailingNewline)
        {
            content += newline;
        }

        return content;
    }
}







