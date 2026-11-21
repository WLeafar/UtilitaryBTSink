using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using UtilitaryBTSink.Models;
using UtilitaryBTSink.Services;
using UtilitaryBTSink.Views;

namespace UtilitaryBTSink;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\UtilitaryBTSink_SingleInstance_Mutex";
    private const string WakeEventName = "Local\\UtilitaryBTSink_WakeMainWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _wakeEvent;
    private MainWindow? _mainWindow;

    public DiagnosticLogger Logger { get; } = new();
    public SettingsStore SettingsStore { get; } = new();
    public BluetoothDiscoveryService DiscoveryService { get; }
    public AudioPlaybackService PlaybackService { get; }
    public WasapiKeepAliveEngine KeepAliveEngine { get; }
    public PhantomStreamWatchdog Watchdog { get; private set; } = null!;
    public StartupManager StartupManager { get; } = new();

    public App()
    {
        InitializeComponent();

        DiscoveryService = new BluetoothDiscoveryService(Logger);
        PlaybackService = new AudioPlaybackService(Logger);
        KeepAliveEngine = new WasapiKeepAliveEngine(Logger);

        UnhandledException += (_, e) =>
        {
            Logger.Log("CRASH-UI", $"Unhandled WinUI exception: {e.Message} | {e.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Log("CRASH-DOMAIN", $"Fatal unhandled exception: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Log("CRASH-TASK", $"Unobserved task exception: {e.Exception}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Signal the running instance to restore its window from the system tray
            if (EventWaitHandle.TryOpenExisting(WakeEventName, out var existingWake))
            {
                existingWake.Set();
                existingWake.Dispose();
            }
            Exit();
            return;
        }

        Logger.Log("APP", $"Starting UtilitaryBTSink | OS: {Environment.OSVersion} | 64-bit: {Environment.Is64BitProcess}");

        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
        var settings = SettingsStore.Load();

        Watchdog = new PhantomStreamWatchdog(DiscoveryService, PlaybackService, KeepAliveEngine, settings, Logger);

        var cmdArgs = Environment.GetCommandLineArgs();
        bool startMinimized = cmdArgs.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        Logger.Log("APP", $"Startup mode: {(startMinimized ? "Minimized to tray" : "Visible window")} | AutoReconnectOnStartup={settings.AutoReconnectOnStartup} | SavedDevices={settings.SavedDevices.Count}");

        _mainWindow = new MainWindow(
            settings,
            SettingsStore,
            DiscoveryService,
            PlaybackService,
            KeepAliveEngine,
            Watchdog,
            StartupManager,
            Logger,
            startMinimized);

        ListenForSecondInstanceWake();

        if (!startMinimized)
        {
            _mainWindow.Activate();
        }
        else
        {
            // In tray mode, initialize watchdog and auto-connect without showing the window
            _ = _mainWindow.InitializeBackgroundServicesAsync();
        }
    }

    private void ListenForSecondInstanceWake()
    {
        if (_wakeEvent is null || _mainWindow is null) return;

        ThreadPool.RegisterWaitForSingleObject(
            _wakeEvent,
            (_, _) => _mainWindow.DispatcherQueue.TryEnqueue(() => _mainWindow.RestoreFromTray()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }
}
