using System;
using System.Collections.Generic;
using System.Text;

namespace VsIdeBridge.Infrastructure;

internal static class PipeCommandNames
{
    private static readonly Dictionary<string, string[]> PreferredAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tools.IdeHelp"] = ["help", "catalog"],
        ["Tools.IdeSmokeTest"] = ["smoke-test"],
        ["Tools.IdeGetState"] = ["state"],
        ["Tools.IdeGetUiSettings"] = ["ui-settings"],
        ["Tools.IdeCaptureVsWindow"] = ["capture-vs-window"],
        ["Tools.IdeWaitForReady"] = ["ready"],
        ["Tools.IdeOpenSolution"] = ["open-solution"],
        ["Tools.IdeLaunchVisualStudio"] = ["launch-visual-studio"],
        ["Tools.IdeCreateSolution"] = ["create-solution"],
        ["Tools.IdeCloseIde"] = ["close-ide"],
        ["Tools.IdeBatchCommands"] = ["batch"],
        ["Tools.IdeFindText"] = ["find-text"],
        ["Tools.IdeFindTextBatch"] = ["find-text-batch"],
        ["Tools.IdeFindFiles"] = ["find-files"],
        ["Tools.IdeOpenDocument"] = ["open-document"],
        ["Tools.IdeListDocuments"] = ["list-documents"],
        ["Tools.IdeListOpenTabs"] = ["list-tabs"],
        ["Tools.IdeActivateDocument"] = ["activate-document"],
        ["Tools.IdeCloseDocument"] = ["close-document"],
        ["Tools.IdeSaveDocument"] = ["save-document"],
        ["Tools.IdeCloseFile"] = ["close-file"],
        ["Tools.IdeReloadDocument"] = ["reload-document"],
        ["Tools.IdeCloseAllExceptCurrent"] = ["close-others"],
        ["Tools.IdeActivateWindow"] = ["activate-window"],
        ["Tools.IdeListWindows"] = ["list-windows"],
        ["Tools.IdeExecuteVsCommand"] = ["execute-command"],
        ["Tools.IdeFindAllReferences"] = ["find-references"],
        ["Tools.IdeShowCallHierarchy"] = ["call-hierarchy"],
        ["Tools.IdeGetDocumentSlice"] = ["document-slice"],
        ["Tools.IdeGetSmartContextForQuery"] = ["smart-context"],
        ["Tools.IdeApplyUnifiedDiff"] = ["apply-diff", "apply-patch"],
        ["Tools.IdeWriteFile"] = ["write-file"],
        ["Tools.IdeGoToDefinition"] = ["goto-definition"],
        ["Tools.IdeGoToImplementation"] = ["goto-implementation"],
        ["Tools.IdeGetFileOutline"] = ["file-outline"],
        ["Tools.IdeSearchSymbols"] = ["search-symbols"],
        ["Tools.IdeGetQuickInfo"] = ["quick-info"],
        ["Tools.IdePeekDefinition"] = ["peek-definition"],
        ["Tools.IdeGetDocumentSlices"] = ["document-slices"],
        ["Tools.IdeGetFileSymbols"] = ["file-symbols"],
        ["Tools.IdeSetBreakpoint"] = ["set-breakpoint"],
        ["Tools.IdeListBreakpoints"] = ["list-breakpoints"],
        ["Tools.IdeRemoveBreakpoint"] = ["remove-breakpoint"],
        ["Tools.IdeClearAllBreakpoints"] = ["clear-breakpoints"],
        ["Tools.IdeEnableBreakpoint"] = ["enable-breakpoint"],
        ["Tools.IdeDisableBreakpoint"] = ["disable-breakpoint"],
        ["Tools.IdeEnableAllBreakpoints"] = ["enable-all-breakpoints"],
        ["Tools.IdeDisableAllBreakpoints"] = ["disable-all-breakpoints"],
        ["Tools.IdeDebugGetState"] = ["debug-state"],
        ["Tools.IdeDebugStart"] = ["debug-start"],
        ["Tools.IdeDebugStop"] = ["debug-stop"],
        ["Tools.IdeDebugBreak"] = ["debug-break"],
        ["Tools.IdeDebugContinue"] = ["debug-continue"],
        ["Tools.IdeDebugStepOver"] = ["debug-step-over"],
        ["Tools.IdeDebugStepInto"] = ["debug-step-into"],
        ["Tools.IdeDebugStepOut"] = ["debug-step-out"],
        ["Tools.IdeDebugThreads"] = ["debug-threads"],
        ["Tools.IdeDebugStack"] = ["debug-stack"],
        ["Tools.IdeDebugLocals"] = ["debug-locals"],
        ["Tools.IdeDebugModules"] = ["debug-modules"],
        ["Tools.IdeDebugWatch"] = ["debug-watch"],
        ["Tools.IdeDebugExceptions"] = ["debug-exceptions"],
        ["Tools.IdeDiagnosticsSnapshot"] = ["diagnostics-snapshot"],
        ["Tools.IdeBuildConfigurations"] = ["build-configurations"],
        ["Tools.IdeSetBuildConfiguration"] = ["set-build-configuration"],
        ["Tools.IdeCountReferences"] = ["count-references"],
        ["Tools.IdeBuildSolution"] = ["build"],
        ["Tools.IdeRebuildSolution"] = ["rebuild", "rebuild-solution"],
        ["Tools.IdeGetErrorList"] = ["errors"],
        ["Tools.IdeGetWarnings"] = ["warnings"],
        ["Tools.IdeGetMessages"] = ["messages"],
        ["Tools.IdeBuildAndCaptureErrors"] = ["build-errors"],
        ["Tools.IdeListProjects"] = ["list-projects"],
        ["Tools.IdeQueryProjectItems"] = ["query-project-items"],
        ["Tools.IdeQueryProjectProperties"] = ["query-project-properties"],
        ["Tools.IdeQueryProjectConfigurations"] = ["query-project-configurations"],
        ["Tools.IdeQueryProjectReferences"] = ["query-project-references"],
        ["Tools.IdeQueryProjectOutputs"] = ["query-project-outputs"],
        ["Tools.IdeAddProject"] = ["add-project"],
        ["Tools.IdeCreateProject"] = ["create-project"],
        ["Tools.IdeRemoveProject"] = ["remove-project"],
        ["Tools.IdeSetStartupProject"] = ["set-startup-project"],
        ["Tools.IdeListLaunchProfiles"] = ["list-launch-profiles"],
        ["Tools.IdeSetLaunchProfile"] = ["set-launch-profile"],
        ["Tools.IdeRenameProject"] = ["rename-project"],
        ["Tools.IdeAddFileToProject"] = ["add-file-to-project"],
        ["Tools.IdeRemoveFileFromProject"] = ["remove-file-from-project"],
        ["Tools.IdeSetPythonProjectEnv"] = ["set-python-project-env"],
        ["Tools.IdeSetPythonStartupFile"] = ["set-python-startup-file"],
        ["Tools.IdeGetPythonStartupFile"] = ["get-python-startup-file"],
        ["Tools.IdeSearchSolutions"] = ["search-solutions"],
        ["Tools.IdeRunCodeAnalysis"] = ["run-code-analysis"],
    };

    public static string GetPrimaryName(string canonicalName)
    {
        foreach (string alias in GetAliases(canonicalName))
        {
            return alias;
        }

        return canonicalName;
    }

    public static IReadOnlyList<string> GetAliases(string canonicalName)
    {
        List<string> aliases = [];
        if (PreferredAliases.TryGetValue(canonicalName, out var preferred))
        {
            aliases.AddRange(preferred);
        }

        string generated = ToKebabAlias(canonicalName);
        if (!string.IsNullOrWhiteSpace(generated) &&
            !aliases.Exists(alias => string.Equals(alias, generated, StringComparison.OrdinalIgnoreCase)))
        {
            aliases.Add(generated);
        }

        return aliases;
    }

    private static string ToKebabAlias(string canonicalName)
    {
        string suffix = canonicalName;
        if (suffix.StartsWith("Tools.Ide", StringComparison.OrdinalIgnoreCase))
        {
            suffix = suffix.Substring("Tools.Ide".Length);
        }
        else if (suffix.StartsWith("Tools.VsIdeBridge", StringComparison.OrdinalIgnoreCase))
        {
            suffix = suffix.Substring("Tools.VsIdeBridge".Length);
        }
        else if (suffix.StartsWith("Tools.", StringComparison.OrdinalIgnoreCase))
        {
            suffix = suffix.Substring("Tools.".Length);
        }

        if (string.IsNullOrWhiteSpace(suffix))
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        for (int i = 0; i < suffix.Length; i++)
        {
            char ch = suffix[i];
            if (char.IsUpper(ch) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}
