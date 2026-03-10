using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class DocumentService(IServiceProvider serviceProvider)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private const string DocumentNotFoundCode = "document_not_found";
    private const string ResolvedPathProperty = "resolvedPath";
    private const string TextDocumentKind = "TextDocument";
    private const string UnsupportedOperationCode = "unsupported_operation";

    public async Task<JObject> ListOpenTabsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var activePath = TryGetDocumentFullName(dte.ActiveDocument);
        var documents = EnumerateOpenDocuments(dte);
        var items = new JArray();
        for (var i = 0; i < documents.Count; i++)
        {
            items.Add(CreateDocumentInfo(documents[i], activePath, i + 1));
        }

        return new JObject
        {
            ["count"] = items.Count,
            ["activePath"] = string.IsNullOrWhiteSpace(activePath) ? string.Empty : PathNormalization.NormalizeFilePath(activePath),
            ["items"] = items,
        };
    }

    public async Task<JObject> ListOpenDocumentsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var activePath = TryGetDocumentFullName(dte.ActiveDocument);
        var items = new JArray(
            EnumerateOpenDocuments(dte)
                .Select((document, index) => CreateDocumentInfo(document, activePath, index + 1)));

        return new JObject
        {
            ["count"] = items.Count,
            ["items"] = items,
        };
    }

    public async Task<JObject> OpenDocumentAsync(DTE2 dte, string filePath, int line, int column, bool allowDiskFallback = true)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var normalizedPath = ResolveDocumentPath(dte, filePath, allowDiskFallback);
        if (!File.Exists(normalizedPath))
        {
            normalizedPath = TryFindExistingOpenDocumentPathByFileName(dte, normalizedPath) ?? normalizedPath;
            if (!File.Exists(normalizedPath))
            {
                throw new CommandErrorException(
                    DocumentNotFoundCode,
                    $"File does not exist on disk: {normalizedPath}");
            }
        }

        var window = dte.ItemOperations.OpenFile(normalizedPath);
        window.Activate();

        var navigated = false;
        if (window.Document?.Object(TextDocumentKind) is TextDocument textDocument)
        {
            navigated = TryNavigateToLine(textDocument, line, column);
        }

        return new JObject
        {
            [ResolvedPathProperty] = normalizedPath,
            ["name"] = Path.GetFileName(normalizedPath),
            ["line"] = Math.Max(1, line),
            ["column"] = Math.Max(1, column),
            ["navigated"] = navigated,
            ["windowCaption"] = window.Caption,
        };
    }

    public async Task<JObject> RevealDocumentRangeAsync(
        DTE2 dte,
        string filePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        if (!File.Exists(normalizedPath))
        {
            throw new CommandErrorException(DocumentNotFoundCode, $"File not found: {normalizedPath}");
        }

        var window = dte.ItemOperations.OpenFile(normalizedPath);
        window.Activate();

        var navigated = false;
        if (window.Document?.Object(TextDocumentKind) is TextDocument textDocument)
        {
            try
            {
                var selection = textDocument.Selection;
                SelectRange(
                    textDocument,
                    selection,
                    Math.Max(1, startLine),
                    Math.Max(1, startColumn),
                    Math.Max(1, endLine),
                    Math.Max(1, endColumn));
                TryShowActivePoint(selection);
                navigated = true;
            }
            catch (ArgumentException)
            {
                navigated = TryNavigateToLine(textDocument, startLine, startColumn);
            }
            catch (COMException)
            {
                navigated = TryNavigateToLine(textDocument, startLine, startColumn);
            }
        }

        return new JObject
        {
            [ResolvedPathProperty] = normalizedPath,
            ["name"] = Path.GetFileName(normalizedPath),
            ["startLine"] = Math.Max(1, startLine),
            ["startColumn"] = Math.Max(1, startColumn),
            ["endLine"] = Math.Max(1, endLine),
            ["endColumn"] = Math.Max(1, endColumn),
            ["navigated"] = navigated,
            ["windowCaption"] = window.Caption,
        };
    }

    public async Task<JObject> WriteDocumentTextAsync(
        DTE2 dte,
        string filePath,
        string content,
        int line,
        int column,
        bool saveChanges,
        IReadOnlyCollection<(int StartLine, int EndLine)>? changedRanges = null,
        IReadOnlyCollection<int>? deletedLines = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        var directory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(normalizedPath))
        {
            File.WriteAllText(normalizedPath, string.Empty);
        }

        var window = dte.ItemOperations.OpenFile(normalizedPath);
        window.Activate();

        var document = window.Document ?? dte.ActiveDocument
            ?? throw new CommandErrorException(DocumentNotFoundCode, $"Unable to activate: {normalizedPath}");

        var originalContent = TryReadDocumentText(document) ?? ReadFileText(normalizedPath);
        var usedEditorBuffer = false;
        try
        {
            if (document.Object(TextDocumentKind) is TextDocument textDocument)
            {
                var start = textDocument.StartPoint.CreateEditPoint();
                start.ReplaceText(textDocument.EndPoint, content, 0);

                var selection = textDocument.Selection;
                selection.MoveToLineAndOffset(Math.Max(1, line), Math.Max(1, column), false);
                TryShowActivePoint(selection);
                usedEditorBuffer = string.Equals(TryReadDocumentText(document), content, StringComparison.Ordinal);
            }
        }
        catch (COMException)
        {
            usedEditorBuffer = false;
        }

        if (!usedEditorBuffer)
        {
            File.WriteAllText(normalizedPath, content);
            if (window.Document is not null)
            {
                window.Document.Close(vsSaveChanges.vsSaveChangesNo);
                window = dte.ItemOperations.OpenFile(normalizedPath);
                window.Activate();
            }
        }

        var finalDocument = window.Document ?? document;
        var finalContent = TryReadDocumentText(finalDocument) ?? ReadFileText(normalizedPath);
        if (!string.Equals(finalContent, content, StringComparison.Ordinal))
        {
            throw new CommandErrorException("write_failed", $"Document content did not match the requested text after write: {normalizedPath}");
        }

        var contentChanged = !string.Equals(originalContent, finalContent, StringComparison.Ordinal);

        if (saveChanges && window.Document is not null)
        {
            window.Document.Save();
        }

        if ((changedRanges?.Count > 0) || (deletedLines?.Count > 0))
        {
            var view = TryGetActiveWpfTextView();
            if (view is not null)
            {
                BridgeEditHighlightService.Instance.ApplyHighlights(view, changedRanges ?? [], deletedLines ?? []);
            }
        }

        return new JObject
        {
            [ResolvedPathProperty] = normalizedPath,
            ["editorBacked"] = usedEditorBuffer,
            ["verified"] = true,
            ["contentChanged"] = contentChanged,
            ["saved"] = window.Document?.Saved ?? saveChanges,
            ["line"] = Math.Max(1, line),
            ["column"] = Math.Max(1, column),
            ["windowCaption"] = window.Caption,
        };
    }
    private static string ReadFileText(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string? TryReadDocumentText(Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (document is null)
        {
            return null;
        }

        try
        {
            if (document.Object(TextDocumentKind) is not TextDocument textDocument)
            {
                return null;
            }

            var start = textDocument.StartPoint.CreateEditPoint();
            return start.GetText(textDocument.EndPoint);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private IWpfTextView? TryGetActiveWpfTextView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_serviceProvider.GetService(typeof(SVsTextManager)) is not IVsTextManager textManager)
        {
            return null;
        }

        if (textManager.GetActiveView(1, null, out var activeView) != 0 || activeView is null)
        {
            return null;
        }

        var componentModel = _serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
        var adapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
        return adapters?.GetWpfTextView(activeView);
    }

    public async Task<JObject> PositionTextSelectionAsync(
        DTE2 dte,
        string? filePath,
        string? documentQuery,
        int? line,
        int? column,
        bool selectWord)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!string.IsNullOrWhiteSpace(filePath) && !string.IsNullOrWhiteSpace(documentQuery))
        {
            throw new CommandErrorException("invalid_arguments", "Use either --file or --document, not both.");
        }

        Document document;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
            if (!File.Exists(normalizedPath))
            {
                throw new CommandErrorException(DocumentNotFoundCode, $"File not found: {normalizedPath}");
            }

            var window = dte.ItemOperations.OpenFile(normalizedPath);
            window.Activate();
            document = window.Document ?? dte.ActiveDocument ?? throw new CommandErrorException(DocumentNotFoundCode, $"Unable to activate: {normalizedPath}");
        }
        else if (!string.IsNullOrWhiteSpace(documentQuery))
        {
            var match = ResolveDocumentMatches(dte, documentQuery, fallbackToActive: false, allowMultiple: false);
            document = match.Documents[0];
            document.Activate();
        }
        else
        {
            document = dte.ActiveDocument ?? throw new CommandErrorException(DocumentNotFoundCode, "There is no active document.");
            document.Activate();
        }

        if (document.Object(TextDocumentKind) is not TextDocument textDocument)
        {
            throw new CommandErrorException(UnsupportedOperationCode, $"Document is not a text document: {document.FullName}");
        }

        var selection = textDocument.Selection;
        var targetLine = Math.Max(1, line ?? selection.ActivePoint.Line);
        var targetColumn = Math.Max(1, column ?? selection.ActivePoint.DisplayColumn);
        selection.MoveToLineAndOffset(targetLine, targetColumn, false);
        TryShowActivePoint(selection);
        if (selectWord)
        {
            TrySelectCurrentWord(selection);
            TryShowActivePoint(selection);
        }

        var activeLine = selection.ActivePoint.Line;
        var activeColumn = selection.ActivePoint.DisplayColumn;
        return new JObject
        {
            [ResolvedPathProperty] = PathNormalization.NormalizeFilePath(document.FullName),
            ["name"] = document.Name,
            ["line"] = activeLine,
            ["column"] = activeColumn,
            ["selectedText"] = selection.Text,
            ["lineText"] = GetLineText(textDocument, activeLine),
        };
    }

    public async Task<JObject> ActivateOpenDocumentAsync(DTE2 dte, string query)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var match = ResolveDocumentMatches(dte, query, fallbackToActive: false, allowMultiple: false);
        match.Documents[0].Activate();

        return new JObject
        {
            ["query"] = query,
            ["matchedBy"] = match.MatchedBy,
            ["document"] = CreateDocumentInfo(match.Documents[0], match.Documents[0].FullName),
        };
    }

    public async Task<JObject> CloseOpenDocumentsAsync(DTE2 dte, string? query, bool closeAllMatches, bool saveChanges)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var activePath = TryGetDocumentFullName(dte.ActiveDocument);
        var match = ResolveDocumentMatches(dte, query, fallbackToActive: true, allowMultiple: closeAllMatches);
        var closed = new JArray();
        foreach (var document in match.Documents)
        {
            var info = CreateDocumentInfo(document, activePath);
            document.Close(saveChanges ? vsSaveChanges.vsSaveChangesYes : vsSaveChanges.vsSaveChangesNo);
            closed.Add(info);
        }

        return new JObject
        {
            ["query"] = query ?? string.Empty,
            ["matchedBy"] = match.MatchedBy,
            ["saveChanges"] = saveChanges,
            ["count"] = closed.Count,
            ["items"] = closed,
        };
    }

    public async Task<JObject> SaveDocumentAsync(DTE2 dte, string? filePath, bool saveAll)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (saveAll)
        {
            dte.Documents.SaveAll();
            return new JObject
            {
                ["saveAll"] = true,
                ["count"] = dte.Documents.Count,
            };
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalized = PathNormalization.NormalizeFilePath(filePath);
            var document = TryFindOpenDocumentByPath(dte, normalized) ?? throw new CommandErrorException(DocumentNotFoundCode, $"No open document matching path: {filePath}");
            document.Save();
            return new JObject
            {
                ["saveAll"] = false,
                ["count"] = 1,
                ["path"] = TryGetDocumentFullName(document),
                ["saved"] = TryGetDocumentSaved(document),
            };
        }

        var active = dte.ActiveDocument ?? throw new CommandErrorException("no_active_document", "No active document to save.");
        active.Save();
        return new JObject
        {
            ["saveAll"] = false,
            ["count"] = 1,
            ["path"] = TryGetDocumentFullName(active),
            ["saved"] = TryGetDocumentSaved(active),
        };
    }

    public async Task<JObject> CloseFileAsync(DTE2 dte, string? filePath, string? query, bool saveChanges)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string resolvedQuery;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            resolvedQuery = PathNormalization.NormalizeFilePath(filePath);
        }
        else if (!string.IsNullOrWhiteSpace(query))
        {
            resolvedQuery = query!;
        }
        else
        {
            throw new CommandErrorException("invalid_arguments", "Specify --file or --query.");
        }

        return await CloseOpenDocumentsAsync(dte, resolvedQuery, closeAllMatches: false, saveChanges).ConfigureAwait(true);
    }

    public async Task<JObject> CloseAllExceptCurrentAsync(DTE2 dte, bool saveChanges)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var activeDocument = dte.ActiveDocument ?? throw new CommandErrorException(DocumentNotFoundCode, "There is no active document.");
        var activePath = TryGetDocumentFullName(activeDocument);
        if (string.IsNullOrWhiteSpace(activePath))
        {
            throw new CommandErrorException(DocumentNotFoundCode, "The active document does not have a file path.");
        }

        var documentsToClose = EnumerateOpenDocuments(dte)
            .Where(document => !string.Equals(
                TryGetDocumentFullName(document),
                activePath,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        var closed = new JArray();
        foreach (var document in documentsToClose)
        {
            closed.Add(CreateDocumentInfo(document, activePath));
            document.Close(saveChanges ? vsSaveChanges.vsSaveChangesYes : vsSaveChanges.vsSaveChangesNo);
        }

        return new JObject
        {
            ["activePath"] = PathNormalization.NormalizeFilePath(activePath),
            ["saveChanges"] = saveChanges,
            ["count"] = closed.Count,
            ["items"] = closed,
        };
    }

    public async Task<(string Path, string Text)> GetActiveDocumentTextAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var document = dte.ActiveDocument ?? throw new CommandErrorException(DocumentNotFoundCode, "There is no active document.");
        if (document.Object(TextDocumentKind) is not TextDocument textDocument)
        {
            throw new CommandErrorException(UnsupportedOperationCode, $"Active document is not a text document: {document.FullName}");
        }

        var editPoint = textDocument.StartPoint.CreateEditPoint();
        return (document.FullName, editPoint.GetText(textDocument.EndPoint));
    }

    public async Task<JObject> GetDocumentSliceAsync(
        DTE2 dte,
        string? filePath,
        int startLine,
        int endLine,
        bool includeLineNumbers,
        bool revealInEditor = true)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var resolvedPath = ResolveDocumentPath(dte, filePath);
        var text = ReadDocumentText(dte, resolvedPath);
        var lines = SplitLines(text);

        var clampedStart = Math.Max(1, startLine);
        var clampedEnd = Math.Max(clampedStart, endLine);
        var actualStart = Math.Min(clampedStart, Math.Max(1, lines.Count));
        var actualEnd = Math.Min(clampedEnd, Math.Max(1, lines.Count));

        if (revealInEditor)
        {
            _ = await OpenDocumentAsync(dte, resolvedPath, actualStart, 1, allowDiskFallback: false).ConfigureAwait(true);
        }

        var sliceLines = new JArray();
        var builder = new System.Text.StringBuilder();
        for (var lineNumber = actualStart; lineNumber <= actualEnd && lineNumber <= lines.Count; lineNumber++)
        {
            var lineText = lines[lineNumber - 1];
            sliceLines.Add(new JObject
            {
                ["line"] = lineNumber,
                ["text"] = lineText,
            });

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            if (includeLineNumbers)
            {
                builder.Append(lineNumber);
                builder.Append(": ");
            }

            builder.Append(lineText);
        }

        return new JObject
        {
            [ResolvedPathProperty] = resolvedPath,
            ["requestedStartLine"] = clampedStart,
            ["requestedEndLine"] = clampedEnd,
            ["actualStartLine"] = actualStart,
            ["actualEndLine"] = actualEnd,
            ["lineCount"] = lines.Count,
            ["text"] = builder.ToString(),
            ["lines"] = sliceLines,
        };
    }

    public async Task<JObject> GoToDefinitionAsync(
        DTE2 dte,
        string? filePath,
        string? documentQuery,
        int? line,
        int? column)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var sourceLocation = await PositionTextSelectionAsync(dte, filePath, documentQuery, line, column, selectWord: false)
            .ConfigureAwait(true);

        // Use IVsUIShell.PostExecCommand with the standard cmdidGotoDefn
        // rather than dte.ExecuteCommand("Edit.GoToDefinition", ...).
        // ExecuteCommand shows a modal "Command requires one argument" dialog
        // for this command.  PostExecCommand posts through the shell command
        // dispatcher ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â the same path as pressing F12.
        //
        // guidStandardCommandSet97 = {5efc7975-14bc-11cf-9b2b-00aa00573819}
        // cmdidGotoDefn             = 935   (from stdidcmd.h)
        try
        {
            var goToDefnGuid = new Guid("{5efc7975-14bc-11cf-9b2b-00aa00573819}");
            const uint goToDefnId = 935;
            object? arg = null;
            var shell = (Microsoft.VisualStudio.Shell.Interop.IVsUIShell)
                Microsoft.VisualStudio.Shell.Package.GetGlobalService(
                    typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShell)) ?? throw new CommandErrorException(UnsupportedOperationCode, "IVsUIShell service not available.");
            shell.PostExecCommand(ref goToDefnGuid, goToDefnId, 0, ref arg);
            // PostExecCommand is asynchronous ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â give VS a moment to navigate.
            await Task.Delay(500).ConfigureAwait(true);
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

        var activeDoc = dte.ActiveDocument;
        if (activeDoc is null)
        {
            return new JObject
            {
                ["sourceLocation"] = sourceLocation,
                ["definitionLocation"] = null,
                ["definitionFound"] = false,
            };
        }

        var definitionPath = PathNormalization.NormalizeFilePath(activeDoc.FullName);
        int definitionLine = 0, definitionColumn = 0;
        string selectedText = string.Empty, lineText = string.Empty;

        if (activeDoc.Object(TextDocumentKind) is TextDocument defTextDoc)
        {
            var selection = defTextDoc.Selection;
            definitionLine = selection.ActivePoint.Line;
            definitionColumn = selection.ActivePoint.DisplayColumn;
            selectedText = selection.Text ?? string.Empty;
            lineText = GetLineText(defTextDoc, definitionLine);
        }

        var definitionLocation = new JObject
        {
            [ResolvedPathProperty] = definitionPath,
            ["name"] = activeDoc.Name ?? string.Empty,
            ["line"] = definitionLine,
            ["column"] = definitionColumn,
            ["selectedText"] = selectedText,
            ["lineText"] = lineText,
        };

        var sourcePath = (string?)sourceLocation[ResolvedPathProperty] ?? string.Empty;
        var sourceLine = (int?)sourceLocation["line"] ?? 0;
        var definitionFound = !string.Equals(sourcePath, definitionPath, StringComparison.OrdinalIgnoreCase)
            || sourceLine != definitionLine;

        return new JObject
        {
            ["sourceLocation"] = sourceLocation,
            ["definitionLocation"] = definitionLocation,
            ["definitionFound"] = definitionFound,
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

        var sourceLocation = await PositionTextSelectionAsync(dte, filePath, documentQuery, line, column, selectWord: true)
            .ConfigureAwait(true);

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

        var implementationLocation = await PositionTextSelectionAsync(dte, null, null, null, null, selectWord: false)
            .ConfigureAwait(true);
        var sourcePath = (string?)sourceLocation[ResolvedPathProperty] ?? string.Empty;
        var sourceLine = (int?)sourceLocation["line"] ?? 0;
        var implementationPath = (string?)implementationLocation[ResolvedPathProperty] ?? string.Empty;
        var implementationLine = (int?)implementationLocation["line"] ?? 0;
        var implementationFound = !string.Equals(sourcePath, implementationPath, StringComparison.OrdinalIgnoreCase)
            || sourceLine != implementationLine;

        return new JObject
        {
            ["sourceLocation"] = sourceLocation,
            ["implementationLocation"] = implementationLocation,
            ["implementationFound"] = implementationFound,
        };
    }

    public async Task<JObject> GetDocumentSlicesAsync(DTE2 dte, JArray ranges)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var results = new JArray();
        foreach (var rangeToken in ranges)
        {
            if (rangeToken is not Newtonsoft.Json.Linq.JObject range) continue;
            var file = range["file"]?.Value<string>();
            var line = range["line"]?.Value<int>() ?? 1;
            var before = range["contextBefore"]?.Value<int>()
                ?? range["context-before"]?.Value<int>()
                ?? 0;
            var after = range["contextAfter"]?.Value<int>()
                ?? range["context-after"]?.Value<int>()
                ?? 0;
            var startLine = range["startLine"]?.Value<int>()
                ?? range["start-line"]?.Value<int>()
                ?? Math.Max(1, line - before);
            var endLine = range["endLine"]?.Value<int>()
                ?? range["end-line"]?.Value<int>()
                ?? line + after;

            try
            {
                var slice = await GetDocumentSliceAsync(dte, file, startLine, endLine, includeLineNumbers: true)
                    .ConfigureAwait(true);
                results.Add(slice);
            }
            catch (Exception ex)
            {
                results.Add(new Newtonsoft.Json.Linq.JObject
                {
                    [ResolvedPathProperty] = file ?? string.Empty,
                    ["error"] = ex.Message,
                });
            }
        }

        return new Newtonsoft.Json.Linq.JObject
        {
            ["count"] = results.Count,
            ["slices"] = results,
        };
    }

    public async Task<JObject> GetQuickInfoAsync(DTE2 dte, string? filePath, string? documentQuery, int? line, int? column, int contextLines)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Position cursor and get source context
        var sourceLocation = await PositionTextSelectionAsync(dte, filePath, documentQuery, line, column, selectWord: true)
            .ConfigureAwait(true);

        var sourcePath = (string?)sourceLocation[ResolvedPathProperty] ?? string.Empty;
        var sourceLine = (int?)sourceLocation["line"] ?? 0;
        var word = (string?)sourceLocation["selectedText"] ?? string.Empty;

        JObject? defLocation = null;
        JObject? definitionSlice = null;
        var definitionFound = false;
        try
        {
            var defResult = await GoToDefinitionAsync(dte, sourcePath, null, sourceLine, (int?)sourceLocation["column"])
                .ConfigureAwait(true);

            definitionFound = (bool?)defResult["definitionFound"] == true;
            defLocation = defResult["definitionLocation"] as JObject;

            if (definitionFound && defLocation is not null)
            {
                var defPath = (string?)defLocation[ResolvedPathProperty];
                var defLine = (int?)defLocation["line"] ?? 0;
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
        }
        finally
        {
            await TryRestoreLocationAsync(
                dte,
                sourcePath,
                sourceLine,
                (int?)sourceLocation["column"] ?? column ?? 1).ConfigureAwait(true);
        }

        return new JObject
        {
            ["word"] = word,
            ["sourceLocation"] = sourceLocation,
            ["definitionFound"] = definitionFound,
            ["definitionLocation"] = defLocation ?? (JToken)JValue.CreateNull(),
            ["definitionContext"] = definitionSlice ?? (JToken)JValue.CreateNull(),
        };
    }

    public async Task<JObject> GetFileOutlineAsync(DTE2 dte, string? filePath, int maxDepth, string? kindFilter = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var resolvedPath = ResolveDocumentPath(dte, filePath);

        ProjectItem? projectItem = null;
        try { projectItem = dte.Solution.FindProjectItem(resolvedPath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

        if (projectItem is null)
        {
            return new JObject
            {
                [ResolvedPathProperty] = resolvedPath,
                ["count"] = 0,
                ["symbols"] = new JArray(),
                ["note"] = "File is not part of any project or code model is unavailable.",
            };
        }

        var symbols = new JArray();
        string? note = null;
        try
        {
            var codeModel = projectItem.FileCodeModel;
            if (codeModel?.CodeElements is not null)
            {
                foreach (CodeElement element in codeModel.CodeElements)
                {
                    try { CollectOutlineSymbols(element, symbols, 0, maxDepth, kindFilter); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                }
            }
            else
            {
                note = "No code model available for this file type.";
            }
        }
        catch (Exception ex)
        {
            note = $"Code model unavailable: {ex.Message}";
        }

        var result = new JObject
        {
            [ResolvedPathProperty] = resolvedPath,
            ["count"] = symbols.Count,
            ["symbols"] = symbols,
        };
        if (note is not null) result["note"] = note;
        return result;
    }

    private static readonly HashSet<vsCMElement> s_outlineKinds =
    [
        vsCMElement.vsCMElementFunction,
        vsCMElement.vsCMElementClass,
        vsCMElement.vsCMElementStruct,
        vsCMElement.vsCMElementEnum,
        vsCMElement.vsCMElementNamespace,
        vsCMElement.vsCMElementInterface,
        vsCMElement.vsCMElementProperty,
        vsCMElement.vsCMElementVariable,
    ];

    private static void CollectOutlineSymbols(CodeElement element, JArray symbols, int depth, int maxDepth, string? kindFilter = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (depth > maxDepth) return;

        vsCMElement kind;
        try { kind = element.Kind; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return; }

        string name = string.Empty;
        int startLine = 0, endLine = 0;
        try { name = element.Name ?? string.Empty; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { startLine = element.StartPoint?.Line ?? 0; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { endLine = element.EndPoint?.Line ?? 0; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

        if (s_outlineKinds.Contains(kind))
        {
            var kindName = NormalizeOutlineKind(kind);
            var matchesFilter = MatchesOutlineKind(kindName, kindFilter);

            if (matchesFilter)
            {
                symbols.Add(new JObject
                {
                    ["name"] = name,
                    ["kind"] = kindName,
                    ["startLine"] = startLine,
                    ["endLine"] = endLine,
                    ["depth"] = depth,
                });
            }
        }

        CodeElements? children = null;
        try
        {
            if (element is CodeNamespace ns) children = ns.Members;
            else if (element is CodeClass cls) children = cls.Members;
            else if (element is CodeStruct st) children = st.Members;
            else if (element is CodeInterface iface) children = iface.Members;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

        if (children is null) return;
        foreach (CodeElement child in children)
        {
            try { CollectOutlineSymbols(child, symbols, depth + 1, maxDepth, kindFilter); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }

    private static string NormalizeOutlineKind(vsCMElement kind)
    {
        return kind switch
        {
            vsCMElement.vsCMElementFunction => "function",
            vsCMElement.vsCMElementClass => "class",
            vsCMElement.vsCMElementStruct => "struct",
            vsCMElement.vsCMElementEnum => "enum",
            vsCMElement.vsCMElementNamespace => "namespace",
            vsCMElement.vsCMElementInterface => "interface",
            vsCMElement.vsCMElementProperty => "member",
            vsCMElement.vsCMElementVariable => "member",
            _ => kind.ToString().Replace("vsCMElement", string.Empty),
        };
    }

    private static bool MatchesOutlineKind(string normalizedKind, string? kindFilter)
    {
        var filter = kindFilter?.Trim();
        if (string.IsNullOrWhiteSpace(filter) ||
            string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return filter!.ToLowerInvariant() switch
        {
            "type" => normalizedKind is "class" or "struct" or "enum" or "interface",
            "member" => normalizedKind is "member" or "function",
            _ => normalizedKind.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0,
        };
    }

    private static string ResolveDocumentPath(DTE2 dte, string? filePath, bool allowDiskFallback = true)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return ResolveRequestedDocumentPath(dte, filePath!, allowDiskFallback);
        }

        if (dte.ActiveDocument is null || string.IsNullOrWhiteSpace(dte.ActiveDocument.FullName))
        {
            throw new CommandErrorException(DocumentNotFoundCode, "There is no active document.");
        }

        return PathNormalization.NormalizeFilePath(dte.ActiveDocument.FullName);
    }

    private static string ResolveRequestedDocumentPath(DTE2 dte, string filePath, bool allowDiskFallback)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!Path.IsPathRooted(filePath) && TryResolveRelativeDocumentPath(dte, filePath) is { } resolvedRelativePath)
        {
            return resolvedRelativePath;
        }

        var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        if (File.Exists(normalizedPath))
        {
            return normalizedPath;
        }

        var allMatches = SolutionFileLocator.FindMatches(dte, filePath)
            .Concat(allowDiskFallback ? SolutionFileLocator.FindDiskMatches(dte, filePath, maxResults: 250) : [])
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Path = group.Key,
                Score = group.Max(item => item.Score),
                group.OrderByDescending(item => item.Score).First().Source,
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path.Length)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allMatches.Length == 0)
        {
            throw new CommandErrorException(
                DocumentNotFoundCode,
                $"File not found: {normalizedPath}. No solution item matched '{filePath}'.");
        }

        var topScore = allMatches[0].Score;
        var topMatches = allMatches
            .Where(item => item.Score == topScore)
            .ToArray();
        var secondScore = allMatches.Length > topMatches.Length ? (int?)allMatches[topMatches.Length].Score : null;
        var clearLead = secondScore is null || topScore - secondScore.Value >= 50;

        if (topMatches.Length == 1 && topScore >= 900 && clearLead)
        {
            return topMatches[0].Path;
        }

        var preview = string.Join(", ", allMatches.Take(5).Select(item => $"{item.Path} ({item.Source}, score={item.Score})"));
        var suffix = allMatches.Length > 5 ? ", ..." : string.Empty;
        throw new CommandErrorException(
            "document_ambiguous",
            $"Multiple solution items matched '{filePath}'. Use find-files to disambiguate. Candidates: {preview}{suffix}");
    }

    private static void TrySelectCurrentWord(TextSelection selection)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var originalLine = selection.ActivePoint.Line;
        var originalColumn = selection.ActivePoint.DisplayColumn;
        selection.WordLeft(false, 1);
        if (selection.ActivePoint.Line != originalLine)
        {
            selection.MoveToLineAndOffset(originalLine, originalColumn, false);
        }

        selection.WordRight(true, 1);
        if (string.IsNullOrWhiteSpace(selection.Text))
        {
            selection.MoveToLineAndOffset(originalLine, originalColumn, false);
        }
    }

    /// <summary>
    /// Navigates to a line/column, clamping to the document range and handling
    /// transient COM errors (e.g. "Class not registered" when the editor buffer
    /// hasn't fully initialized after first open).
    /// </summary>
    private static bool TryNavigateToLine(TextDocument textDocument, int line, int column)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var maxLine = textDocument.EndPoint.Line;
                var clampedLine = Math.Min(Math.Max(line, 1), Math.Max(1, maxLine));
                var clampedColumn = Math.Max(1, column);
                var selection = textDocument.Selection;
                selection.MoveToLineAndOffset(clampedLine, clampedColumn, false);
                TryShowActivePoint(selection);
                return true;
            }
            catch (ArgumentException)
            {
                if (attempt == 0)
                {
                    // Editor buffers can be transiently unavailable immediately after open.
                    System.Threading.Thread.Sleep(100);
                    continue;
                }

                break;
            }
            catch (COMException)
            {
                if (attempt == 0)
                {
                    System.Threading.Thread.Sleep(100);
                    continue;
                }

                break;
            }
        }

        // Final fallback: try line 1 col 1.
        try
        {
            var selection = textDocument.Selection;
            selection.MoveToLineAndOffset(1, 1, false);
            TryShowActivePoint(selection);
        }
        catch
        {
            // Give up on navigation; the file is still open.
        }

        return false;
    }

    private static void TryShowActivePoint(TextSelection selection)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            selection.ActivePoint.TryToShow(vsPaneShowHow.vsPaneShowCentered);
        }
        catch
        {
            // Some editor surfaces may not support viewport repositioning.
        }
    }

    private static void SelectRange(
        TextDocument textDocument,
        TextSelection selection,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var resolvedStartLine = Math.Max(1, startLine);
        var resolvedStartColumn = Math.Max(1, startColumn);
        var resolvedEndLine = Math.Max(resolvedStartLine, endLine);
        var resolvedEndColumn = Math.Max(1, endColumn);

        selection.MoveToLineAndOffset(resolvedStartLine, resolvedStartColumn, false);

        if (resolvedEndLine <= resolvedStartLine)
        {
            selection.EndOfLine(true);
            return;
        }

        var documentEndLine = textDocument.EndPoint.Line;
        if (resolvedEndLine >= documentEndLine)
        {
            selection.EndOfDocument(true);
            return;
        }

        selection.MoveToLineAndOffset(resolvedEndLine + 1, resolvedEndColumn, true);
    }

    private static string GetLineText(TextDocument textDocument, int lineNumber)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (lineNumber < 1)
        {
            return string.Empty;
        }

        var start = textDocument.StartPoint.CreateEditPoint();
        start.MoveToLineAndOffset(lineNumber, 1);
        var end = start.CreateEditPoint();
        end.EndOfLine();
        return start.GetText(end);
    }

    private static string? TryResolveRelativeDocumentPath(DTE2 dte, string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var normalizedRelativePath = filePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        foreach (var root in EnumerateDocumentSearchRoots(dte))
        {
            var candidate = PathNormalization.NormalizeFilePath(Path.Combine(root, normalizedRelativePath));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var targetFileName = Path.GetFileName(normalizedRelativePath);
        string? fileNameMatch = null;
        foreach (var document in EnumerateOpenDocuments(dte))
        {
            var documentPath = TryGetDocumentFullName(document);
            if (string.IsNullOrWhiteSpace(documentPath))
            {
                continue;
            }

            var normalizedDocumentPath = PathNormalization.NormalizeFilePath(documentPath);
            var comparableDocumentPath = normalizedDocumentPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (comparableDocumentPath.EndsWith(Path.DirectorySeparatorChar + normalizedRelativePath, StringComparison.OrdinalIgnoreCase) ||
                comparableDocumentPath.EndsWith(normalizedRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedDocumentPath;
            }

            if (fileNameMatch is null && string.Equals(Path.GetFileName(comparableDocumentPath), targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                fileNameMatch = normalizedDocumentPath;
            }
        }

        return fileNameMatch;
    }

    private static IReadOnlyList<string> EnumerateDocumentSearchRoots(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var searchRoots = new List<string>();
        var solutionPath = dte.Solution?.IsOpen == true ? dte.Solution.FullName : string.Empty;
        var current = string.IsNullOrWhiteSpace(solutionPath)
            ? string.Empty
            : Path.GetDirectoryName(PathNormalization.NormalizeFilePath(solutionPath)) ?? string.Empty;

        while (!string.IsNullOrWhiteSpace(current))
        {
            AddDistinctPath(searchRoots, current);
            var parent = Path.GetDirectoryName(current) ?? string.Empty;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return searchRoots;
    }

    private static void AddDistinctPath(List<string> searchRoots, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var normalizedCandidate = PathNormalization.NormalizeFilePath(candidate);
        if (!searchRoots.Contains(normalizedCandidate, StringComparer.OrdinalIgnoreCase))
        {
            searchRoots.Add(normalizedCandidate);
        }
    }

    private static IReadOnlyList<Document> EnumerateOpenDocuments(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return
        [
            .. dte.Documents
                .Cast<Document>()
                .Where(HasDocumentPath),
        ];
    }

    private static (List<Document> Documents, string MatchedBy) ResolveDocumentMatches(
        DTE2 dte,
        string? query,
        bool fallbackToActive,
        bool allowMultiple)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var documents = EnumerateOpenDocuments(dte);
        if (documents.Count == 0)
        {
            throw new CommandErrorException(DocumentNotFoundCode, "There are no open documents.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            if (allowMultiple)
            {
                return (documents.ToList(), "all");
            }

            if (!fallbackToActive || dte.ActiveDocument is null)
            {
                throw new CommandErrorException("invalid_arguments", "Missing document query.");
            }

            return (new List<Document> { dte.ActiveDocument }, "active");
        }

        var rawQuery = query!.Trim();
        var queryLooksLikePath = rawQuery.IndexOfAny(['\\', '/', ':']) >= 0;
        if (queryLooksLikePath)
        {
            var normalizedQueryPath = PathNormalization.NormalizeFilePath(rawQuery);
            var exactPath = documents.Where(document => MatchesDocumentExactPath(document, normalizedQueryPath))
                .ToList();
            if (exactPath.Count > 0)
            {
                return FinalizeMatches(exactPath, allowMultiple, "path");
            }
        }

        var exactName = documents.Where(document => MatchesDocumentExactName(document, rawQuery))
            .ToList();
        if (exactName.Count > 0)
        {
            exactName = PreferSingleDocumentMatch(dte, exactName);
            return FinalizeMatches(exactName, allowMultiple, "filename");
        }

        var containsName = documents.Where(document => MatchesDocumentNameContains(document, rawQuery))
            .ToList();
        if (containsName.Count > 0)
        {
            containsName = PreferSingleDocumentMatch(dte, containsName);
            return FinalizeMatches(containsName, allowMultiple, "filename-contains");
        }

        var containsPath = documents.Where(document => MatchesDocumentPathContains(document, rawQuery))
            .ToList();
        if (containsPath.Count > 0)
        {
            containsPath = PreferSingleDocumentMatch(dte, containsPath);
            return FinalizeMatches(containsPath, allowMultiple, "path-contains");
        }

        throw new CommandErrorException(DocumentNotFoundCode, $"No open document matched '{rawQuery}'.");
    }

    private static List<Document> PreferSingleDocumentMatch(DTE2 dte, List<Document> matches)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (matches.Count <= 1)
        {
            return matches;
        }

        if (TryGetDocumentFullName(dte.ActiveDocument) is string activeDocumentPath &&
            !string.IsNullOrWhiteSpace(activeDocumentPath))
        {
            var activeMatches = matches.Where(document => MatchesDocumentExactPath(document, activeDocumentPath)).ToList();
            if (activeMatches.Count == 1)
            {
                return activeMatches;
            }
        }

        matches = PreferMatches(matches, IsProjectBackedDocument);
        matches = PreferMatches(matches, document => !IsReviewArtifactDocument(document));
        matches = PreferMatches(matches, document => !IsOutputDocument(document));
        return matches;
    }

    private static List<Document> PreferMatches(List<Document> matches, Func<Document, bool> predicate)
    {
        var preferred = matches.Where(predicate).ToList();
        return preferred.Count == 0 ? matches : preferred;
    }

    private static (List<Document> Documents, string MatchedBy) FinalizeMatches(List<Document> matches, bool allowMultiple, string matchedBy)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!allowMultiple && matches.Count > 1)
        {
            var options = string.Join(", ", GetDistinctDocumentNames(matches));
            throw new CommandErrorException("invalid_arguments", $"Document query is ambiguous. Matches: {options}");
        }

        return allowMultiple ? (matches, matchedBy) : ([matches[0]], matchedBy);
    }

    private static string ReadDocumentText(DTE2 dte, string resolvedPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var openDocument = TryFindOpenDocumentByPath(dte, resolvedPath);

        var editorText = TryReadDocumentText(openDocument);

        return editorText ?? File.ReadAllText(resolvedPath);
    }

    private static bool HasDocumentPath(Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return !string.IsNullOrWhiteSpace(TryGetDocumentFullName(document));
    }

    private static bool MatchesDocumentExactPath(Document document, string normalizedQueryPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var fullName = TryGetDocumentFullName(document);
        return !string.IsNullOrWhiteSpace(fullName) &&
            string.Equals(fullName, normalizedQueryPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDocumentExactName(Document document, string rawQuery)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var name = TryGetDocumentName(document);
        var fullName = TryGetDocumentFullName(document);
        var fileName = string.IsNullOrWhiteSpace(fullName) ? string.Empty : Path.GetFileName(fullName);
        return string.Equals(name, rawQuery, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, rawQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDocumentNameContains(Document document, string rawQuery)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var name = TryGetDocumentName(document);
        var fullName = TryGetDocumentFullName(document);
        var fileName = string.IsNullOrWhiteSpace(fullName) ? null : Path.GetFileName(fullName);
        return ContainsOrdinalIgnoreCase(name, rawQuery) ||
            ContainsOrdinalIgnoreCase(fileName, rawQuery);
    }

    private static bool MatchesDocumentPathContains(Document document, string rawQuery)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ContainsOrdinalIgnoreCase(TryGetDocumentFullName(document), rawQuery);
    }

    private static IReadOnlyList<string> GetDistinctDocumentNames(IEnumerable<Document> matches)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return
        [
            .. matches
                .Select(GetDocumentLabel)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static string GetDocumentLabel(Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var fullName = TryGetDocumentFullName(document) ?? string.Empty;
        var project = TryGetDocumentProjectUniqueName(document);
        if (!string.IsNullOrWhiteSpace(fullName) && !string.IsNullOrWhiteSpace(project))
        {
            return $"{Path.GetFileName(fullName)} [{project}]";
        }

        return TryGetDocumentName(document) ?? Path.GetFileName(fullName);
    }

    private static bool IsProjectBackedDocument(Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return !string.IsNullOrWhiteSpace(TryGetDocumentProjectUniqueName(document));
    }

    private static bool IsReviewArtifactDocument(Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var fullName = TryGetDocumentFullName(document) ?? string.Empty;
        return fullName.IndexOf("\\output\\pr-review\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsOutputDocument(Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var fullName = TryGetDocumentFullName(document) ?? string.Empty;
        return fullName.IndexOf("\\output\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Document? TryFindOpenDocumentByPath(DTE2 dte, string resolvedPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (Document document in dte.Documents)
        {
            if (MatchesDocumentExactPath(document, resolvedPath))
            {
                return document;
            }
        }

        return null;
    }


    private static string? TryFindExistingOpenDocumentPathByFileName(DTE2 dte, string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var document in EnumerateOpenDocuments(dte))
        {
            var documentPath = TryGetDocumentFullName(document);
            if (string.IsNullOrWhiteSpace(documentPath) || !File.Exists(documentPath))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(documentPath), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return documentPath;
            }
        }

        return null;
    }

    private static bool ContainsOrdinalIgnoreCase(string? value, string query)
    {
        var candidate = value;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return candidate!.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return [.. normalized.Split('\n')];
    }

    private static JObject CreateDocumentInfo(Document document, string? activePath, int? tabIndex = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var fullName = TryGetDocumentFullName(document) ?? string.Empty;
        var normalizedPath = string.IsNullOrWhiteSpace(fullName) ? string.Empty : PathNormalization.NormalizeFilePath(fullName);
        var normalizedActivePath = string.IsNullOrWhiteSpace(activePath) ? string.Empty : PathNormalization.NormalizeFilePath(activePath);
        var saved = TryGetDocumentSaved(document);

        return new JObject
        {
            ["name"] = TryGetDocumentName(document) ?? Path.GetFileName(fullName),
            ["path"] = normalizedPath,
            ["tabIndex"] = tabIndex,
            ["isActive"] = !string.IsNullOrWhiteSpace(normalizedPath) &&
                string.Equals(normalizedPath, normalizedActivePath, StringComparison.OrdinalIgnoreCase),
            ["project"] = TryGetDocumentProjectUniqueName(document) ?? string.Empty,
            ["isProjectBacked"] = IsProjectBackedDocument(document),
            ["isReviewArtifact"] = IsReviewArtifactDocument(document),
            ["saved"] = (JToken?)saved ?? JValue.CreateNull(),
        };
    }

    private static string? TryGetDocumentFullName(Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (document is null)
        {
            return null;
        }

        try
        {
            var fullName = document.FullName;
            return string.IsNullOrWhiteSpace(fullName) ? null : PathNormalization.NormalizeFilePath(fullName);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? TryGetDocumentName(Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (document is null)
        {
            return null;
        }

        try
        {
            return string.IsNullOrWhiteSpace(document.Name) ? null : document.Name;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? TryGetDocumentProjectUniqueName(Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (document is null)
        {
            return null;
        }

        try
        {
            var project = document.ProjectItem?.ContainingProject?.UniqueName;
            return string.IsNullOrWhiteSpace(project) ? null : project;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool? TryGetDocumentSaved(Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (document is null)
        {
            return null;
        }

        try
        {
            return document.Saved;
        }
        catch (COMException)
        {
            return null;
        }
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}











