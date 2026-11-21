using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UtilitaryBTSink.Models;
using Windows.Media.Audio;

namespace UtilitaryBTSink.Services;

public sealed class AudioPlaybackService : IDisposable
{
    private static readonly TimeSpan OpenGracePeriod = TimeSpan.FromSeconds(6);

    private sealed class ActiveSession
    {
        public required BluetoothAudioDevice Device { get; init; }
        public AudioPlaybackConnection? Connection { get; set; }
        public bool IsOpening { get; set; }
        public DateTimeOffset? LastOpenSuccessUtc { get; set; }
        public TaskCompletionSource<bool>? OpenedWaiter { get; set; }
    }

    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _manuallyDisconnectedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDisconnectedUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _handshakeLock = new(1, 1);
    private readonly DiagnosticLogger _logger;
    private volatile bool _disposed;

    public event Action<string, AudioPlaybackConnectionState, bool, string>? DeviceStateChanged;
    public event Action<string>? DiagnosticLog;

    public AudioPlaybackService(DiagnosticLogger logger)
    {
        _logger = logger;
    }

    public int OpenedCount => _sessions.Values.Count(s => s.Connection?.State == AudioPlaybackConnectionState.Opened);

    public string GetActiveDevicesSummary()
    {
        var names = _sessions.Values
            .Where(s => s.Connection?.State == AudioPlaybackConnectionState.Opened)
            .Select(s => s.Device.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0 ? "None" : string.Join(", ", names);
    }

    public bool IsConnected(string deviceId)
        => _sessions.TryGetValue(deviceId, out var s) && s.Connection?.State == AudioPlaybackConnectionState.Opened;

    public bool IsConnectedOrPending(string deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var s) || s.Connection is null) return false;
        if (s.Connection.State == AudioPlaybackConnectionState.Opened) return true;
        if (s.IsOpening) return true;
        if (s.LastOpenSuccessUtc.HasValue && (DateTimeOffset.UtcNow - s.LastOpenSuccessUtc.Value) < OpenGracePeriod)
            return true;
        return false;
    }

    public bool IsManuallyDisconnected(string deviceId)
        => _manuallyDisconnectedIds.ContainsKey(deviceId);

    public void ClearManualDisconnect(string deviceId)
        => _manuallyDisconnectedIds.TryRemove(deviceId, out _);

    public async Task<bool> ConnectAsync(
        BluetoothAudioDevice device,
        bool isAutoRequest = false,
        CancellationToken ct = default)
    {
        if (_disposed) return false;

        if (!isAutoRequest)
        {
            _manuallyDisconnectedIds.TryRemove(device.Id, out _);
        }

        if (IsConnectedOrPending(device.Id))
        {
            return true;
        }

        var deviceLock = _deviceLocks.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await deviceLock.WaitAsync(ct);
        try
        {
            if (IsConnectedOrPending(device.Id))
            {
                return true;
            }

            // Serialize AVDTP/WinRT handshakes and allow full driver stabilization before starting the next device
            await _handshakeLock.WaitAsync(ct);
            try
            {
                bool ok = await ConnectInternalAsync(device, isAutoRequest, ct);
                if (ok)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(2200), ct);
                }
                return ok;
            }
            finally
            {
                _handshakeLock.Release();
            }
        }
        finally
        {
            deviceLock.Release();
        }
    }

    public async Task ResetAllOpenedEndpointsAsync(string reason, CancellationToken ct = default)
    {
        var activeDevices = _sessions.Values
            .Where(s => s.Connection is not null)
            .Select(s => s.Device)
            .ToList();

        _logger.Log("A2DP", $"ResetAllOpenedEndpointsAsync started | Reason={reason} | Targets={activeDevices.Count}");

        foreach (var device in activeDevices)
        {
            await ResetEndpointAsync(device.Id, reason, ct);
        }
    }

    public async Task<bool> ReconnectDeviceAsync(BluetoothAudioDevice device, string reason, CancellationToken ct = default)
    {
        if (_disposed) return false;
        _manuallyDisconnectedIds.TryRemove(device.Id, out _);

        var previouslyOpenedSiblings = _sessions.Values
            .Where(s => !string.Equals(s.Device.Id, device.Id, StringComparison.OrdinalIgnoreCase) &&
                        s.Connection?.State == AudioPlaybackConnectionState.Opened)
            .Select(s => s.Device)
            .ToList();

        var deviceLock = _deviceLocks.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await deviceLock.WaitAsync(ct);
        try
        {
            _logger.Log("A2DP", $"ReconnectDeviceAsync({device.Name}) | Reason={reason}");
            DiagnosticLog?.Invoke($"[A2DP] Restarting audio stream for {device.Name}...");
            await DisposeSessionConnectionAsync(device.Id);

            await _handshakeLock.WaitAsync(ct);
            try
            {
                bool ok = await ConnectInternalAsync(device, isAutoRequest: false, ct);
                if (ok)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(2200), ct);
                }

                // Restore any sibling stream dropped by the BthA2dp driver during renegotiation
                foreach (var sibling in previouslyOpenedSiblings)
                {
                    if (!IsConnectedOrPending(sibling.Id) && !IsManuallyDisconnected(sibling.Id))
                    {
                        _logger.Log("A2DP", $"Restoring sibling dropped during reconnect: {sibling.Name}");
                        await ConnectInternalAsync(sibling, isAutoRequest: true, ct);
                        await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
                    }
                }

                return ok;
            }
            finally
            {
                _handshakeLock.Release();
            }
        }
        finally
        {
            deviceLock.Release();
        }
    }

    public async Task<bool> ResetEndpointAsync(string deviceId, string reason, CancellationToken ct = default)
    {
        if (_disposed || !_sessions.TryGetValue(deviceId, out var session)) return false;
        return await ReconnectDeviceAsync(session.Device, reason, ct);
    }

    public async Task DisconnectAsync(string deviceId, bool isManual = true)
    {
        if (isManual)
        {
            _manuallyDisconnectedIds[deviceId] = 1;
        }

        var deviceLock = _deviceLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await deviceLock.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(deviceId, out var session))
            {
                string name = session.Device.Name;
                await DisposeSessionConnectionAsync(deviceId);
                _logger.Log("A2DP", $"DisconnectAsync({name}) | Manual={isManual} | RemainingOpened={OpenedCount}");
                DiagnosticLog?.Invoke($"[A2DP] Disconnected: {name}");
                DeviceStateChanged?.Invoke(deviceId, AudioPlaybackConnectionState.Closed, false, name);
            }
        }
        finally
        {
            deviceLock.Release();
        }
    }

    private async Task<bool> ConnectInternalAsync(BluetoothAudioDevice device, bool isAutoRequest, CancellationToken ct)
    {
        if (IsConnectedOrPending(device.Id))
        {
            _logger.Log("A2DP", $"ConnectInternalAsync({device.Name}) skipped: already connected or awaiting StateChanged.");
            return true;
        }

        await DisposeSessionConnectionAsync(device.Id);

        // Ensure the Windows BthA2dp driver finishes releasing the prior session before opening a new one
        if (_lastDisconnectedUtc.TryGetValue(device.Id, out var lastDisc))
        {
            var elapsed = DateTimeOffset.UtcNow - lastDisc;
            var minCooldown = TimeSpan.FromMilliseconds(2500);
            if (elapsed < minCooldown)
            {
                var wait = minCooldown - elapsed;
                _logger.Log("A2DP", $"Waiting {wait.TotalMilliseconds:F0}ms cooldown for BthA2dp to release {device.Name}...");
                await Task.Delay(wait, ct);
            }
        }

        _logger.Log("A2DP", $"ConnectAsync started: {device.Name} | Auto={isAutoRequest} | ActiveNow={OpenedCount} | Id={device.Id}");
        DeviceStateChanged?.Invoke(device.Id, AudioPlaybackConnectionState.Closed, true, device.Name);

        var sw = Stopwatch.StartNew();
        var conn = AudioPlaybackConnection.TryCreateFromId(device.Id);
        if (conn is null)
        {
            _logger.Log("A2DP-ERR", $"TryCreateFromId returned NULL for {device.Name} (Id={device.Id})");
            if (!isAutoRequest)
            {
                DiagnosticLog?.Invoke($"[A2DP] Unable to create connection for {device.Name}.");
            }
            DeviceStateChanged?.Invoke(device.Id, AudioPlaybackConnectionState.Closed, false, device.Name);
            return false;
        }

        _logger.Log("A2DP", $"TryCreateFromId OK for {device.Name} | InitialState={conn.State}");
        var openedWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.StateChanged += OnConnectionStateChanged;
        var session = new ActiveSession
        {
            Device = device,
            Connection = conn,
            IsOpening = true,
            OpenedWaiter = openedWaiter
        };
        _sessions[device.Id] = session;

        try
        {
            await conn.StartAsync().AsTask(ct);
            long startMs = sw.ElapsedMilliseconds;
            _logger.Log("A2DP", $"StartAsync OK for {device.Name} in {startMs}ms | State={conn.State}");

            var openResult = await conn.OpenAsync().AsTask(ct);
            long totalMs = sw.ElapsedMilliseconds;
            session.IsOpening = false;

            string extErr = openResult.ExtendedError is null
                ? "none"
                : $"0x{openResult.ExtendedError.HResult:X8} ({openResult.ExtendedError.Message})";

            _logger.Log("A2DP", $"OpenAsync({device.Name}) finished in {totalMs}ms | Status={openResult.Status} | State={conn.State} | ExtErr={extErr} | TotalOpened={OpenedCount}");

            if (openResult.Status == AudioPlaybackConnectionOpenResultStatus.Success)
            {
                session.LastOpenSuccessUtc = DateTimeOffset.UtcNow;

                if (conn.State != AudioPlaybackConnectionState.Opened)
                {
                    await Task.WhenAny(openedWaiter.Task, Task.Delay(TimeSpan.FromMilliseconds(3500), ct));
                    _logger.Log("A2DP", $"StateChanged confirmation({device.Name}) after OpenAsync: State={conn.State} in {sw.ElapsedMilliseconds}ms | TotalOpened={OpenedCount}");
                }

                DiagnosticLog?.Invoke($"[A2DP] Audio channel opened: {device.Name}");
                DeviceStateChanged?.Invoke(device.Id, conn.State, false, device.Name);
                return true;
            }

            if (!isAutoRequest)
            {
                DiagnosticLog?.Invoke($"[A2DP] {device.Name} rejected or is out of range ({openResult.Status}).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            session.IsOpening = false;
            _logger.Log("A2DP-ERR", $"Exception connecting {device.Name} in {sw.ElapsedMilliseconds}ms: 0x{ex.HResult:X8} - {ex.Message}");
            if (!isAutoRequest)
            {
                DiagnosticLog?.Invoke($"[A2DP] Error requesting connection for {device.Name}: {ex.Message}");
            }
        }

        await DisposeSessionConnectionAsync(device.Id);
        DeviceStateChanged?.Invoke(device.Id, AudioPlaybackConnectionState.Closed, false, device.Name);
        return false;
    }

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        foreach (var kvp in _sessions)
        {
            if (ReferenceEquals(kvp.Value.Connection, sender))
            {
                var newState = sender.State;
                if (newState == AudioPlaybackConnectionState.Opened)
                {
                    kvp.Value.OpenedWaiter?.TrySetResult(true);
                }
                else if (newState == AudioPlaybackConnectionState.Closed)
                {
                    kvp.Value.LastOpenSuccessUtc = null;
                }
                _logger.Log("A2DP-STATE", $"StateChanged: {kvp.Value.Device.Name} -> {newState} | TotalOpenedNow={OpenedCount}");
                DiagnosticLog?.Invoke($"[A2DP] {kvp.Value.Device.Name} -> {newState}");
                DeviceStateChanged?.Invoke(kvp.Key, newState, false, kvp.Value.Device.Name);
                break;
            }
        }
    }

    private async Task DisposeSessionConnectionAsync(string deviceId)
    {
        if (_sessions.TryRemove(deviceId, out var session) && session.Connection is not null)
        {
            var conn = session.Connection;
            session.Connection = null;
            session.IsOpening = false;
            session.LastOpenSuccessUtc = null;
            session.OpenedWaiter?.TrySetCanceled();
            conn.StateChanged -= OnConnectionStateChanged;
            _lastDisconnectedUtc[deviceId] = DateTimeOffset.UtcNow;

            // Brief pause before native Dispose to avoid racing WinRT StateChanged callbacks
            await Task.Delay(150);
            try
            {
                conn.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Log("A2DP-WARN", $"Dispose(AudioPlaybackConnection) ignored error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var id in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(id, out var session) && session.Connection is not null)
            {
                var conn = session.Connection;
                session.Connection = null;
                conn.StateChanged -= OnConnectionStateChanged;
                try { conn.Dispose(); } catch { }
            }
        }
        foreach (var sem in _deviceLocks.Values)
        {
            sem.Dispose();
        }
        _handshakeLock.Dispose();
    }
}
