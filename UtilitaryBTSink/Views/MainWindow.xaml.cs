using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UtilitaryBTSink.Models;
using UtilitaryBTSink.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace UtilitaryBTSink.Views;

public sealed partial class MainWindow : Window
{
    private const int CompactWidth = 680;
    private const int CompactHeight = 460;
    private const int ExpandedHeight = 670;

    private readonly WatchdogSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly BluetoothDiscoveryService _discovery;
    private readonly AudioPlaybackService _playback;
    private readonly WasapiKeepAliveEngine _keepAlive;
    private readonly PhantomStreamWatchdog _watchdog;
    private readonly StartupManager _startupManager;
    private readonly DiagnosticLogger _logger;
    private readonly TrayIconManager _tray;
    private readonly IntPtr _hwnd;
    private readonly ObservableCollection<BluetoothAudioDevice> _deviceItems = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _logRefreshTimer;
    private readonly SemaphoreSlim _bulkConnectLock = new(1, 1);
    private volatile bool _logDirty;
    private bool _showingPreviousLog;
    private bool _allowExit;
    private bool _initialized;

    public MainWindow(
        WatchdogSettings settings,
        SettingsStore settingsStore,
        BluetoothDiscoveryService discovery,
        AudioPlaybackService playback,
        WasapiKeepAliveEngine keepAlive,
        PhantomStreamWatchdog watchdog,
        StartupManager startupManager,
        DiagnosticLogger logger,
        bool startMinimized)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _discovery = discovery;
        _playback = playback;
        _keepAlive = keepAlive;
        _watchdog = watchdog;
        _startupManager = startupManager;
        _logger = logger;

        ConfigureMergedTitleBar();
        ApplyWindowIcon();

        _hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(CompactWidth, CompactHeight));
        DevicesListView.ItemsSource = _deviceItems;

        _logRefreshTimer = DispatcherQueue.CreateTimer();
        _logRefreshTimer.Interval = TimeSpan.FromMilliseconds(200);
        _logRefreshTimer.Tick += (_, _) =>
        {
            if (!_logDirty || _showingPreviousLog || LogsPanel.Visibility != Visibility.Visible) return;
            _logDirty = false;
            RefreshLogView();
        };
        _logRefreshTimer.Start();

        AppWindow.Closing += OnAppWindowClosing;

        _tray = new TrayIconManager(_hwnd, "UtilitaryBTSink: Ready");
        _tray.OpenRequested += () => DispatcherQueue.TryEnqueue(RestoreFromTray);
        _tray.ReconnectRequested += () => DispatcherQueue.TryEnqueue(async () => await _playback.ResetAllOpenedEndpointsAsync("Tray Menu"));
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);

        BindInitialSettings();
        WireServiceEvents();

        if (!startMinimized)
        {
            _ = InitializeBackgroundServicesAsync();
        }
    }

    private void ConfigureMergedTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }
        catch
        {
            // Safe fallback if the system compositor does not support title bar customization
        }
    }

    private void ApplyWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // Fall back to the embedded executable icon if the sidecar file is unavailable
        }
    }

    public async Task InitializeBackgroundServicesAsync()
    {
        if (_initialized) return;
        _initialized = true;

        await _keepAlive.DumpAudioEndpointsDiagnosticsAsync("Startup");
        _discovery.StartWatching(_settings);
        _watchdog.Start();

        if (_settings.AutoReconnectOnStartup)
        {
            await _discovery.WaitForInitialEnumerationAsync(TimeSpan.FromSeconds(4));
            await Task.Delay(500);
            await ConnectSavedDevicesAsync();
        }
    }

    private async Task ConnectSavedDevicesAsync()
    {
        if (!await _bulkConnectLock.WaitAsync(0))
        {
            _logger.Log("A2DP", "ConnectSavedDevicesAsync skipped: batch connection already in progress.");
            return;
        }

        try
        {
            var devices = _discovery.GetDevicesSnapshot();
            var newlyConnected = new List<BluetoothAudioDevice>();

            foreach (var device in devices)
            {
                if (_settings.IsAutoConnectEnabled(device.Id) && !_playback.IsConnectedOrPending(device.Id))
                {
                    _playback.ClearManualDisconnect(device.Id);
                    bool ok = await _playback.ConnectAsync(device, isAutoRequest: true);
                    if (ok)
                    {
                        newlyConnected.Add(device);
                        _settings.MarkConnected(device.Id, device.Name);
                        _settingsStore.Save(_settings);
                        await _keepAlive.DumpAudioEndpointsDiagnosticsAsync($"After AutoConnect {device.Name}");
                    }
                }
            }

            if (newlyConnected.Count >= 2)
            {
                var firstDevice = newlyConnected[0];
                _logger.Log("A2DP", $"Multi-Stream ({newlyConnected.Count} devices): re-syncing {firstDevice.Name} to stabilize simultaneous startup audio...");
                DiagnosticFooterText.Text = $"Stabilizing simultaneous audio ({newlyConnected.Count} devices)...";
                await Task.Delay(1000);
                bool resyncOk = await _playback.ReconnectDeviceAsync(firstDevice, "Initial Multi-Stream Stabilization");
                if (resyncOk)
                {
                    await _keepAlive.DumpAudioEndpointsDiagnosticsAsync($"After Multi-Stream Stabilization ({firstDevice.Name})");
                    DiagnosticFooterText.Text = $"Simultaneous audio stabilized ({_playback.OpenedCount} active).";
                }
            }
        }
        finally
        {
            _bulkConnectLock.Release();
        }
    }

    private void BindInitialSettings()
    {
        SilenceThresholdBox.Value = _settings.SilenceThresholdSeconds;
        PhantomTimeoutBox.Value = _settings.PhantomTimeoutSeconds;
        AutoReconnectToggle.IsOn = _settings.AutoReconnectOnStartup;
        StartupToggle.IsOn = _startupManager.IsStartupEnabled();
    }

    private void WireServiceEvents()
    {
        _logger.LogUpdated += () => _logDirty = true;

        _discovery.DevicesChanged += () =>
        {
            if (!_allowExit) DispatcherQueue.TryEnqueue(SyncDeviceCollection);
        };

        _playback.DeviceStateChanged += (deviceId, state, isConnecting, name) =>
        {
            if (_allowExit) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_allowExit) return;
                var row = _deviceItems.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.State = state;
                    row.IsConnecting = isConnecting;
                }
                int opened = _playback.OpenedCount;
                string summary = _playback.GetActiveDevicesSummary();
                HeaderStatusText.Text = $"{opened} active";
                _tray.UpdateTooltip($"UtilitaryBTSink: {opened} active ({summary})");
            });
        };

        _playback.DiagnosticLog += msg =>
        {
            if (!_allowExit) DispatcherQueue.TryEnqueue(() =>
            {
                if (!_allowExit) DiagnosticFooterText.Text = msg;
            });
        };

        _watchdog.TelemetryUpdated += snap =>
        {
            if (!_allowExit) DispatcherQueue.TryEnqueue(() =>
            {
                if (_allowExit) return;
                HeaderStatusText.Text = $"{snap.ActiveDevicesCount} active";
                TelemetrySummaryText.Text =
                    $"Active: {snap.ActiveDevicesCount} ({snap.ActiveDevicesSummary}) · Silence: {snap.SilenceSeconds:F1}s · Keep-Alive: {snap.KeepAlivePulsesSent} pulses · Recoveries: {snap.AutoRecoveryCount}";
                DiagnosticFooterText.Text = snap.LastDiagnosticMessage;
            });
        };
    }

    private void UpdateAuxPanelState(bool showSettings, bool showLogs)
    {
        SettingsToggleBtn.IsChecked = showSettings;
        LogsToggleBtn.IsChecked = showLogs;
        SettingsPanel.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        LogsPanel.Visibility = showLogs ? Visibility.Visible : Visibility.Collapsed;

        int targetHeight = (showSettings || showLogs) ? ExpandedHeight : CompactHeight;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(AppWindow.Size.Width, targetHeight));

        if (showLogs)
        {
            _logDirty = false;
            RefreshLogView();
        }
    }

    private void OnSettingsPanelToggled(object sender, RoutedEventArgs e)
    {
        bool open = SettingsToggleBtn.IsChecked == true;
        UpdateAuxPanelState(showSettings: open, showLogs: false);
    }

    private void OnLogsPanelToggled(object sender, RoutedEventArgs e)
    {
        bool open = LogsToggleBtn.IsChecked == true;
        UpdateAuxPanelState(showSettings: false, showLogs: open);
    }

    private void OnLogSourceToggled(object sender, RoutedEventArgs e)
    {
        _showingPreviousLog = LogSourceToggleBtn.IsChecked == true;
        LogTitleText.Text = _showingPreviousLog ? "Log — Last Session" : "Log — Current Session";
        LogSourceToggleBtn.Content = _showingPreviousLog ? "View Current Session" : "View Last Session";
        RefreshLogView();
    }

    private void RefreshLogView()
    {
        if (_allowExit) return;
        string text = _showingPreviousLog ? _logger.GetPreviousLogText() : _logger.GetFullLogText();
        if (DiagnosticLogText.Text != text)
        {
            DiagnosticLogText.Text = text;
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private void SyncDeviceCollection()
    {
        var snapshot = _discovery.GetDevicesSnapshot();

        for (int i = _deviceItems.Count - 1; i >= 0; i--)
        {
            if (!snapshot.Any(s => string.Equals(s.Id, _deviceItems[i].Id, StringComparison.OrdinalIgnoreCase)))
            {
                _deviceItems.RemoveAt(i);
            }
        }

        foreach (var dev in snapshot)
        {
            var existing = _deviceItems.FirstOrDefault(d => string.Equals(d.Id, dev.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                dev.IsAutoConnectEnabled = _settings.IsAutoConnectEnabled(dev.Id);
                dev.State = _playback.IsConnected(dev.Id)
                    ? Windows.Media.Audio.AudioPlaybackConnectionState.Opened
                    : Windows.Media.Audio.AudioPlaybackConnectionState.Closed;
                _deviceItems.Add(dev);
            }
            else
            {
                existing.Name = dev.Name;
            }
        }
    }

    private async void OnDeviceReconnectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string deviceId) return;
        var device = _discovery.FindDevice(deviceId);
        if (device is null) return;

        _logger.Log("UI", $"User clicked individual Reconnect on {device.Name}");
        bool ok = await _playback.ReconnectDeviceAsync(device, "User Requested");
        if (ok)
        {
            _settings.MarkConnected(device.Id, device.Name);
            _settingsStore.Save(_settings);
            await _keepAlive.DumpAudioEndpointsDiagnosticsAsync($"After Reconnect {device.Name}");
        }
    }

    private async void OnDeviceActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string deviceId) return;
        var device = _discovery.FindDevice(deviceId);
        if (device is null) return;

        if (_playback.IsConnected(deviceId))
        {
            _logger.Log("UI", $"User clicked Disconnect on {device.Name}");
            await _playback.DisconnectAsync(deviceId, isManual: true);
            await _keepAlive.DumpAudioEndpointsDiagnosticsAsync($"After Disconnect {device.Name}");
        }
        else
        {
            _logger.Log("UI", $"User clicked Connect on {device.Name}");
            bool ok = await _playback.ConnectAsync(device, isAutoRequest: false);
            if (ok)
            {
                _settings.MarkConnected(device.Id, device.Name);
                _settingsStore.Save(_settings);
                await _keepAlive.DumpAudioEndpointsDiagnosticsAsync($"After Connect {device.Name}");
            }
        }
    }

    private async void OnDeviceAutoConnectToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.Tag is not string deviceId) return;
        var row = _deviceItems.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;

        if (row.IsAutoConnectEnabled == toggle.IsOn) return;

        row.IsAutoConnectEnabled = toggle.IsOn;
        _discovery.UpdateAutoConnectFlag(row.Id, toggle.IsOn);
        _settings.SetAutoConnect(row.Id, row.Name, toggle.IsOn);
        _settingsStore.Save(_settings);

        _logger.Log("UI", $"AutoConnect changed to {toggle.IsOn} for {row.Name}");
        DiagnosticFooterText.Text = toggle.IsOn
            ? $"Auto-connect enabled for {row.Name}."
            : $"Auto-connect disabled for {row.Name}.";

        if (toggle.IsOn && _settings.AutoReconnectOnStartup && !_playback.IsConnectedOrPending(row.Id))
        {
            var device = _discovery.FindDevice(row.Id) ?? row;
            _playback.ClearManualDisconnect(device.Id);
            await _playback.ConnectAsync(device, isAutoRequest: true);
        }
    }

    private async void OnConnectSavedClicked(object sender, RoutedEventArgs e)
    {
        _logger.Log("UI", "User clicked Connect Saved");
        await ConnectSavedDevicesAsync();
    }

    private async void OnDisconnectAllClicked(object sender, RoutedEventArgs e)
    {
        _logger.Log("UI", "User clicked Disconnect All");
        foreach (var dev in _discovery.GetDevicesSnapshot())
        {
            await _playback.DisconnectAsync(dev.Id, isManual: true);
        }
        await _keepAlive.DumpAudioEndpointsDiagnosticsAsync("After Disconnect All");
    }

    private async void OnDiagnoseAudioClicked(object sender, RoutedEventArgs e)
    {
        _logger.Log("UI", $"Manual diagnostics requested | OpenedCount={_playback.OpenedCount} | Active={_playback.GetActiveDevicesSummary()}");
        await _keepAlive.DumpAudioEndpointsDiagnosticsAsync("User Requested");
        RefreshLogView();
    }

    private void OnCopyLogClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(_showingPreviousLog ? _logger.GetPreviousLogText() : _logger.GetFullLogText());
            Clipboard.SetContent(pkg);
            DiagnosticFooterText.Text = "Log copied to clipboard.";
        }
        catch (Exception ex)
        {
            _logger.Log("UI-ERR", $"Failed to copy log: {ex.Message}");
        }
    }

    private void OnOpenLogFolderClicked(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _logger.LogDirectory,
            UseShellExecute = true
        });
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        if (_showingPreviousLog)
        {
            _showingPreviousLog = false;
            LogSourceToggleBtn.IsChecked = false;
            LogTitleText.Text = "Log — Current Session";
            LogSourceToggleBtn.Content = "View Last Session";
        }
        _logger.Clear();
        RefreshLogView();
    }

    private void OnThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initialized || double.IsNaN(args.NewValue)) return;
        _settings.SilenceThresholdSeconds = (int)SilenceThresholdBox.Value;
        _settings.PhantomTimeoutSeconds = (int)PhantomTimeoutBox.Value;
        _settingsStore.Save(_settings);
        _logger.Log("SETTINGS", $"Thresholds updated: Silence={_settings.SilenceThresholdSeconds}s | Phantom={_settings.PhantomTimeoutSeconds}s");
    }

    private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.AutoReconnectOnStartup = AutoReconnectToggle.IsOn;
        _settingsStore.Save(_settings);
        _logger.Log("SETTINGS", $"AutoReconnectOnStartup={_settings.AutoReconnectOnStartup}");
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _startupManager.SetStartupEnabled(StartupToggle.IsOn);
        _logger.Log("SETTINGS", $"StartWithWindows={StartupToggle.IsOn}");
        DiagnosticFooterText.Text = StartupToggle.IsOn
            ? "UtilitaryBTSink configured to start minimized in the system tray with Windows."
            : "Automatic startup with Windows disabled.";
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowExit && _settings.MinimizeToTrayOnClose)
        {
            args.Cancel = true;
            ShowWindow(_hwnd, 0);
        }
    }

    public void RestoreFromTray()
    {
        ShowWindow(_hwnd, 9);
        SetForegroundWindow(_hwnd);
        Activate();
    }

    private void ExitApplication()
    {
        _logger.Log("APP", "Exiting UtilitaryBTSink from tray menu.");
        _allowExit = true;
        _logRefreshTimer.Stop();
        _watchdog.Dispose();
        _keepAlive.Dispose();
        _playback.Dispose();
        _discovery.Dispose();
        _tray.Dispose();
        _bulkConnectLock.Dispose();
        Application.Current.Exit();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
