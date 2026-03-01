using System;
using System.IO;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class DocumentService
{
    public async Task<JObject> OpenDocumentAsync(DTE2 dte, string filePath, int line, int column)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        if (!File.Exists(normalizedPath))
        {
            throw new CommandErrorException("document_not_found", $"File not found: {normalizedPath}");
        }

        var window = dte.ItemOperations.OpenFile(normalizedPath);
        window.Activate();

        if (window.Document?.Object("TextDocument") is TextDocument textDocument)
        {
            var selection = textDocument.Selection;
            selection.MoveToLineAndOffset(Math.Max(1, line), Math.Max(1, column), false);
        }

        return new JObject
        {
            ["resolvedPath"] = normalizedPath,
            ["line"] = Math.Max(1, line),
            ["column"] = Math.Max(1, column),
            ["windowCaption"] = window.Caption,
        };
    }

    public async Task<(string Path, string Text)> GetActiveDocumentTextAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var document = dte.ActiveDocument;
        if (document is null)
        {
            throw new CommandErrorException("document_not_found", "There is no active document.");
        }

        if (document.Object("TextDocument") is not TextDocument textDocument)
        {
            throw new CommandErrorException("unsupported_operation", $"Active document is not a text document: {document.FullName}");
        }

        var editPoint = textDocument.StartPoint.CreateEditPoint();
        return (document.FullName, editPoint.GetText(textDocument.EndPoint));
    }
}
