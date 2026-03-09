using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using System;
using System.Collections.Generic;

namespace VsIdeBridge.Services;

internal sealed class BridgeUiSettingsService
{
    private const string CollectionPath = "VsIdeBridge";
    private const string AllowEditsKey = "AllowBridgeEdits";
    private const string GoToEditedPartsKey = "GoToEditedParts";
    private const string BestPracticeDiagnosticsEnabledKey = "BestPracticeDiagnosticsEnabled";

    private readonly WritableSettingsStore? _store;
    private readonly Dictionary<string, bool> _fallback = new(StringComparer.OrdinalIgnoreCase);

    public BridgeUiSettingsService(IServiceProvider serviceProvider)
    {
        try
        {
            var settingsManager = new ShellSettingsManager(serviceProvider);
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

    public bool AllowBridgeEdits
    {
        get => ReadBoolean(AllowEditsKey, defaultValue: false);
        set => WriteBoolean(AllowEditsKey, value);
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
            catch (Exception ex)
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        _fallback[name] = value;
    }
}
