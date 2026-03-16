using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VsIdeBridgeCli;

internal static partial class PythonRuntimeService
{
    private const string ActiveInterpreterPathPropertyName = "activeInterpreterPath";
    private const string ManagedEnvironmentPathPropertyName = "managedEnvironmentPath";
    private const string ManagedBaseInterpreterPathPropertyName = "managedBaseInterpreterPath";
    private const string ManagedRuntimeVersionPropertyName = "managedRuntimeVersion";
    private const string ProvisioningModePropertyName = "provisioningMode";
    private const string ManagedProvisioningMode = "managed";
    private const string SkipProvisioningMode = "skip";
    private const string RestrictedExecutionMode = "restricted";
    private const string UnrestrictedExecutionMode = "unrestricted";
    private const string RestrictedExecutionSummary = "Restricted scratch mode blocks file writes, file deletes, process launch, network access, temp file creation, and unsafe native imports.";
    private const string FileWritesDisabledReason = "file writes are disabled in restricted mode";
    private const string DirectoryDeletionDisabledReason = "directory deletion is disabled in restricted mode";
    private const string ProcessLaunchDisabledReason = "process launch is disabled in restricted mode";
    private const string RegistryChangesDisabledReason = "registry changes are disabled in restricted mode";
    private const string NetworkAccessDisabledReason = "network access is disabled in restricted mode";

    private static Regex PyLauncherLineRegex()
    {
        return PyLauncherPathRegex();
    }

    internal static async Task<JsonObject> ListEnvironmentsAsync()
    {
        var activeInterpreterPath = LoadActiveInterpreterPath();
        var environments = await DiscoverEnvironmentsAsync(activeInterpreterPath).ConfigureAwait(false);
        return new JsonObject
        {
            ["success"] = true,
            ["selectedPath"] = environments.FirstOrDefault(environment => environment.Selected)?.Path,
            ["count"] = environments.Count,
            ["envs"] = ToJsonArray(environments),
        };
    }

    internal static async Task<JsonObject> GetEnvironmentInfoAsync(string? interpreterPath = null)
    {
        var environment = await ResolveEnvironmentAsync(interpreterPath).ConfigureAwait(false);
        return new JsonObject
        {
            ["success"] = true,
            ["env"] = environment.ToJson(),
        };
    }

    internal static async Task<JsonObject> SetActiveEnvironmentAsync(string? interpreterPath, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(interpreterPath) && string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Either 'path' or 'name' is required.");
        }

        var environment = await ResolveEnvironmentAsync(interpreterPath, name).ConfigureAwait(false);
        SaveActiveInterpreterPath(environment.Path);
        var selectedEnvironment = environment with { Selected = true };
        return new JsonObject
        {
            ["success"] = true,
            ["selectedPath"] = selectedEnvironment.Path,
            ["env"] = selectedEnvironment.ToJson(),
        };
    }

    internal static async Task<JsonObject> ListPackagesAsync(string? interpreterPath = null)
    {
        var environment = await ResolveEnvironmentAsync(interpreterPath).ConfigureAwait(false);
        var result = await ProcessRunner.RunAsync(
            new ProcessStartInfo
            {
                FileName = environment.Path,
                Arguments = "-I -m pip list --format=json",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }).ConfigureAwait(false);

        JsonArray packages;
        if (result.Success)
        {
            try
            {
                packages = JsonNode.Parse(result.Stdout) as JsonArray ?? [];
            }
            catch (JsonException)
            {
                packages = [];
            }
        }
        else
        {
            packages = [];
        }

        return new JsonObject
        {
            ["success"] = result.Success,
            ["env"] = environment.ToJson(),
            ["count"] = packages.Count,
            ["packages"] = packages,
            ["stderr"] = result.Stderr,
            ["stdout"] = result.Stdout,
            ["exitCode"] = result.ExitCode,
        };
    }

    internal static async Task<JsonObject> ExecuteSnippetAsync(string code, string? interpreterPath = null, string? workingDirectory = null, int timeoutMs = ProcessRunner.DefaultTimeoutMilliseconds, bool approved = false, bool allowUnrestrictedExecution = false)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Python code is required.");
        }

        var environment = await ResolveEnvironmentAsync(interpreterPath).ConfigureAwait(false);
        var executionMode = GetExecutionMode(allowUnrestrictedExecution);
        var approval = EnsureApproved(
            toolName: "python_repl",
            environment,
            approved,
            mutating: false,
            actionDescription: allowUnrestrictedExecution ? "execute unrestricted Python code" : "execute restricted Python code",
            executionMode);
        if (!approval.Allowed)
        {
            return BuildApprovalRequiredPayload(approval, environment);
        }

        var resolvedWorkingDirectory = ResolveWorkingDirectory(workingDirectory);
        var tempScriptPath = CreateTemporaryScriptPath();
        Directory.CreateDirectory(Path.GetDirectoryName(tempScriptPath) ?? Path.GetTempPath());
        await File.WriteAllTextAsync(tempScriptPath, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
        var bootstrapScriptPath = allowUnrestrictedExecution ? null : CreateTemporaryBootstrapPath();
        if (bootstrapScriptPath is not null)
        {
            await File.WriteAllTextAsync(
                bootstrapScriptPath,
                BuildRestrictedBootstrapScript("snippet", tempScriptPath),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
        }

        try
        {
            return await RunInterpreterAsync(
                environment,
                toolName: "python_repl",
                arguments: QuoteArgument(bootstrapScriptPath ?? tempScriptPath),
                workingDirectory: resolvedWorkingDirectory,
                timeoutMs,
                additionalData: BuildPythonExecutionAdditionalData("inline", executionMode)).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tempScriptPath);
            if (bootstrapScriptPath is not null)
            {
                TryDeleteFile(bootstrapScriptPath);
            }
        }
    }

    internal static async Task<JsonObject> RunFileAsync(string filePath, IReadOnlyList<string>? scriptArguments = null, string? interpreterPath = null, string? workingDirectory = null, int timeoutMs = ProcessRunner.DefaultTimeoutMilliseconds, bool approved = false, bool allowUnrestrictedExecution = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Python file path is required.");
        }

        var environment = await ResolveEnvironmentAsync(interpreterPath).ConfigureAwait(false);
        var executionMode = GetExecutionMode(allowUnrestrictedExecution);
        var approval = EnsureApproved(
            toolName: "python_run_file",
            environment,
            approved,
            mutating: false,
            actionDescription: allowUnrestrictedExecution ? "run an unrestricted Python file" : "run a restricted Python file",
            executionMode);
        if (!approval.Allowed)
        {
            return BuildApprovalRequiredPayload(approval, environment);
        }

        var resolvedWorkingDirectory = ResolveWorkingDirectory(workingDirectory);
        var resolvedFilePath = ResolvePathFromWorkingDirectory(filePath, resolvedWorkingDirectory);
        if (!File.Exists(resolvedFilePath))
        {
            throw new InvalidOperationException($"Python file '{resolvedFilePath}' does not exist.");
        }

        string arguments;
        string? bootstrapScriptPath = null;
        if (allowUnrestrictedExecution)
        {
            var argumentsBuilder = new StringBuilder();
            argumentsBuilder.Append(QuoteArgument(resolvedFilePath));
            foreach (var argument in scriptArguments ?? [])
            {
                argumentsBuilder.Append(' ').Append(QuoteArgument(argument));
            }

            arguments = argumentsBuilder.ToString();
        }
        else
        {
            bootstrapScriptPath = CreateTemporaryBootstrapPath();
            Directory.CreateDirectory(Path.GetDirectoryName(bootstrapScriptPath) ?? Path.GetTempPath());
            await File.WriteAllTextAsync(
                bootstrapScriptPath,
                BuildRestrictedBootstrapScript("file", resolvedFilePath, scriptArguments),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
            arguments = QuoteArgument(bootstrapScriptPath);
        }

        try
        {
            return await RunInterpreterAsync(
                environment,
                toolName: "python_run_file",
                arguments,
                workingDirectory: resolvedWorkingDirectory,
                timeoutMs,
                additionalData: BuildPythonExecutionAdditionalData(
                    "file",
                    executionMode,
                    new JsonObject
                    {
                        ["file"] = resolvedFilePath,
                        ["argCount"] = scriptArguments?.Count ?? 0,
                    })).ConfigureAwait(false);
        }
        finally
        {
            if (bootstrapScriptPath is not null)
            {
                TryDeleteFile(bootstrapScriptPath);
            }
        }
    }

    internal static async Task<JsonObject> InstallPackagesAsync(IReadOnlyList<string> packages, string? interpreterPath = null, int timeoutMs = ProcessRunner.DefaultTimeoutMilliseconds, bool approved = false)
    {
        if (packages.Count == 0)
        {
            throw new InvalidOperationException("At least one Python package is required.");
        }

        var environment = await ResolveEnvironmentAsync(interpreterPath).ConfigureAwait(false);
        var approval = EnsureApproved(
            toolName: "python_install_package",
            environment,
            approved,
            mutating: true,
            actionDescription: "install Python packages");
        if (!approval.Allowed)
        {
            return BuildApprovalRequiredPayload(approval, environment);
        }

        return await RunInterpreterAsync(
            environment,
            toolName: "python_install_package",
            arguments: "-I -m pip install " + JoinArguments(packages),
            workingDirectory: Directory.GetCurrentDirectory(),
            timeoutMs,
            additionalData: new JsonObject
            {
                ["packageManager"] = "pip",
                ["packages"] = ToJsonArray(packages),
                ["packageCount"] = packages.Count,
            }).ConfigureAwait(false);
    }

    internal static async Task<JsonObject> InstallRequirementsAsync(string requirementsFile, string? interpreterPath = null, int timeoutMs = ProcessRunner.DefaultTimeoutMilliseconds, bool approved = false)
    {
        var absPath = System.IO.Path.GetFullPath(requirementsFile);
        if (!System.IO.File.Exists(absPath))
            throw new InvalidOperationException($"Requirements file not found: {absPath}");

        var environment = await ResolveEnvironmentAsync(interpreterPath).ConfigureAwait(false);
        var approval = EnsureApproved(
            toolName: "python_install_requirements",
            environment,
            approved,
            mutating: true,
            actionDescription: "install Python requirements");
        if (!approval.Allowed)
        {
            return BuildApprovalRequiredPayload(approval, environment);
        }

        return await RunInterpreterAsync(
            environment,
            toolName: "python_install_requirements",
            arguments: "-I -m pip install -r " + QuoteArgument(absPath),
            workingDirectory: System.IO.Path.GetDirectoryName(absPath)!,
            timeoutMs,
            additionalData: new JsonObject
            {
                ["packageManager"] = "pip",
                ["requirementsFile"] = absPath,
            }).ConfigureAwait(false);
    }

    internal static async Task<JsonObject> RemovePackagesAsync(IReadOnlyList<string> packages, string? interpreterPath = null, int timeoutMs = ProcessRunner.DefaultTimeoutMilliseconds, bool approved = false)
    {
        if (packages.Count == 0)
        {
            throw new InvalidOperationException("At least one Python package is required.");
        }

        var environment = await ResolveEnvironmentAsync(interpreterPath).ConfigureAwait(false);
        var approval = EnsureApproved(
            toolName: "python_remove_package",
            environment,
            approved,
            mutating: true,
            actionDescription: "remove Python packages");
        if (!approval.Allowed)
        {
            return BuildApprovalRequiredPayload(approval, environment);
        }

        return await RunInterpreterAsync(
            environment,
            toolName: "python_remove_package",
            arguments: "-I -m pip uninstall -y " + JoinArguments(packages),
            workingDirectory: Directory.GetCurrentDirectory(),
            timeoutMs,
            additionalData: new JsonObject
            {
                ["packageManager"] = "pip",
                ["packages"] = ToJsonArray(packages),
                ["packageCount"] = packages.Count,
            }).ConfigureAwait(false);
    }

    internal static async Task<JsonObject> CreateEnvironmentAsync(string targetPath, string? baseInterpreterPath = null, string? workingDirectory = null, int timeoutMs = ProcessRunner.DefaultTimeoutMilliseconds, bool approved = false)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new InvalidOperationException("Environment path is required.");
        }

        var baseEnvironment = await ResolveEnvironmentAsync(baseInterpreterPath).ConfigureAwait(false);
        var approval = EnsureApproved(
            toolName: "python_create_env",
            baseEnvironment,
            approved,
            mutating: true,
            actionDescription: "create a Python environment");
        if (!approval.Allowed)
        {
            return BuildApprovalRequiredPayload(approval, baseEnvironment);
        }

        var resolvedWorkingDirectory = ResolveWorkingDirectory(workingDirectory);
        var resolvedTargetPath = ResolvePathFromWorkingDirectory(targetPath, resolvedWorkingDirectory);
        EnsureEnvironmentTargetIsEmpty(resolvedTargetPath);

        var result = await RunInterpreterAsync(
            baseEnvironment,
            toolName: "python_create_env",
            arguments: "-I -m venv " + QuoteArgument(resolvedTargetPath),
            workingDirectory: resolvedWorkingDirectory,
            timeoutMs,
            additionalData: new JsonObject
            {
                ["targetPath"] = resolvedTargetPath,
                ["baseInterpreterPath"] = baseEnvironment.Path,
            }).ConfigureAwait(false);

        var createdInterpreterPath = GetEnvironmentInterpreterPath(resolvedTargetPath);
        result["createdInterpreterPath"] = createdInterpreterPath;
        if (result["success"]?.GetValue<bool>() == true && File.Exists(createdInterpreterPath))
        {
            var createdEnvironment = await InspectInterpreterAsync(createdInterpreterPath, selected: false, source: "created").ConfigureAwait(false);
            result["createdEnv"] = createdEnvironment.ToJson();
        }

        return result;
    }

    private static async Task<PythonEnvironment> ResolveEnvironmentAsync(string? interpreterPath, string? name = null)
    {
        var requestedPath = NormalizePath(interpreterPath);
        var environments = await DiscoverEnvironmentsAsync(LoadActiveInterpreterPath()).ConfigureAwait(false);
        var environment = !string.IsNullOrWhiteSpace(requestedPath)
            ? environments.FirstOrDefault(item => PathEquals(item.Path, requestedPath))
            : !string.IsNullOrWhiteSpace(name)
                ? environments.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                : environments.FirstOrDefault(item => item.Selected);

        if (environment is not null)
        {
            return environment;
        }

        if (!string.IsNullOrWhiteSpace(requestedPath) && File.Exists(requestedPath))
        {
            return await InspectInterpreterAsync(requestedPath, selected: false, source: "explicit").ConfigureAwait(false);
        }

        var notFoundReason = !string.IsNullOrWhiteSpace(name)
            ? $"No Python environment named '{name}' was found. Call python_list_envs to see available environments."
            : "No Python interpreter is available. Configure a bridge-managed runtime or select an existing interpreter first.";
        throw new InvalidOperationException(notFoundReason);
    }

    private static async Task<List<PythonEnvironment>> DiscoverEnvironmentsAsync(string? activeInterpreterPath)
    {
        var environments = new List<PythonEnvironment>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        activeInterpreterPath = NormalizePath(activeInterpreterPath);

        foreach (var managedCandidate in GetManagedInterpreterCandidates())
        {
            await TryAddInterpreterAsync(environments, seenPaths, managedCandidate, activeInterpreterPath, "managed").ConfigureAwait(false);
        }

        foreach (var launcherPath in await DiscoverViaPyLauncherAsync().ConfigureAwait(false))
        {
            await TryAddInterpreterAsync(environments, seenPaths, launcherPath, activeInterpreterPath, "py-launcher").ConfigureAwait(false);
        }

        foreach (var pathCandidate in GetPathInterpreterCandidates())
        {
            await TryAddInterpreterAsync(environments, seenPaths, pathCandidate, activeInterpreterPath, "path").ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(activeInterpreterPath) && File.Exists(activeInterpreterPath) && seenPaths.Add(activeInterpreterPath))
        {
            environments.Add(await InspectInterpreterAsync(activeInterpreterPath, selected: true, source: "persisted").ConfigureAwait(false));
        }

        if (environments.Count > 0 && environments.All(environment => !environment.Selected))
        {
            environments[0] = environments[0] with { Selected = true };
        }

        return
        [
            .. environments
                .OrderByDescending(environment => environment.Selected)
                .ThenBy(environment => environment.Kind, StringComparer.Ordinal)
                .ThenBy(environment => environment.Path, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static async Task TryAddInterpreterAsync(List<PythonEnvironment> environments, HashSet<string> seenPaths, string? candidatePath, string? activeInterpreterPath, string source)
    {
        var normalizedPath = NormalizePath(candidatePath);
        if (string.IsNullOrWhiteSpace(normalizedPath) || !File.Exists(normalizedPath) || !seenPaths.Add(normalizedPath))
        {
            return;
        }

        environments.Add(await InspectInterpreterAsync(normalizedPath, PathEquals(normalizedPath, activeInterpreterPath), source).ConfigureAwait(false));
    }

    private static async Task<PythonEnvironment> InspectInterpreterAsync(string interpreterPath, bool selected, string source)
    {
        var metadata = await ReadInterpreterMetadataAsync(interpreterPath).ConfigureAwait(false);
        var kind = ClassifyEnvironmentKind(interpreterPath);
        var runtimeConfig = kind == "managed" ? ReadManagedRuntimeConfig() : null;
        var managedRuntimeVersion = kind == "managed"
            ? (string.IsNullOrWhiteSpace(metadata.Version) ? runtimeConfig?.ManagedRuntimeVersion : metadata.Version)
            : null;
        var name = ComputeEnvironmentName(kind, metadata.Prefix);
        return new PythonEnvironment(
            Path: interpreterPath,
            Name: name,
            Version: metadata.Version,
            Kind: kind,
            Selected: selected,
            Source: source,
            PackageManagers: GetPackageManagers(kind),
            Prefix: metadata.Prefix,
            BasePrefix: metadata.BasePrefix,
            Executable: metadata.Executable,
            ProvisioningMode: kind == "managed" ? ManagedProvisioningMode : null,
            ManagedRuntimeVersion: managedRuntimeVersion);
    }

    private static async Task<JsonObject> RunInterpreterAsync(PythonEnvironment environment, string toolName, string arguments, string workingDirectory, int timeoutMs, JsonObject? additionalData = null)
    {
        var normalizedTimeout = NormalizeTimeout(timeoutMs);
        var startInfo = new ProcessStartInfo
        {
            FileName = environment.Path,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(startInfo, normalizedTimeout).ConfigureAwait(false);
        stopwatch.Stop();

        var payload = new JsonObject
        {
            ["success"] = result.Success,
            ["tool"] = toolName,
            ["env"] = environment.ToJson(),
            ["workingDirectory"] = workingDirectory,
            ["command"] = result.Command,
            ["args"] = result.Arguments,
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr,
            ["exitCode"] = result.ExitCode,
            ["durationMs"] = stopwatch.ElapsedMilliseconds,
        };

        MergeJsonObject(payload, additionalData);
        return payload;
    }

    private static JsonObject BuildPythonExecutionAdditionalData(string inputKind, string executionMode, JsonObject? additionalData = null)
    {
        var payload = new JsonObject
        {
            ["inputKind"] = inputKind,
            ["executionMode"] = executionMode,
        };

        if (string.Equals(executionMode, RestrictedExecutionMode, StringComparison.Ordinal))
        {
            payload["restrictionSummary"] = RestrictedExecutionSummary;
        }

        MergeJsonObject(payload, additionalData);
        return payload;
    }

    internal static string BuildRestrictedBootstrapScript(string entryKind, string targetPath, IReadOnlyList<string>? scriptArguments = null)
    {
        var entryKindLiteral = JsonSerializer.Serialize(entryKind);
        var targetLiteral = JsonSerializer.Serialize(targetPath);
        var argsLiteral = JsonSerializer.Serialize(scriptArguments ?? []);
        var fileWritesDisabledLiteral = JsonSerializer.Serialize(FileWritesDisabledReason);
        var directoryDeletionDisabledLiteral = JsonSerializer.Serialize(DirectoryDeletionDisabledReason);
        var processLaunchDisabledLiteral = JsonSerializer.Serialize(ProcessLaunchDisabledReason);
        var registryChangesDisabledLiteral = JsonSerializer.Serialize(RegistryChangesDisabledReason);
        var networkAccessDisabledLiteral = JsonSerializer.Serialize(NetworkAccessDisabledReason);
        return $$"""
import builtins
import os
import pathlib
import runpy
import shutil
import socket
import sys
import tempfile

_ENTRY_KIND = {{entryKindLiteral}}
_TARGET = {{targetLiteral}}
_SCRIPT_ARGS = {{argsLiteral}}
_FILE_WRITES_DISABLED = {{fileWritesDisabledLiteral}}
_DIRECTORY_DELETION_DISABLED = {{directoryDeletionDisabledLiteral}}
_PROCESS_LAUNCH_DISABLED = {{processLaunchDisabledLiteral}}
_REGISTRY_CHANGES_DISABLED = {{registryChangesDisabledLiteral}}
_NETWORK_ACCESS_DISABLED = {{networkAccessDisabledLiteral}}

sys.dont_write_bytecode = True

class BridgeRestrictionError(PermissionError):
    pass

def _bridge_block(reason):
    raise BridgeRestrictionError("Bridge restricted Python execution blocked: " + reason)

def _bridge_is_write_mode(mode):
    mode_text = str(mode or "r")
    return any(token in mode_text for token in ("w", "a", "+", "x"))

def _bridge_is_write_open(args):
    if len(args) >= 3 and isinstance(args[2], int):
        write_flags = os.O_WRONLY | os.O_RDWR | os.O_CREAT | os.O_APPEND | os.O_TRUNC
        return (args[2] & write_flags) != 0
    if len(args) >= 2:
        return _bridge_is_write_mode(args[1])
    return False

def _bridge_audit(event, args):
    if event == "open" and _bridge_is_write_open(args):
        _bridge_block(_FILE_WRITES_DISABLED)
    blocked_events = {
        "os.remove": "file deletion is disabled in restricted mode",
        "os.rename": "file renames are disabled in restricted mode",
        "os.replace": "file replacement is disabled in restricted mode",
        "os.mkdir": "directory creation is disabled in restricted mode",
        "os.rmdir": _DIRECTORY_DELETION_DISABLED,
        "os.link": "hard link creation is disabled in restricted mode",
        "os.symlink": "symlink creation is disabled in restricted mode",
        "os.chmod": "file mode changes are disabled in restricted mode",
        "os.chown": "ownership changes are disabled in restricted mode",
        "os.startfile": _PROCESS_LAUNCH_DISABLED,
        "os.system": _PROCESS_LAUNCH_DISABLED,
        "shutil.copyfile": _FILE_WRITES_DISABLED,
        "shutil.copytree": "directory writes are disabled in restricted mode",
        "shutil.move": "file moves are disabled in restricted mode",
        "shutil.rmtree": _DIRECTORY_DELETION_DISABLED,
        "ctypes.dlopen": "native library loading is disabled in restricted mode",
        "winreg.CreateKey": _REGISTRY_CHANGES_DISABLED,
        "winreg.DeleteKey": _REGISTRY_CHANGES_DISABLED,
        "winreg.DeleteValue": _REGISTRY_CHANGES_DISABLED,
        "winreg.SaveKey": _REGISTRY_CHANGES_DISABLED,
        "winreg.SetValue": _REGISTRY_CHANGES_DISABLED,
        "winreg.SetValueEx": _REGISTRY_CHANGES_DISABLED,
    }
    reason = blocked_events.get(event)
    if reason is not None:
        _bridge_block(reason)
    if event.startswith("subprocess."):
        _bridge_block(_PROCESS_LAUNCH_DISABLED)
    if event.startswith("socket."):
        _bridge_block(_NETWORK_ACCESS_DISABLED)
    if event.startswith("winreg."):
        _bridge_block(_REGISTRY_CHANGES_DISABLED)

sys.addaudithook(_bridge_audit)

def _bridge_blocked(reason):
    def _blocked(*args, **kwargs):
        _bridge_block(reason)
    return _blocked

_bridge_original_import = builtins.__import__
def _bridge_import(name, globals=None, locals=None, fromlist=(), level=0):
    root = (name or "").split(".", 1)[0]
    if root in {"ctypes", "_ctypes", "multiprocessing", "subprocess", "winreg"}:
        _bridge_block(f"importing {root} is disabled in restricted mode")
    return _bridge_original_import(name, globals, locals, fromlist, level)

builtins.__import__ = _bridge_import

_bridge_builtin_open = builtins.open
def _bridge_open(file, mode="r", *args, **kwargs):
    if _bridge_is_write_mode(mode):
        _bridge_block(_FILE_WRITES_DISABLED)
    return _bridge_builtin_open(file, mode, *args, **kwargs)

builtins.open = _bridge_open

for _name, _reason in {
    "makedirs": "directory creation is disabled in restricted mode",
    "mkdir": "directory creation is disabled in restricted mode",
    "removedirs": _DIRECTORY_DELETION_DISABLED,
    "rmdir": _DIRECTORY_DELETION_DISABLED,
    "remove": "file deletion is disabled in restricted mode",
    "unlink": "file deletion is disabled in restricted mode",
    "rename": "file renames are disabled in restricted mode",
    "replace": "file replacement is disabled in restricted mode",
    "chmod": "file mode changes are disabled in restricted mode",
    "chown": "ownership changes are disabled in restricted mode",
    "link": "hard link creation is disabled in restricted mode",
    "symlink": "symlink creation is disabled in restricted mode",
    "system": _PROCESS_LAUNCH_DISABLED,
    "popen": _PROCESS_LAUNCH_DISABLED,
    "startfile": _PROCESS_LAUNCH_DISABLED,
    "spawnl": _PROCESS_LAUNCH_DISABLED,
    "spawnle": _PROCESS_LAUNCH_DISABLED,
    "spawnlp": _PROCESS_LAUNCH_DISABLED,
    "spawnlpe": _PROCESS_LAUNCH_DISABLED,
    "spawnv": _PROCESS_LAUNCH_DISABLED,
    "spawnve": _PROCESS_LAUNCH_DISABLED,
    "spawnvp": _PROCESS_LAUNCH_DISABLED,
    "spawnvpe": _PROCESS_LAUNCH_DISABLED,
    "execv": _PROCESS_LAUNCH_DISABLED,
    "execve": _PROCESS_LAUNCH_DISABLED,
    "execvp": _PROCESS_LAUNCH_DISABLED,
    "execvpe": _PROCESS_LAUNCH_DISABLED,
}.items():
    if hasattr(os, _name):
        setattr(os, _name, _bridge_blocked(_reason))

for _name, _reason in {
    "copy": _FILE_WRITES_DISABLED,
    "copy2": _FILE_WRITES_DISABLED,
    "copyfile": _FILE_WRITES_DISABLED,
    "copytree": "directory writes are disabled in restricted mode",
    "move": "file moves are disabled in restricted mode",
    "rmtree": _DIRECTORY_DELETION_DISABLED,
}.items():
    if hasattr(shutil, _name):
        setattr(shutil, _name, _bridge_blocked(_reason))

for _name, _reason in {
    "mkstemp": "temp file creation is disabled in restricted mode",
    "mkdtemp": "temp directory creation is disabled in restricted mode",
    "NamedTemporaryFile": "temp file creation is disabled in restricted mode",
    "TemporaryDirectory": "temp directory creation is disabled in restricted mode",
    "TemporaryFile": "temp file creation is disabled in restricted mode",
    "SpooledTemporaryFile": "temp file creation is disabled in restricted mode",
}.items():
    if hasattr(tempfile, _name):
        setattr(tempfile, _name, _bridge_blocked(_reason))

for _name, _reason in {
    "socket": "network access is disabled in restricted mode",
    "create_connection": _NETWORK_ACCESS_DISABLED,
    "create_server": _NETWORK_ACCESS_DISABLED,
    "fromfd": _NETWORK_ACCESS_DISABLED,
    "socketpair": _NETWORK_ACCESS_DISABLED,
}.items():
    if hasattr(socket, _name):
        setattr(socket, _name, _bridge_blocked(_reason))

_bridge_path_open = pathlib.Path.open
def _bridge_read_only_path_open(self, mode="r", *args, **kwargs):
    if _bridge_is_write_mode(mode):
        _bridge_block(_FILE_WRITES_DISABLED)
    return _bridge_path_open(self, mode, *args, **kwargs)

pathlib.Path.open = _bridge_read_only_path_open

for _name, _reason in {
    "chmod": "file mode changes are disabled in restricted mode",
    "hardlink_to": "hard link creation is disabled in restricted mode",
    "mkdir": "directory creation is disabled in restricted mode",
    "rename": "file renames are disabled in restricted mode",
    "replace": "file replacement is disabled in restricted mode",
    "rmdir": _DIRECTORY_DELETION_DISABLED,
    "symlink_to": "symlink creation is disabled in restricted mode",
    "touch": _FILE_WRITES_DISABLED,
    "unlink": "file deletion is disabled in restricted mode",
    "write_bytes": _FILE_WRITES_DISABLED,
    "write_text": _FILE_WRITES_DISABLED,
}.items():
    if hasattr(pathlib.Path, _name):
        setattr(pathlib.Path, _name, _bridge_blocked(_reason))

sys.argv = [_TARGET, *_SCRIPT_ARGS]

if _ENTRY_KIND == "snippet":
    _bridge_globals = {
        "__name__": "__main__",
        "__file__": _TARGET,
        "__package__": None,
        "__cached__": None,
    }
    with builtins.open(_TARGET, "r", encoding="utf-8") as _bridge_handle:
        _bridge_code = _bridge_handle.read()
    exec(compile(_bridge_code, _TARGET, "exec"), _bridge_globals)
else:
    runpy.run_path(_TARGET, run_name="__main__")
""";
    }

    private static async Task<PythonInterpreterMetadata> ReadInterpreterMetadataAsync(string interpreterPath)
    {
        const string script = "import json, sys; print(json.dumps({'version': sys.version.split()[0], 'prefix': sys.prefix, 'base_prefix': getattr(sys, 'base_prefix', sys.prefix), 'executable': sys.executable}))";
        var startInfo = new ProcessStartInfo
        {
            FileName = interpreterPath,
            Arguments = $"-I -c {QuoteArgument(script)}",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var result = await ProcessRunner.RunAsync(startInfo, timeoutMs: 10_000).ConfigureAwait(false);
        if (!result.Success)
        {
            var fallbackVersion = TryParseVersionFromStdErr(result.Stderr);
            return new PythonInterpreterMetadata(fallbackVersion, null, null, interpreterPath);
        }

        try
        {
            var json = JsonNode.Parse(result.Stdout) as JsonObject;
            return new PythonInterpreterMetadata(
                json?["version"]?.GetValue<string>(),
                json?["prefix"]?.GetValue<string>(),
                json?["base_prefix"]?.GetValue<string>(),
                json?["executable"]?.GetValue<string>() ?? interpreterPath);
        }
        catch (JsonException)
        {
            return new PythonInterpreterMetadata(null, null, null, interpreterPath);
        }
    }

    private static async Task<IEnumerable<string>> DiscoverViaPyLauncherAsync()
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                new ProcessStartInfo
                {
                    FileName = "py",
                    Arguments = "-0p",
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                timeoutMs: 10_000).ConfigureAwait(false);

            if (!result.Success)
            {
                return [];
            }

            var paths = new List<string>();
            foreach (var line in result.Stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                var match = PyLauncherLineRegex().Match(line);
                if (match.Success)
                {
                    paths.Add(match.Groups["path"].Value.Trim());
                }
            }

            return paths;
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> GetPathInterpreterCandidates()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in directories)
        {
            var pythonExe = Path.Combine(directory, "python.exe");
            if (File.Exists(pythonExe))
            {
                yield return pythonExe;
            }

            var python3Exe = Path.Combine(directory, "python3.exe");
            if (File.Exists(python3Exe))
            {
                yield return python3Exe;
            }
        }
    }

    private static IEnumerable<string> GetManagedInterpreterCandidates()
    {
        var envOverride = NormalizePath(Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_MANAGED_PYTHON_PATH"));
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            yield return envOverride;
        }

        var runtimeConfig = ReadManagedRuntimeConfig();
        if (!runtimeConfig.ManagedEnabled)
        {
            yield break;
        }

        foreach (var configuredPath in runtimeConfig.InterpreterPaths)
        {
            yield return configuredPath;
        }

        var installedRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        yield return Path.Combine(installedRoot, "python", "managed-runtime", "python.exe");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "VsIdeBridge", "python", "managed-runtime", "python.exe");
        }
    }

    private static ManagedPythonRuntimeConfig ReadManagedRuntimeConfig()
    {
        var configDirectory = Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_CONFIG_DIR");
        string configPath;
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            configPath = Path.Combine(configDirectory, "python-runtime.json");
        }
        else
        {
            var installedRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            configPath = Path.Combine(installedRoot, "config", "python-runtime.json");
        }

        if (!File.Exists(configPath))
        {
            return new ManagedPythonRuntimeConfig(false, true, [], null);
        }

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            var paths = new List<string>();
            var provisioningMode = json?[ProvisioningModePropertyName]?.GetValue<string>()?.Trim();
            var managedRuntimeVersion = json?[ManagedRuntimeVersionPropertyName]?.GetValue<string>()?.Trim();
            var managedEnvironmentPath = NormalizePath(json?[ManagedEnvironmentPathPropertyName]?.GetValue<string>());
            if (!string.IsNullOrWhiteSpace(managedEnvironmentPath))
            {
                paths.Add(managedEnvironmentPath);
            }

            var managedBaseInterpreterPath = NormalizePath(json?[ManagedBaseInterpreterPathPropertyName]?.GetValue<string>());
            if (!string.IsNullOrWhiteSpace(managedBaseInterpreterPath))
            {
                paths.Add(managedBaseInterpreterPath);
            }

            return new ManagedPythonRuntimeConfig(
                true,
                !string.Equals(provisioningMode, SkipProvisioningMode, StringComparison.OrdinalIgnoreCase),
                paths,
                string.IsNullOrWhiteSpace(managedRuntimeVersion) ? null : managedRuntimeVersion);
        }
        catch (IOException)
        {
            return new ManagedPythonRuntimeConfig(false, true, [], null);
        }
        catch (JsonException)
        {
            return new ManagedPythonRuntimeConfig(false, true, [], null);
        }
    }

    private static string ClassifyEnvironmentKind(string interpreterPath)
    {
        var normalizedPath = NormalizePath(interpreterPath) ?? interpreterPath;
        if (IsManagedInterpreter(normalizedPath))
        {
            return "managed";
        }

        var interpreterDirectory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(interpreterDirectory))
        {
            if (Directory.Exists(Path.Combine(interpreterDirectory, "conda-meta")) ||
                Directory.Exists(Path.Combine(interpreterDirectory, "..", "conda-meta")))
            {
                return "conda";
            }

            if (File.Exists(Path.Combine(interpreterDirectory, "pyvenv.cfg")) ||
                File.Exists(Path.Combine(interpreterDirectory, "..", "pyvenv.cfg")))
            {
                return "venv";
            }
        }

        return "system";
    }

    private static string? ComputeEnvironmentName(string kind, string? prefix)
    {
        if (kind == "managed")
        {
            return "managed";
        }

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var leafName = System.IO.Path.GetFileName(prefix.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(leafName))
            {
                return leafName;
            }
        }

        return null;
    }

    private static bool IsManagedInterpreter(string interpreterPath)
    {
        foreach (var candidate in GetManagedInterpreterCandidates())
        {
            if (PathEquals(candidate, interpreterPath))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonArray GetPackageManagers(string kind)
    {
        return kind switch
        {
            "conda" => ["pip", "conda"],
            _ => ["pip"],
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<PythonEnvironment> environments)
    {
        var array = new JsonArray();
        foreach (var environment in environments)
        {
            array.Add(environment.ToJson());
        }

        return array;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    internal static string? LoadActiveInterpreterPath()
    {
        var state = LoadState();
        return NormalizePath(state[ActiveInterpreterPathPropertyName]?.GetValue<string>());
    }

    private static void SaveActiveInterpreterPath(string interpreterPath)
    {
        var state = LoadState();
        state[ActiveInterpreterPathPropertyName] = NormalizePath(interpreterPath);
        SaveState(state);
    }

    private static string GetStateFilePath()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_PYTHON_STATE_DIR");
        var baseDirectory = !string.IsNullOrWhiteSpace(overrideDirectory)
            ? overrideDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsIdeBridge");
        return Path.Combine(baseDirectory, "python-state.json");
    }

    private static JsonObject LoadState()
    {
        var statePath = GetStateFilePath();
        if (!File.Exists(statePath))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject ?? [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SaveState(JsonObject state)
    {
        var statePath = GetStateFilePath();
        var stateDirectory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory);
        }

        File.WriteAllText(statePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static PythonApprovalDecision EnsureApproved(string toolName, PythonEnvironment environment, bool approved, bool mutating, string actionDescription, string? executionMode = null)
    {
        var approvalScope = BuildApprovalScope(toolName, environment, mutating, executionMode);
        if (!approved)
        {
            return new PythonApprovalDecision(
                false,
                "required",
                approvalScope,
                BuildApprovalRequiredMessage(environment, mutating, actionDescription, executionMode));
        }

        return new PythonApprovalDecision(true, "bridge", approvalScope, null);
    }

    private static JsonObject BuildApprovalRequiredPayload(PythonApprovalDecision decision, PythonEnvironment environment)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["message"] = decision.Message,
            ["env"] = environment.ToJson(),
            ["approvalRequired"] = true,
            ["approvalGranted"] = false,
            ["approvalChoice"] = decision.Choice,
            ["approvalScope"] = decision.Scope.DeepClone(),
        };
    }

    private static JsonObject BuildApprovalScope(string toolName, PythonEnvironment environment, bool mutating, string? executionMode = null)
    {
        var scope = new JsonObject
        {
            ["tool"] = toolName,
            ["interpreterPath"] = environment.Path,
            ["kind"] = environment.Kind,
            ["mutating"] = mutating,
            ["userOwnedEnvironment"] = !string.Equals(environment.Kind, "managed", StringComparison.Ordinal),
            ["provisioningMode"] = environment.ProvisioningMode,
        };

        if (!string.IsNullOrWhiteSpace(executionMode))
        {
            scope["executionMode"] = executionMode;
        }

        return scope;
    }

    private static string BuildApprovalRequiredMessage(PythonEnvironment environment, bool mutating, string actionDescription, string? executionMode = null)
    {
        var builder = new StringBuilder();
        builder.Append("Visual Studio bridge approval is required to ")
            .Append(actionDescription)
            .Append(" using ")
            .Append(environment.Path)
            .Append('.');

        if (string.Equals(executionMode, RestrictedExecutionMode, StringComparison.Ordinal))
        {
            builder.Append(' ').Append(RestrictedExecutionSummary);
        }
        else if (string.Equals(executionMode, UnrestrictedExecutionMode, StringComparison.Ordinal))
        {
            builder.Append(" Unrestricted Python execution can modify files, launch processes, and access the network.");
        }

        if (mutating)
        {
            builder.Append(' ');
            builder.Append(string.Equals(environment.Kind, "managed", StringComparison.Ordinal)
                ? "This will modify the bridge-managed Python environment."
                : "This will modify an existing user-owned Python environment.");
        }

        return builder.ToString();
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        var normalizedPath = NormalizePath(workingDirectory);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            if (!Directory.Exists(normalizedPath))
            {
                throw new InvalidOperationException($"Working directory '{normalizedPath}' does not exist.");
            }

            return normalizedPath;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolvePathFromWorkingDirectory(string path, string workingDirectory)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.IsPathRooted(expandedPath)
            ? Path.GetFullPath(expandedPath)
            : Path.GetFullPath(expandedPath, workingDirectory);
    }

    private static string CreateTemporaryScriptPath()
    {
        return Path.Combine(Path.GetTempPath(), "VsIdeBridge", "python", $"snippet-{Guid.NewGuid():N}.py");
    }

    private static string CreateTemporaryBootstrapPath()
    {
        return Path.Combine(Path.GetTempPath(), "VsIdeBridge", "python", $"bootstrap-{Guid.NewGuid():N}.py");
    }

    private static string GetExecutionMode(bool allowUnrestrictedExecution)
    {
        return allowUnrestrictedExecution ? UnrestrictedExecutionMode : RestrictedExecutionMode;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for temporary script files.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for temporary script files.
        }
    }

    private static void EnsureEnvironmentTargetIsEmpty(string targetPath)
    {
        if (File.Exists(targetPath))
        {
            throw new InvalidOperationException($"Cannot create a Python environment at '{targetPath}' because a file already exists there.");
        }

        if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
        {
            throw new InvalidOperationException($"Cannot create a Python environment at '{targetPath}' because the directory is not empty.");
        }

        var parentDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }
    }

    private static string GetEnvironmentInterpreterPath(string environmentDirectory)
    {
        return Path.Combine(environmentDirectory, "Scripts", "python.exe");
    }

    private static int NormalizeTimeout(int timeoutMs)
    {
        return timeoutMs <= 0 ? ProcessRunner.DefaultTimeoutMilliseconds : timeoutMs;
    }

    private static string JoinArguments(IEnumerable<string> values)
    {
        return string.Join(" ", values.Select(QuoteArgument));
    }

    private static void MergeJsonObject(JsonObject target, JsonObject? additionalData)
    {
        if (additionalData is null)
        {
            return;
        }

        foreach (var property in additionalData)
        {
            target[property.Key] = property.Value?.DeepClone();
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool PathEquals(string? left, string? right)
    {
        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryParseVersionFromStdErr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return null;
        }

        var tokens = stderr.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.FirstOrDefault(token => Version.TryParse(token, out _));
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    [GeneratedRegex(@"^\s*-[^\s]+\s+\*?\s*(?<path>.+?)\s*$", RegexOptions.Compiled)]
    private static partial Regex PyLauncherPathRegex();

    private sealed record PythonInterpreterMetadata(string? Version, string? Prefix, string? BasePrefix, string? Executable);

    private sealed record PythonEnvironment(
        string Path,
        string? Name,
        string? Version,
        string Kind,
        bool Selected,
        string Source,
        JsonArray PackageManagers,
        string? Prefix,
        string? BasePrefix,
        string? Executable,
        string? ProvisioningMode,
        string? ManagedRuntimeVersion)
    {
        internal JsonObject ToJson()
        {
            return new JsonObject
            {
                ["path"] = Path,
                ["name"] = Name,
                ["version"] = Version,
                ["kind"] = Kind,
                ["selected"] = Selected,
                ["source"] = Source,
                ["packageManagers"] = PackageManagers.DeepClone(),
                ["prefix"] = Prefix,
                ["basePrefix"] = BasePrefix,
                ["executable"] = Executable,
                ["provisioningMode"] = ProvisioningMode,
                ["managedRuntimeVersion"] = ManagedRuntimeVersion,
            };
        }
    }

    private sealed record PythonApprovalDecision(bool Allowed, string Choice, JsonObject Scope, string? Message);

    private sealed record ManagedPythonRuntimeConfig(bool Exists, bool ManagedEnabled, IReadOnlyList<string> InterpreterPaths, string? ManagedRuntimeVersion);
}
