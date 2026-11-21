using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UtilitaryBTSink.Models;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace UtilitaryBTSink.Services;

public sealed class BluetoothDiscoveryService : IDisposable
{
    private sealed class DiscoveredEntry
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public bool IsAutoConnectEnabled { get; set; }
    }

    private readonly ConcurrentDictionary<string, DiscoveredEntry> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiagnosticLogger _logger;
    private DeviceWatcher? _watcher;
    private TaskCompletionSource<bool> _enumerationCompletedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action? DevicesChanged;

    public BluetoothDiscoveryService(DiagnosticLogger logger)
    {
        _logger = logger;
    }

    public async Task WaitForInitialEnumerationAsync(TimeSpan timeout)
    {
        await Task.WhenAny(_enumerationCompletedTcs.Task, Task.Delay(timeout));
    }

    public IReadOnlyList<BluetoothAudioDevice> GetDevicesSnapshot()
        => _devices.Values
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new BluetoothAudioDevice
            {
                Id = d.Id,
                Name = d.Name,
                IsAutoConnectEnabled = d.IsAutoConnectEnabled
            })
            .ToList();

    public BluetoothAudioDevice? FindDevice(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var d)) return null;
        return new BluetoothAudioDevice
        {
            Id = d.Id,
            Name = d.Name,
            IsAutoConnectEnabled = d.IsAutoConnectEnabled
        };
    }

    public void UpdateAutoConnectFlag(string deviceId, bool enabled)
    {
        if (_devices.TryGetValue(deviceId, out var entry))
        {
            entry.IsAutoConnectEnabled = enabled;
        }
    }

    public void StartWatching(WatchdogSettings settings)
    {
        StopWatching();
        _enumerationCompletedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        string selector = AudioPlaybackConnection.GetDeviceSelector();
        _logger.Log("DISCOVERY", $"Starting A2DP Sink DeviceWatcher | Selector={selector}");
        var watcher = DeviceInformation.CreateWatcher(selector);

        watcher.Added += (_, info) =>
        {
            string name = string.IsNullOrWhiteSpace(info.Name) ? "Bluetooth Device" : info.Name;
            bool auto = settings.IsAutoConnectEnabled(info.Id);
            _devices[info.Id] = new DiscoveredEntry
            {
                Id = info.Id,
                Name = name,
                IsAutoConnectEnabled = auto
            };
            _logger.Log("DISCOVERY", $"Added: {name} | AutoConnect={auto} | Enabled={info.IsEnabled} | Id={info.Id}");
            DevicesChanged?.Invoke();
        };

        watcher.Updated += (_, update) =>
        {
            if (_devices.TryGetValue(update.Id, out var existing))
            {
                if (update.Properties.TryGetValue("System.ItemNameDisplay", out var nameObj) &&
                    nameObj is string updatedName &&
                    !string.IsNullOrWhiteSpace(updatedName))
                {
                    existing.Name = updatedName;
                }
                _logger.Log("DISCOVERY", $"Updated: {existing.Name} | Props={string.Join(",", update.Properties.Keys)}");
                DevicesChanged?.Invoke();
            }
        };

        watcher.Removed += (_, removal) =>
        {
            if (_devices.TryRemove(removal.Id, out var removed))
            {
                _logger.Log("DISCOVERY", $"Removed: {removed.Name} | Id={removal.Id}");
                DevicesChanged?.Invoke();
            }
        };

        watcher.EnumerationCompleted += (_, _) =>
        {
            _logger.Log("DISCOVERY", $"EnumerationCompleted: {_devices.Count} paired A2DP Sink device(s).");
            _enumerationCompletedTcs.TrySetResult(true);
            DevicesChanged?.Invoke();
        };

        watcher.Stopped += (sender, _) =>
        {
            _logger.Log("DISCOVERY", $"DeviceWatcher stopped (Status={sender.Status}).");
            _enumerationCompletedTcs.TrySetResult(false);
        };

        _watcher = watcher;
        _watcher.Start();
    }

    public void StopWatching()
    {
        if (_watcher is null) return;

        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            _watcher.Stop();
        }
        _watcher = null;
        _enumerationCompletedTcs.TrySetResult(false);
    }

    public void Dispose() => StopWatching();
}
