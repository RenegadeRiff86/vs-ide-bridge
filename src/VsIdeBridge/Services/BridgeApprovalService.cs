using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal enum BridgeApprovalKind
{
    Edit,
    ShellExec,
}

internal sealed class BridgeApprovalService
{
    public async Task<JObject> RequestApprovalAsync(IdeCommandContext context, BridgeApprovalKind kind, string? subject, string? details)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        if (IsPersistentlyAllowed(context.Runtime.UiSettings, kind))
        {
            return CreateApprovalData(kind, approval: "persistent", approvalChoice: "persisted_setting", promptShown: false, resultCode: 0);
        }

        await context.Logger.LogAsync(
            $"IDE Bridge: waiting for {GetOperationDisplayName(kind)} approval in Visual Studio.",
            context.CancellationToken,
            activatePane: true).ConfigureAwait(true);

        var result = VsShellUtilities.ShowMessageBox(
            context.Package,
            BuildPromptMessage(kind, subject, details),
            "IDE Bridge Approval Required",
            OLEMSGICON.OLEMSGICON_QUERY,
            OLEMSGBUTTON.OLEMSGBUTTON_YESNOCANCEL,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);

        if (result == (int)VSConstants.MessageBoxResult.IDYES)
        {
            await context.Logger.LogAsync(
                $"IDE Bridge: one-time {GetOperationDisplayName(kind)} approval granted.",
                context.CancellationToken,
                activatePane: true).ConfigureAwait(true);
            return CreateApprovalData(kind, approval: "one-time", approvalChoice: "yes", promptShown: true, resultCode: result);
        }

        if (result == (int)VSConstants.MessageBoxResult.IDCANCEL)
        {
            SetPersistentlyAllowed(context.Runtime.UiSettings, kind, enabled: true);
            await context.Logger.LogAsync(
                $"IDE Bridge: persistent {GetOperationDisplayName(kind)} approval enabled from the Visual Studio prompt.",
                context.CancellationToken,
                activatePane: true).ConfigureAwait(true);
            return CreateApprovalData(kind, approval: "persistent", approvalChoice: "dont_ask_again", promptShown: true, resultCode: result);
        }

        var deniedChoice = result == (int)VSConstants.MessageBoxResult.IDNO ? "no" : "dismissed";

        throw new CommandErrorException(
            GetDeniedCode(kind),
            GetDeniedMessage(kind),
            new
            {
                approvalRequested = true,
                approvalChoice = deniedChoice,
                promptShown = true,
                operation = GetOperationCode(kind),
                persistentSettingEnabled = false,
                resultCode = result,
            });
    }

    private static bool IsPersistentlyAllowed(BridgeUiSettingsService settings, BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.Edit => settings.AllowBridgeEdits,
            BridgeApprovalKind.ShellExec => settings.AllowBridgeShellExec,
            _ => false,
        };
    }

    private static JObject CreateApprovalData(BridgeApprovalKind kind, string approval, string approvalChoice, bool promptShown, int resultCode)
    {
        return new JObject
        {
            ["operation"] = GetOperationCode(kind),
            ["approval"] = approval,
            ["approvalChoice"] = approvalChoice,
            ["promptShown"] = promptShown,
            ["persistentSettingEnabled"] = string.Equals(approval, "persistent", StringComparison.Ordinal),
            ["resultCode"] = resultCode,
        };
    }

    private static void SetPersistentlyAllowed(BridgeUiSettingsService settings, BridgeApprovalKind kind, bool enabled)
    {
        switch (kind)
        {
            case BridgeApprovalKind.Edit:
                settings.AllowBridgeEdits = enabled;
                break;
            case BridgeApprovalKind.ShellExec:
                settings.AllowBridgeShellExec = enabled;
                break;
        }
    }

    private static string BuildPromptMessage(BridgeApprovalKind kind, string? subject, string? details)
    {
        var operationDisplayName = GetOperationDisplayName(kind);
        var message = $"An external IDE Bridge request wants to {operationDisplayName}.\r\n\r\n";

        if (!string.IsNullOrWhiteSpace(subject))
        {
            message += $"Target: {TrimForPrompt(subject!, 160)}\r\n";
        }

        if (!string.IsNullOrWhiteSpace(details))
        {
            message += $"Details: {TrimForPrompt(details!, 260)}\r\n";
        }

        message += "\r\n" +
            "Yes: allow this request once.\r\n" +
            "No: keep it blocked.\r\n" +
            "Cancel: don't ask again for this kind of request.\r\n\r\n" +
            GetPersistentAllowHint(kind);

        return message;
    }

    private static string TrimForPrompt(string value, int maxLength)
    {
        var normalized = value.Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized.Substring(0, maxLength - 3) + "...";
    }

    private static string GetPersistentAllowHint(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.Edit => "Use IDE Bridge > Allow Bridge Edits if you want future edit requests to run without prompts.",
            BridgeApprovalKind.ShellExec => "Use IDE Bridge > Allow Bridge Shell Exec if you want future shell exec requests to run without prompts.",
            _ => "Use the IDE Bridge menu to change approval settings.",
        };
    }

    private static string GetDeniedCode(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.Edit => "edit_approval_denied",
            BridgeApprovalKind.ShellExec => "shell_exec_approval_denied",
            _ => "approval_denied",
        };
    }

    private static string GetDeniedMessage(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.Edit => "Bridge edit approval was denied. Wait for a human to approve the Visual Studio prompt, or enable IDE Bridge > Allow Bridge Edits.",
            BridgeApprovalKind.ShellExec => "Bridge shell exec approval was denied. Wait for a human to approve the Visual Studio prompt, or enable IDE Bridge > Allow Bridge Shell Exec.",
            _ => "Bridge approval was denied.",
        };
    }

    private static string GetOperationDisplayName(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.Edit => "edit files in this solution",
            BridgeApprovalKind.ShellExec => "run an external process from this solution",
            _ => "perform a privileged action",
        };
    }

    private static string GetOperationCode(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.Edit => "edit",
            BridgeApprovalKind.ShellExec => "shell_exec",
            _ => "unknown",
        };
    }
}
