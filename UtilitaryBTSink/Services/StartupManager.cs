using System;
using Microsoft.Win32;

namespace UtilitaryBTSink.Services;

public sealed class StartupManager
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string AppValueName = "UtilitaryBTSink";

    public bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(AppValueName) is string;
    }

    public void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        if (enabled)
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                key.SetValue(AppValueName, $"\"{exePath}\" --minimized");
            }
        }
        else
        {
            key.DeleteValue(AppValueName, throwOnMissingValue: false);
        }
    }
}
