using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace UtilitaryBTSink.Models;

public enum KeepAliveMode
{
    SubAudibleDither,
    SilentBufferFlag,
    Disabled
}

public sealed class SavedDevicePreference
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool AutoConnectEnabled { get; set; }
    public DateTimeOffset? LastConnectedUtc { get; set; }
}

public sealed class WatchdogSettings
{
    public int SilenceThresholdSeconds { get; set; } = 15;
    public int KeepAliveIntervalSeconds { get; set; } = 10;
    public int PhantomTimeoutSeconds { get; set; } = 20;
    public int MaxConsecutiveResets { get; set; } = 5;
    public KeepAliveMode KeepAliveMode { get; set; } = KeepAliveMode.SubAudibleDither;
    public bool AutoReconnectOnStartup { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public Dictionary<string, SavedDevicePreference> SavedDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAutoConnectEnabled(string deviceId)
    {
        return SavedDevices.TryGetValue(deviceId, out var pref) && pref.AutoConnectEnabled;
    }

    public void SetAutoConnect(string deviceId, string deviceName, bool enabled)
    {
        if (!SavedDevices.TryGetValue(deviceId, out var pref))
        {
            pref = new SavedDevicePreference
            {
                Id = deviceId,
                Name = deviceName
            };
            SavedDevices[deviceId] = pref;
        }

        pref.Name = deviceName;
        pref.AutoConnectEnabled = enabled;
    }

    public void MarkConnected(string deviceId, string deviceName)
    {
        if (!SavedDevices.TryGetValue(deviceId, out var pref))
        {
            pref = new SavedDevicePreference
            {
                Id = deviceId,
                Name = deviceName,
                AutoConnectEnabled = false
            };
            SavedDevices[deviceId] = pref;
        }

        pref.Name = deviceName;
        pref.LastConnectedUtc = DateTimeOffset.UtcNow;
    }
}

public sealed class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UtilitaryBTSink",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public WatchdogSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<WatchdogSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.SavedDevices = new Dictionary<string, SavedDevicePreference>(
                        loaded.SavedDevices ?? new(),
                        StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch
        {
            // Safe fallback to default settings if JSON is corrupted
        }
        return new WatchdogSettings();
    }

    public void Save(WatchdogSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
