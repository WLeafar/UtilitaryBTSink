using System;
using System.Threading;
using System.Threading.Tasks;
using UtilitaryBTSink.Models;

namespace UtilitaryBTSink.Services;

public sealed class PhantomStreamWatchdog : IDisposable
{
    private const float SilenceAmplitudeThreshold = 0.0005f;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AutoConnectScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatLogInterval = TimeSpan.FromSeconds(15);

    private readonly BluetoothDiscoveryService _discoveryService;
    private readonly AudioPlaybackService _playbackService;
    private readonly WasapiKeepAliveEngine _keepAliveEngine;
    private readonly WatchdogSettings _settings;
    private readonly DiagnosticLogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private ulong _lastClockPosition;
    private ulong _clockAtLastPulse;
    private int _pulsesWithoutAdvance;
    private DateTimeOffset _lastAudioEnergyUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastKeepAlivePulseUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAutoConnectScanUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeatLogUtc = DateTimeOffset.MinValue;
    private bool _wasInSilence;
    private int _consecutiveResets;
    private int _totalRecoveries;
    private string _lastDiagnostic = "Monitoring and auto-connect active.";

    public event Action<StreamTelemetrySnapshot>? TelemetryUpdated;

    public PhantomStreamWatchdog(
        BluetoothDiscoveryService discoveryService,
        AudioPlaybackService playbackService,
        WasapiKeepAliveEngine keepAliveEngine,
        WatchdogSettings settings,
        DiagnosticLogger logger)
    {
        _discoveryService = discoveryService;
        _playbackService = playbackService;
        _keepAliveEngine = keepAliveEngine;
        _settings = settings;
        _logger = logger;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _logger.Log("WATCHDOG", $"Started | SilenceThreshold={_settings.SilenceThresholdSeconds}s | PhantomTimeout={_settings.PhantomTimeoutSeconds}s | KeepAliveMode={_settings.KeepAliveMode}");
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunMonitorLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunMonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;

            if (_settings.AutoReconnectOnStartup &&
                (now - _lastAutoConnectScanUtc) >= AutoConnectScanInterval)
            {
                _lastAutoConnectScanUtc = now;
                await RequestAutoConnectForSavedDevicesAsync(ct);
            }

            int openedCount = _playbackService.OpenedCount;
            string activeSummary = _playbackService.GetActiveDevicesSummary();
            var (peak, clockPos, clockOk) = _keepAliveEngine.SampleTelemetry();

            if (openedCount == 0)
            {
                _keepAliveEngine.StopKeepAliveStream();
                _lastAudioEnergyUtc = now;
                _lastClockPosition = 0;
                _clockAtLastPulse = 0;
                _pulsesWithoutAdvance = 0;
                _wasInSilence = false;
                EmitSnapshot(0, "None", "Waiting for devices", peak, 0, 0, clockPos, false);
                continue;
            }

            // Real audio detected on default endpoint: stream is healthy, never trigger false alarm
            if (peak > SilenceAmplitudeThreshold)
            {
                if (_wasInSilence)
                {
                    _logger.Log("WATCHDOG", $"Audio detected again (Peak={peak:F5}) | Active={activeSummary}");
                    _wasInSilence = false;
                }
                _keepAliveEngine.StopKeepAliveStream();
                _lastAudioEnergyUtc = now;
                _pulsesWithoutAdvance = 0;
                _consecutiveResets = 0;
            }

            double silenceSeconds = (now - _lastAudioEnergyUtc).TotalSeconds;
            if (silenceSeconds >= 3.0 && !_wasInSilence)
            {
                _wasInSilence = true;
                _logger.Log("WATCHDOG", $"Silence detected on default endpoint (Peak={peak:F6}) | Active={activeSummary}");
            }

            if (silenceSeconds >= _settings.SilenceThresholdSeconds)
            {
                // Keep the WASAPI stream fed during extended silence and verify clock progress across pulses
                if ((now - _lastKeepAlivePulseUtc).TotalSeconds >= _settings.KeepAliveIntervalSeconds ||
                    !_keepAliveEngine.IsKeepAliveStreamActive)
                {
                    if (_keepAliveEngine.IsKeepAliveStreamActive && clockOk && _lastKeepAlivePulseUtc != DateTimeOffset.MinValue)
                    {
                        if (clockPos > _clockAtLastPulse)
                        {
                            _pulsesWithoutAdvance = 0;
                        }
                        else
                        {
                            _pulsesWithoutAdvance++;
                        }
                    }

                    if (_keepAliveEngine.SendKeepAlivePulse(_settings.KeepAliveMode))
                    {
                        _lastKeepAlivePulseUtc = now;
                        _clockAtLastPulse = clockPos;
                        _lastDiagnostic = $"Keep-Alive pulse ({_settings.KeepAliveMode}) sent after {silenceSeconds:F0}s of silence.";
                    }
                }
            }

            if (clockPos > _lastClockPosition)
            {
                _lastClockPosition = clockPos;
            }

            if ((now - _lastHeartbeatLogUtc) >= HeartbeatLogInterval)
            {
                _lastHeartbeatLogUtc = now;
                _logger.Log("HEARTBEAT", $"Opened={openedCount} ({activeSummary}) | Peak={peak:F5} | KeepAliveActive={_keepAliveEngine.IsKeepAliveStreamActive} | Clock={clockPos} | Silence={silenceSeconds:F1}s | Pulses={_keepAliveEngine.PulsesSent}");
            }

            bool clockStalled = _pulsesWithoutAdvance >= 3;
            if (clockStalled)
            {
                _consecutiveResets++;
                if (_consecutiveResets <= _settings.MaxConsecutiveResets)
                {
                    _totalRecoveries++;
                    _logger.Log("WATCHDOG-REC", $"WASAPI engine stalled across 3 Keep-Alive pulses. Resetting WASAPI cache only ({_consecutiveResets}/{_settings.MaxConsecutiveResets})...");
                    _lastDiagnostic = "Resetting WASAPI cache after clock stall between Keep-Alive pulses...";
                    _keepAliveEngine.ResetEndpointCache();
                    _pulsesWithoutAdvance = 0;
                    _lastClockPosition = 0;
                    _clockAtLastPulse = 0;
                    continue;
                }
                else
                {
                    _logger.Log("WATCHDOG-ERR", "WASAPI circuit-breaker tripped. Waiting 30s.");
                    _lastDiagnostic = "WASAPI circuit-breaker active. Waiting 30s.";
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    _consecutiveResets = 0;
                    _pulsesWithoutAdvance = 0;
                }
            }

            EmitSnapshot(
                openedCount,
                activeSummary,
                $"{openedCount} active (Opened)",
                peak,
                silenceSeconds,
                0,
                clockPos,
                clockStalled);
        }
    }

    private async Task RequestAutoConnectForSavedDevicesAsync(CancellationToken ct)
    {
        var devices = _discoveryService.GetDevicesSnapshot();
        foreach (var device in devices)
        {
            if (ct.IsCancellationRequested) break;
            if (!_settings.IsAutoConnectEnabled(device.Id)) continue;
            if (_playbackService.IsConnectedOrPending(device.Id)) continue;
            if (_playbackService.IsManuallyDisconnected(device.Id)) continue;

            _logger.Log("AUTO-CONNECT", $"Watchdog requesting auto-connect for saved paired device: {device.Name}");
            _lastDiagnostic = $"Requesting auto-connect for paired device: {device.Name}...";
            _ = await _playbackService.ConnectAsync(device, isAutoRequest: true, ct);
        }
    }

    private void EmitSnapshot(
        int activeCount,
        string activeSummary,
        string status,
        float peak,
        double silenceSeconds,
        double stallSeconds,
        ulong clockFrames,
        bool isStalled)
    {
        TelemetryUpdated?.Invoke(new StreamTelemetrySnapshot(
            activeCount,
            activeSummary,
            status,
            peak,
            silenceSeconds,
            stallSeconds,
            clockFrames,
            isStalled,
            _keepAliveEngine.PulsesSent,
            _totalRecoveries,
            _lastDiagnostic));
    }

    public void Dispose() => Stop();
}
