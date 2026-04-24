using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using VsIdeBridge.Commands;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Services;

internal static class PipeServerSupport
{
    public static bool ReadBooleanEnvironmentVariable(string name, bool defaultValue)
    {
        try
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            if (bool.TryParse(raw, out bool parsed))
            {
                return parsed;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            {
                return numeric != 0;
            }

            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public static PipeSecurity CreatePipeSecurity()
    {
        PipeSecurity security = new();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        AddPipeAccessRule(security, identity.User, PipeAccessRights.FullControl);
        AddPipeAccessRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl);
        AddPipeAccessRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl);
        AddPipeAccessRule(security, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), NamedPipeAccessDefaults.ClientReadWriteRights);
        TryAddPipeAccessRule(security, "S-1-15-2-1", NamedPipeAccessDefaults.ClientReadWriteRights);
        return security;
    }

    public static string SerializeSuccessEnvelope(string commandName, string? requestId, CommandExecutionResult commandResult, DateTimeOffset enqueuedAtUtc, DateTimeOffset startedAtUtc, int queuePositionAtEnqueue, double queueWaitMs)
    {
        return SerializeQueuedEnvelope(
            commandName,
            requestId,
            success: true,
            commandResult.Summary,
            commandResult.Data,
            commandResult.Warnings,
            error: null,
            enqueuedAtUtc,
            startedAtUtc,
            queuePositionAtEnqueue,
            queueWaitMs);
    }

    public static string SerializeFailureEnvelope(string commandName, string? requestId, string summary, JToken failureData, object error, DateTimeOffset enqueuedAtUtc, DateTimeOffset startedAtUtc, int queuePositionAtEnqueue, double queueWaitMs)
    {
        return SerializeQueuedEnvelope(
            commandName,
            requestId,
            success: false,
            summary,
            failureData,
            [],
            error,
            enqueuedAtUtc,
            startedAtUtc,
            queuePositionAtEnqueue,
            queueWaitMs);
    }

    public static bool IsCompactCommandError(string code)
    {
        return string.Equals(code, "invalid_arguments", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "invalid_request", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "command_not_found", StringComparison.OrdinalIgnoreCase);
    }

    public static JToken BuildCompactCommandErrorData(string commandName, CommandErrorException ex)
    {
        JObject errorData = new()
        {
            ["command"] = commandName,
            ["errorCode"] = ex.Code,
            ["recoveryHint"] = BuildCompactCommandErrorHint(commandName, ex.Code),
        };

        if (ex.Details is not null)
        {
            errorData["details"] = JToken.FromObject(ex.Details);
        }

        return errorData;
    }

    private static void AddPipeAccessRule(PipeSecurity security, SecurityIdentifier? sid, PipeAccessRights rights)
    {
        if (sid is null)
        {
            return;
        }

        security.AddAccessRule(new PipeAccessRule(sid, rights, AccessControlType.Allow));
    }

    private static void TryAddPipeAccessRule(PipeSecurity security, string sidValue, PipeAccessRights rights)
    {
        try
        {
            AddPipeAccessRule(security, new SecurityIdentifier(sidValue), rights);
        }
        catch (ArgumentException ex)
        {
            System.Diagnostics.Debug.WriteLine($"PipeServerSupport failed to add pipe access rule for SID '{sidValue}': {ex}");
        }
    }

    private static string SerializeQueuedEnvelope(string commandName, string? requestId, bool success, string summary, JToken data, JArray warnings, object? error, DateTimeOffset enqueuedAtUtc, DateTimeOffset startedAtUtc, int queuePositionAtEnqueue, double queueWaitMs)
    {
        string queuedSummary = BuildQueuedSummary(summary, queuePositionAtEnqueue, queueWaitMs);
        JToken responseData = WithQueueMetadata(data, enqueuedAtUtc, startedAtUtc, queuePositionAtEnqueue, queueWaitMs);
        JArray responseWarnings = WithQueueWarning(warnings, queuePositionAtEnqueue, queueWaitMs);
        CommandEnvelope envelope = new()
        {
            SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
            Command = commandName,
            RequestId = requestId,
            Success = success,
            StartedAtUtc = startedAtUtc.UtcDateTime.ToString("O"),
            FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
            Summary = queuedSummary,
            Warnings = responseWarnings,
            Error = error,
            Data = responseData,
        };
        return JsonConvert.SerializeObject(envelope);
    }

    private static string BuildQueuedSummary(string summary, int queuePositionAtEnqueue, double queueWaitMs)
    {
        if (queuePositionAtEnqueue <= 0 && queueWaitMs < 100)
        {
            return summary;
        }

        return $"{summary} (queued {Math.Round(queueWaitMs)} ms behind {queuePositionAtEnqueue} request(s))";
    }

    private static JToken WithQueueMetadata(JToken data, DateTimeOffset enqueuedAtUtc, DateTimeOffset startedAtUtc, int queuePositionAtEnqueue, double queueWaitMs)
    {
        JObject queueMetadata = new()
        {
            ["positionAtEnqueue"] = queuePositionAtEnqueue,
            ["waitMs"] = Math.Round(Math.Max(0, queueWaitMs), 1),
            ["wasQueued"] = queuePositionAtEnqueue > 0 || queueWaitMs >= 100,
            ["enqueuedAtUtc"] = enqueuedAtUtc.UtcDateTime.ToString("O"),
            ["startedAtUtc"] = startedAtUtc.UtcDateTime.ToString("O"),
        };

        if (data is JObject obj)
        {
            obj["queue"] = queueMetadata;
            return obj;
        }

        return new JObject
        {
            ["value"] = data,
            ["queue"] = queueMetadata,
        };
    }

    private static JArray WithQueueWarning(JArray warnings, int queuePositionAtEnqueue, double queueWaitMs)
    {
        if (queuePositionAtEnqueue <= 0 && queueWaitMs < 100)
        {
            return warnings;
        }

        warnings.Add($"Command waited in queue for {Math.Round(queueWaitMs)} ms.");
        return warnings;
    }

    private static string BuildCompactCommandErrorHint(string commandName, string code)
    {
        if (string.Equals(commandName, "apply-diff", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeApplyDiff", StringComparison.OrdinalIgnoreCase))
        {
            return "Use editor patch format with '*** Begin Patch', '*** Update File:', and '*** End Patch'. Unified diff headers like '---' and '+++' are not accepted here.";
        }

        if (string.Equals(code, "invalid_arguments", StringComparison.OrdinalIgnoreCase))
        {
            return "Check the command arguments and try again. Use the tool help or schema for the expected argument names and formats.";
        }

        if (string.Equals(code, "invalid_request", StringComparison.OrdinalIgnoreCase))
        {
            return "Check the request payload shape and try again. The command could not parse the incoming request.";
        }

        if (string.Equals(code, "command_not_found", StringComparison.OrdinalIgnoreCase))
        {
            return "Refresh the installed tool catalog and use the exact tool name exposed by the bridge.";
        }

        return "Check the command input and try again.";
    }
}
