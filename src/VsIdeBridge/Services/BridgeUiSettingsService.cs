using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using System;
using System.Collections.Generic;

namespace VsIdeBridge.Services;

internal sealed class BridgeUiSettingsService
{
    private const string CollectionPath = "VsIdeBridge";
    private const string AllowShellExecKey = "AllowBridgeShellExec";
    private const string AllowPythonExecutionKey = "AllowBridgePythonExecution";
    private const string AllowPythonUnrestrictedExecutionKey = "AllowBridgePythonUnrestrictedExecution";
    private const string AllowPythonEnvironmentMutationKey = "AllowBridgePythonEnvironmentMutation";
    private const string GoToEditedPartsKey = "GoToEditedParts";
    private const string BestPracticeDiagnosticsEnabledKey = "BestPracticeDiagnosticsEnabled";
    private const string AllowBuildKey = "AllowBridgeBuild";
    private const string HttpServerEnabledKey = "HttpServerEnabled";

    private readonly WritableSettingsStore? _store;
    private readonly Dictionary<string, bool> _fallback = new(0, StringComparer.OrdinalIgnoreCase);

    public BridgeUiSettingsService(IServiceProvider serviceProvider)
    {
        try
        {
            ShellSettingsManager settingsManager = new(serviceProvider);
            _store = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);
            if (!_store.CollectionExists(CollectionPath))
            {
                _store.CreateCollection(CollectionPath);
            }
        }
        catch
        {
            _store = null;
        }
    }

    public bool AllowBridgeShellExec
    {
        get => ReadBoolean(AllowShellExecKey, defaultValue: false);
        set => WriteBoolean(AllowShellExecKey, value);
    }

    public bool AllowBridgePythonExecution
    {
        get => ReadBoolean(AllowPythonExecutionKey, defaultValue: false);
        set => WriteBoolean(AllowPythonExecutionKey, value);
    }

    public bool AllowBridgePythonUnrestrictedExecution
    {
        get => ReadBoolean(AllowPythonUnrestrictedExecutionKey, defaultValue: false);
        set => WriteBoolean(AllowPythonUnrestrictedExecutionKey, value);
    }

    public bool AllowBridgePythonEnvironmentMutation
    {
        get => ReadBoolean(AllowPythonEnvironmentMutationKey, defaultValue: false);
        set => WriteBoolean(AllowPythonEnvironmentMutationKey, value);
    }

    public bool GoToEditedParts
    {
        get => ReadBoolean(GoToEditedPartsKey, defaultValue: true);
        set => WriteBoolean(GoToEditedPartsKey, value);
    }

    public bool BestPracticeDiagnosticsEnabled
    {
        get => ReadBoolean(BestPracticeDiagnosticsEnabledKey, defaultValue: true);
        set => WriteBoolean(BestPracticeDiagnosticsEnabledKey, value);
    }

    public bool AllowBridgeBuild
    {
        get => ReadBoolean(AllowBuildKey, defaultValue: false);
        set => WriteBoolean(AllowBuildKey, value);
    }

    public bool HttpServerEnabled
    {
        get => ReadBoolean(HttpServerEnabledKey, defaultValue: false);
        set => WriteBoolean(HttpServerEnabledKey, value);
    }

    private bool ReadBoolean(string name, bool defaultValue)
    {
        if (_store is not null)
        {
            try
            {
                return _store.PropertyExists(CollectionPath, name)
                    ? _store.GetBoolean(CollectionPath, name)
                    : defaultValue;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        return _fallback.TryGetValue(name, out var value) ? value : defaultValue;
    }

    private void WriteBoolean(string name, bool value)
    {
        if (_store is not null)
        {
            try
            {
                _store.SetBoolean(CollectionPath, name, value);
                return;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        _fallback[name] = value;
    }
}
