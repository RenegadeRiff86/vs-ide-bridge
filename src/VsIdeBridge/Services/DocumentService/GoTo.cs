using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class DocumentService
{
    public async Task<JObject> GetDocumentSliceAsync(
        DTE2 dte,
        string? filePath,
        int startLine,
        int endLine,
        bool includeLineNumbers,
        bool revealInEditor = true,
        int? revealLine = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string resolvedPath = ResolveDocumentPath(dte, filePath);
        string text = ReadDocumentText(dte, resolvedPath);
        List<string> lines = SplitLines(text);

        int clampedStart = Math.Max(1, startLine);
        int clampedEnd = Math.Max(clampedStart, endLine);
        int actualStart = Math.Min(clampedStart, Math.Max(1, lines.Count));
        int actualEnd = Math.Min(clampedEnd, Math.Max(1, lines.Count));
        bool revealedInEditor = false;
        string? revealNote = null;

        if (revealInEditor)
        {
            int requestedRevealLine = Math.Max(1, revealLine ?? actualStart);
            int actualRevealLine = Math.Min(requestedRevealLine, Math.Max(1, lines.Count));
            try
            {
                _ = await OpenDocumentAsync(dte, resolvedPath, actualRevealLine, 1, allowDiskFallback: false).ConfigureAwait(true);
                revealedInEditor = true;
            }
            catch (NotImplementedException ex) when (IsProjectSystemDocumentOpenFailure(ex, resolvedPath))
            {
                revealNote = "Reveal skipped because Visual Studio treats the file as a project-system artifact instead of an editor document.";
            }
        }

        (string sliceText, JArray sliceLines) = BuildDocumentSliceContent(lines, actualStart, actualEnd, includeLineNumbers);

        return new JObject
        {
            [ResolvedPathProperty] = resolvedPath,
            ["requestedStartLine"] = clampedStart,
            ["requestedEndLine"] = clampedEnd,
            ["actualStartLine"] = actualStart,
            ["actualEndLine"] = actualEnd,
            ["lineCount"] = lines.Count,
            ["revealedInEditor"] = revealedInEditor,
            ["revealNote"] = revealNote,
            ["text"] = sliceText,
            ["lines"] = sliceLines,
        };
    }

    private static bool IsProjectSystemDocumentOpenFailure(NotImplementedException exception, string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string message = exception.Message ?? string.Empty;
        return message.IndexOf("already open as a project or a solution", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public async Task<JObject> GoToDefinitionAsync(
        DTE2 dte,
        string? filePath,
        string? documentQuery,
        int? line,
        int? column)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        JObject sourceLocation = await PositionTextSelectionAsync(dte, filePath, documentQuery, line, column, selectWord: true)
            .ConfigureAwait(true);

        string selectedSymbolText = (string?)sourceLocation[SelectedTextProperty] ?? string.Empty;
        if (!HasNavigableSymbolText(selectedSymbolText))
        {
            return new JObject
            {
                [SourceLocationProperty] = sourceLocation,
                [DefinitionLocationProperty] = JValue.CreateNull(),
                [DefinitionFoundProperty] = false,
                ["note"] = "No navigable symbol was found under the caret.",
            };
        }

        await PostGoToDefinitionCommandAsync().ConfigureAwait(true);

        JObject? definitionLocation = ReadDefinitionLocation(dte.ActiveDocument);
        if (definitionLocation is null)
        {
            return new JObject
            {
                [SourceLocationProperty] = sourceLocation,
                [DefinitionLocationProperty] = null,
                [DefinitionFoundProperty] = false,
            };
        }

        string sourcePath = (string?)sourceLocation[ResolvedPathProperty] ?? string.Empty;
        int sourceLine = (int?)sourceLocation["line"] ?? 0;
        string definitionPath = (string?)definitionLocation[ResolvedPathProperty] ?? string.Empty;
        int definitionLine = (int?)definitionLocation["line"] ?? 0;
        bool definitionFound = !string.Equals(sourcePath, definitionPath, StringComparison.OrdinalIgnoreCase)
            || sourceLine != definitionLine;

        return new JObject
        {
            [SourceLocationProperty] = sourceLocation,
            [DefinitionLocationProperty] = definitionLocation,
            [DefinitionFoundProperty] = definitionFound,
        };
    }

    public async Task<JObject> GoToImplementationAsync(
        DTE2 dte,
        string? filePath,
        string? documentQuery,
        int? line,
        int? column)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        JObject sourceLocation = await PositionTextSelectionAsync(dte, filePath, documentQuery, line, column, selectWord: true)
            .ConfigureAwait(true);

        string selectedText = (string?)sourceLocation[SelectedTextProperty] ?? string.Empty;
        if (!HasNavigableSymbolText(selectedText))
        {
            return new JObject
            {
                [SourceLocationProperty] = sourceLocation,
                [ImplementationLocationProperty] = sourceLocation,
                [ImplementationFoundProperty] = false,
                ["note"] = "No navigable symbol was found under the caret.",
            };
        }

        try
        {
            dte.ExecuteCommand("Edit.GoToImplementation", string.Empty);
            await Task.Delay(500).ConfigureAwait(true);
        }
        catch (COMException ex)
        {
            throw new CommandErrorException(
                UnsupportedOperationCode,
                $"GoToImplementation failed: {ex.Message}");
        }

        JObject implementationLocation = await PositionTextSelectionAsync(dte, null, null, null, null, selectWord: false)
            .ConfigureAwait(true);
        string sourcePath = (string?)sourceLocation[ResolvedPathProperty] ?? string.Empty;
        int sourceLine = (int?)sourceLocation["line"] ?? 0;
        string implementationPath = (string?)implementationLocation[ResolvedPathProperty] ?? string.Empty;
        int implementationLine = (int?)implementationLocation["line"] ?? 0;
        bool implementationFound = !string.Equals(sourcePath, implementationPath, StringComparison.OrdinalIgnoreCase)
            || sourceLine != implementationLine;

        return new JObject
        {
            [SourceLocationProperty] = sourceLocation,
            [ImplementationLocationProperty] = implementationLocation,
            [ImplementationFoundProperty] = implementationFound,
        };
    }

    public async Task<JObject> GetDocumentSlicesAsync(DTE2 dte, JArray ranges)
    {
        JArray results = [];
        foreach (JToken rangeToken in ranges)
        {
            if (rangeToken is not JObject range)
            {
                continue;
            }

            (string? file, int startLine, int endLine) = ParseSliceRangeArgs(range);

            try
            {
                JObject slice = await GetDocumentSliceAsync(dte, file, startLine, endLine, includeLineNumbers: true)
                    .ConfigureAwait(false);
                results.Add(slice);
            }
            catch (CommandErrorException ex)
            {
                results.Add(new JObject
                {
                    [ResolvedPathProperty] = file ?? string.Empty,
                    ["error"] = ex.Message,
                });
            }
            catch (IOException ex)
            {
                results.Add(new JObject
                {
                    [ResolvedPathProperty] = file ?? string.Empty,
                    ["error"] = ex.Message,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                results.Add(new JObject
                {
                    [ResolvedPathProperty] = file ?? string.Empty,
                    ["error"] = ex.Message,
                });
            }
            catch (InvalidOperationException ex)
            {
                results.Add(new JObject
                {
                    [ResolvedPathProperty] = file ?? string.Empty,
                    ["error"] = ex.Message,
                });
            }
        }

        return new JObject
        {
            ["count"] = results.Count,
            ["slices"] = results,
        };
    }

    public async Task<JObject> GetQuickInfoAsync(DTE2 dte, string? filePath, string? documentQuery, int? line, int? column, int contextLines)
    {
        JObject sourceLocation = await PositionTextSelectionAsync(dte, filePath, documentQuery, line, column, selectWord: true)
            .ConfigureAwait(false);

        string sourcePath = (string?)sourceLocation[ResolvedPathProperty] ?? string.Empty;
        int sourceLine = (int?)sourceLocation["line"] ?? 0;
        string word = ((string?)sourceLocation[SelectedTextProperty] ?? string.Empty).Trim();
        sourceLocation[SelectedTextProperty] = word;

        if (!HasNavigableSymbolText(word))
        {
            return new JObject
            {
                ["word"] = word,
                [SourceLocationProperty] = sourceLocation,
                [DefinitionFoundProperty] = false,
                [DefinitionLocationProperty] = JValue.CreateNull(),
                ["definitionContext"] = JValue.CreateNull(),
                ["note"] = "No symbol was found under the caret.",
            };
        }

        bool definitionFound = false;
        JObject? defLocation = null;
        JObject? definitionSlice = null;
        try
        {
            (definitionFound, defLocation, definitionSlice) = await FetchDefinitionSliceAsync(
                dte, sourcePath, sourceLine, (int?)sourceLocation["column"], contextLines).ConfigureAwait(false);
        }
        finally
        {
            await TryRestoreLocationAsync(
                dte,
                sourcePath,
                sourceLine,
                (int?)sourceLocation["column"] ?? column ?? 1).ConfigureAwait(false);
        }

        return new JObject
        {
            ["word"] = word,
            [SourceLocationProperty] = sourceLocation,
            [DefinitionFoundProperty] = definitionFound,
            [DefinitionLocationProperty] = defLocation ?? (JToken)JValue.CreateNull(),
            ["definitionContext"] = definitionSlice ?? (JToken)JValue.CreateNull(),
        };
    }

    public async Task<JObject> PeekDefinitionAsync(DTE2 dte, string? filePath, string? documentQuery, int? line, int? column)
    {
        JObject sourceLocation = await PositionTextSelectionAsync(dte, filePath, documentQuery, line, column, selectWord: true)
            .ConfigureAwait(false);

        string sourcePath = (string?)sourceLocation[ResolvedPathProperty] ?? string.Empty;
        int sourceLine = (int?)sourceLocation["line"] ?? 0;
        int sourceColumn = (int?)sourceLocation["column"] ?? column ?? 1;
        string word = ((string?)sourceLocation[SelectedTextProperty] ?? string.Empty).Trim();
        sourceLocation[SelectedTextProperty] = word;

        if (!HasNavigableSymbolText(word))
        {
            return new JObject
            {
                ["word"] = word,
                [SourceLocationProperty] = sourceLocation,
                [DefinitionFoundProperty] = false,
                [DefinitionLocationProperty] = JValue.CreateNull(),
                ["definitionSource"] = JValue.CreateNull(),
                ["definitionContext"] = JValue.CreateNull(),
                ["note"] = "No symbol was found under the caret.",
            };
        }

        bool definitionFound = false;
        JObject? definitionLocation = null;
        JObject? definitionSource = null;
        string? note = null;

        try
        {
            JObject definitionResult = await GoToDefinitionAsync(dte, sourcePath, null, sourceLine, sourceColumn)
                .ConfigureAwait(false);
            definitionFound = (bool?)definitionResult[DefinitionFoundProperty] == true;
            definitionLocation = definitionResult[DefinitionLocationProperty] as JObject;

            if (definitionFound && definitionLocation is not null)
            {
                (definitionSource, note) = await BuildPeekDefinitionSourceAsync(dte, definitionLocation).ConfigureAwait(false);
            }
        }
        finally
        {
            await TryRestoreLocationAsync(dte, sourcePath, sourceLine, sourceColumn).ConfigureAwait(false);
        }

        JObject result = new()
        {
            ["word"] = word,
            [SourceLocationProperty] = sourceLocation,
            [DefinitionFoundProperty] = definitionFound,
            [DefinitionLocationProperty] = definitionLocation ?? (JToken)JValue.CreateNull(),
            ["definitionSource"] = definitionSource ?? (JToken)JValue.CreateNull(),
            ["definitionContext"] = definitionSource ?? (JToken)JValue.CreateNull(),
        };

        if (!string.IsNullOrWhiteSpace(note))
        {
            result["note"] = note;
        }

        return result;
    }

    private async Task TryRestoreLocationAsync(DTE2 dte, string? filePath, int? line, int? column)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !line.HasValue || line.Value <= 0)
        {
            return;
        }

        try
        {
            await PositionTextSelectionAsync(dte, filePath, null, line, column, selectWord: false).ConfigureAwait(true);
        }
        catch (CommandErrorException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        catch (InvalidOperationException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task<(JObject? Slice, string? Note)> BuildPeekDefinitionSourceAsync(DTE2 dte, JObject definitionLocation)
    {
        string definitionPath = (string?)definitionLocation[ResolvedPathProperty] ?? string.Empty;
        int definitionLine = (int?)definitionLocation["line"] ?? 0;
        if (string.IsNullOrWhiteSpace(definitionPath) || definitionLine <= 0)
        {
            return (null, "Definition location was found, but its file path or line number was unavailable.");
        }

        (int startLine, int endLine)? range = await TryResolveDefinitionExtentAsync(dte, definitionPath, definitionLine).ConfigureAwait(false);
        if (range is { } resolvedRange)
        {
            JObject fullSlice = await GetDocumentSliceAsync(
                dte,
                definitionPath,
                resolvedRange.startLine,
                resolvedRange.endLine,
                includeLineNumbers: true,
                revealInEditor: false).ConfigureAwait(false);
            return (fullSlice, null);
        }

        JObject fallbackSlice = await GetDocumentSliceAsync(
            dte,
            definitionPath,
            Math.Max(1, definitionLine - 2),
            definitionLine + 12,
            includeLineNumbers: true,
            revealInEditor: false).ConfigureAwait(false);
        return (fallbackSlice, "Returned surrounding definition context because the full definition extent could not be determined safely.");
    }

    private static async Task<(int startLine, int endLine)?> TryResolveDefinitionExtentAsync(DTE2 dte, string definitionPath, int definitionLine)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string resolvedPath = ResolveDocumentPath(dte, definitionPath, allowDiskFallback: false);
        ProjectItem? projectItem = null;
        try
        {
            projectItem = dte.Solution.FindProjectItem(resolvedPath);
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        if (projectItem?.FileCodeModel?.CodeElements is not CodeElements codeElements)
        {
            return null;
        }

        CodeElement? targetElement = FindDefinitionElement(codeElements, definitionLine);
        if (targetElement is null)
        {
            return null;
        }

        try
        {
            int startLine = targetElement.StartPoint?.Line ?? 0;
            int endLine = targetElement.EndPoint?.Line ?? 0;
            return startLine > 0 && endLine >= startLine ? (startLine, endLine) : null;
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    private static CodeElement? FindDefinitionElement(CodeElements elements, int definitionLine)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        CodeElement? bestMatch = null;
        foreach (CodeElement element in elements)
        {
            CodeElement? candidate = FindDefinitionElement(element, definitionLine);
            if (candidate is not null)
            {
                bestMatch = candidate;
            }
        }

        return bestMatch;
    }

    private static CodeElement? FindDefinitionElement(CodeElement element, int definitionLine)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            int startLine = element.StartPoint?.Line ?? 0;
            int endLine = element.EndPoint?.Line ?? 0;
            if (startLine <= 0 || endLine < startLine || definitionLine < startLine || definitionLine > endLine)
            {
                return null;
            }

            if (TryGetDefinitionChildren(element) is { } children)
            {
                foreach (CodeElement child in children)
                {
                    CodeElement? nested = FindDefinitionElement(child, definitionLine);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
            }

            return element;
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    private static CodeElements? TryGetDefinitionChildren(CodeElement element)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return element switch
            {
                CodeNamespace ns => ns.Members,
                CodeClass cls => cls.Members,
                CodeStruct st => st.Members,
                CodeInterface iface => iface.Members,
                _ => null,
            };
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    // ── GoTo helpers ──────────────────────────────────────────────────────────

    private static (string Text, JArray Lines) BuildDocumentSliceContent(
        List<string> lines,
        int actualStart,
        int actualEnd,
        bool includeLineNumbers)
    {
        JArray sliceLines = [];
        StringBuilder builder = new();
        for (int lineNumber = actualStart; lineNumber <= actualEnd && lineNumber <= lines.Count; lineNumber++)
        {
            string lineText = lines[lineNumber - 1];
            sliceLines.Add(new JObject
            {
                ["line"] = lineNumber,
                ["text"] = lineText,
            });

            if (builder.Length > 0) builder.Append('\n');
            if (includeLineNumbers)
            {
                builder.Append(lineNumber);
                builder.Append(": ");
            }

            builder.Append(lineText);
        }

        return (builder.ToString(), sliceLines);
    }

    // Use IVsUIShell.PostExecCommand with the standard cmdidGotoDefn
    // rather than dte.ExecuteCommand("Edit.GoToDefinition", ...).
    // ExecuteCommand shows a modal "Command requires one argument" dialog
    // for this command. PostExecCommand posts through the shell command
    // dispatcher — the same path as pressing F12.
    //
    // guidStandardCommandSet97 = {5efc7975-14bc-11cf-9b2b-00aa00573819}
    // cmdidGotoDefn             = 935   (from stdidcmd.h)
    private static async Task PostGoToDefinitionCommandAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        PostGoToDefinitionOnMainThread();
        // PostExecCommand is asynchronous — give VS a moment to navigate.
        await Task.Delay(500).ConfigureAwait(false);
    }

    private static void PostGoToDefinitionOnMainThread()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            Guid goToDefnGuid = new("{5efc7975-14bc-11cf-9b2b-00aa00573819}");
            const uint GoToDefnId = 935;
            object? arg = null;
            IVsUIShell shell = (IVsUIShell)Package.GetGlobalService(typeof(SVsUIShell))
                ?? throw new CommandErrorException(UnsupportedOperationCode, "IVsUIShell service not available.");
            shell.PostExecCommand(ref goToDefnGuid, GoToDefnId, 0, ref arg);
        }
        catch (CommandErrorException)
        {
            throw;
        }
        catch (COMException ex)
        {
            throw new CommandErrorException(
                UnsupportedOperationCode,
                $"GoToDefinition failed: {ex.Message}");
        }
    }

    private static JObject? ReadDefinitionLocation(Document? activeDoc)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (activeDoc is null)
        {
            return null;
        }

        string definitionPath = PathNormalization.NormalizeFilePath(activeDoc.FullName);
        int definitionLine = 0, definitionColumn = 0;
        string definitionSelectedText = string.Empty, lineText = string.Empty;

        if (activeDoc.Object(TextDocumentKind) is TextDocument defTextDoc)
        {
            TextSelection selection = defTextDoc.Selection;
            definitionLine = selection.ActivePoint.Line;
            definitionColumn = selection.ActivePoint.DisplayColumn;
            definitionSelectedText = selection.Text ?? string.Empty;
            lineText = GetLineText(defTextDoc, definitionLine);
        }

        return new JObject
        {
            [ResolvedPathProperty] = definitionPath,
            ["name"] = activeDoc.Name ?? string.Empty,
            ["line"] = definitionLine,
            ["column"] = definitionColumn,
            [SelectedTextProperty] = definitionSelectedText,
            ["lineText"] = lineText,
        };
    }

    private async Task<(bool Found, JObject? Location, JObject? Slice)> FetchDefinitionSliceAsync(
        DTE2 dte,
        string sourcePath,
        int sourceLine,
        int? sourceColumn,
        int contextLines)
    {
        JObject defResult = await GoToDefinitionAsync(dte, sourcePath, null, sourceLine, sourceColumn)
            .ConfigureAwait(true);

        bool definitionFound = (bool?)defResult[DefinitionFoundProperty] == true;
        JObject? defLocation = defResult[DefinitionLocationProperty] as JObject;
        JObject? definitionSlice = null;

        if (definitionFound && defLocation is not null)
        {
            string? defPath = (string?)defLocation[ResolvedPathProperty];
            int defLine = (int?)defLocation["line"] ?? 0;
            if (!string.IsNullOrEmpty(defPath) && defLine > 0)
            {
                definitionSlice = await GetDocumentSliceAsync(
                    dte,
                    defPath,
                    Math.Max(1, defLine - 2),
                    defLine + contextLines,
                    includeLineNumbers: true).ConfigureAwait(true);
            }
        }

        return (definitionFound, defLocation, definitionSlice);
    }

    private static (string? File, int StartLine, int EndLine) ParseSliceRangeArgs(JObject range)
    {
        string? file = range["file"]?.Value<string>();
        int line = range["line"]?.Value<int?>() ?? 1;
        int before = range["contextBefore"]?.Value<int?>() ?? range["context-before"]?.Value<int?>() ?? 0;
        int after = range["contextAfter"]?.Value<int?>() ?? range["context-after"]?.Value<int?>() ?? 0;
        int startLine = range["startLine"]?.Value<int?>()
            ?? range["start-line"]?.Value<int?>()
            ?? range["start_line"]?.Value<int?>()
            ?? Math.Max(1, line - before);
        int endLine = range["endLine"]?.Value<int?>()
            ?? range["end-line"]?.Value<int?>()
            ?? range["end_line"]?.Value<int?>()
            ?? line + after;
        return (file, startLine, endLine);
    }
}
