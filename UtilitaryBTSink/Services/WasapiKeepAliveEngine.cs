using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UtilitaryBTSink.Interop;
using UtilitaryBTSink.Models;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace UtilitaryBTSink.Services;

public sealed class WasapiKeepAliveEngine : IDisposable
{
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint CLSCTX_ALL = 23;
    private const float SubAudibleDitherAmplitude = 1.5e-5f;

    private sealed class MtaComWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private volatile bool _disposed;

        public MtaComWorker()
        {
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "WasapiMtaWorker"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        private void RunLoop()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); } catch { }
            }
        }

        public T Invoke<T>(Func<T> func)
        {
            if (_disposed) return default!;
            if (Thread.CurrentThread == _thread) return func();

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try { tcs.TrySetResult(func()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                });
            }
            catch (InvalidOperationException)
            {
                return default!;
            }
            return tcs.Task.GetAwaiter().GetResult();
        }

        public Task InvokeAsync(Action action)
        {
            if (_disposed) return Task.CompletedTask;
            if (Thread.CurrentThread == _thread)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try
                    {
                        action();
                        tcs.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                return Task.CompletedTask;
            }
            return tcs.Task;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join(1500);
            }
            _queue.Dispose();
        }
    }

    private readonly MtaComWorker _mta = new();
    private readonly DiagnosticLogger _logger;
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _renderDevice;
    private IAudioMeterInformation? _meterInfo;
    private IAudioClient? _audioClient;
    private IAudioRenderClient? _renderClient;
    private IAudioClock? _audioClock;
    private string _boundEndpointId = string.Empty;
    private uint _bufferFrameCount;
    private ushort _channels = 2;
    private ushort _bitsPerSample = 32;
    private bool _isFloatFormat = true;
    private bool _ditherSign;
    private volatile bool _keepAliveStreamActive;

    public bool IsKeepAliveStreamActive => _keepAliveStreamActive;
    public int PulsesSent { get; private set; }

    public WasapiKeepAliveEngine(DiagnosticLogger logger)
    {
        _logger = logger;
    }

    private bool EnsureMeterInitializedOnMta()
    {
        string currentDefaultId = string.Empty;
        try
        {
            currentDefaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        }
        catch
        {
            // Keep resilient if no default render endpoint is active at the moment
        }

        if (_meterInfo is not null &&
            !string.IsNullOrEmpty(currentDefaultId) &&
            string.Equals(currentDefaultId, _boundEndpointId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_meterInfo is not null &&
            !string.IsNullOrEmpty(currentDefaultId) &&
            !string.Equals(currentDefaultId, _boundEndpointId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Log("WASAPI", $"Default endpoint changed to {currentDefaultId}. Rebinding meter...");
            ResetInternalOnMta();
        }

        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            int hr = _enumerator.GetDefaultAudioEndpoint(
                EDataFlow.eRender,
                ERole.eMultimedia,
                out _renderDevice);

            if (hr != 0 || _renderDevice is null)
            {
                _logger.Log("WASAPI-ERR", $"GetDefaultAudioEndpoint failed: HRESULT=0x{hr:X8}");
                return false;
            }

            _boundEndpointId = currentDefaultId;
            var meterGuid = WasapiGuids.IID_IAudioMeterInformation;
            int meterHr = _renderDevice.Activate(ref meterGuid, CLSCTX_ALL, IntPtr.Zero, out var meterObj);
            if (meterHr != 0)
            {
                _logger.Log("WASAPI-ERR", $"Activate(IAudioMeterInformation) failed: HRESULT=0x{meterHr:X8}");
                return false;
            }

            _meterInfo = (IAudioMeterInformation)meterObj;
            _logger.Log("WASAPI", $"WASAPI meter ready ({Thread.CurrentThread.GetApartmentState()}) on DefaultRender={_boundEndpointId} (MeterHR=0x{meterHr:X8})");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("WASAPI-ERR", $"Exception in EnsureMeterInitialized: 0x{ex.HResult:X8} - {ex.Message}");
            ResetInternalOnMta();
            return false;
        }
    }

    private bool EnsureKeepAliveClientInitializedOnMta()
    {
        if (_audioClient is not null && _renderClient is not null) return true;
        if (!EnsureMeterInitializedOnMta() || _renderDevice is null) return false;

        try
        {
            var clientGuid = WasapiGuids.IID_IAudioClient;
            int hr = _renderDevice.Activate(ref clientGuid, CLSCTX_ALL, IntPtr.Zero, out var clientObj);
            if (hr != 0)
            {
                _logger.Log("WASAPI-ERR", $"Activate(IAudioClient) failed on {_boundEndpointId}: HRESULT=0x{hr:X8}");
                return false;
            }

            _audioClient = (IAudioClient)clientObj;
            hr = _audioClient.GetMixFormat(out IntPtr mixFormatPtr);
            if (hr != 0 || mixFormatPtr == IntPtr.Zero)
            {
                _logger.Log("WASAPI-ERR", $"GetMixFormat failed: HRESULT=0x{hr:X8}");
                StopKeepAliveStreamInternalOnMta();
                return false;
            }

            try
            {
                var fmt = Marshal.PtrToStructure<WaveFormatEx>(mixFormatPtr);
                _channels = fmt.nChannels > 0 ? fmt.nChannels : (ushort)2;
                _bitsPerSample = fmt.wBitsPerSample >= 8 ? fmt.wBitsPerSample : (ushort)16;
                _isFloatFormat = fmt.wFormatTag == 3 || fmt.wFormatTag == 0xFFFE;

                const long hundredMs = 1_000_000;
                hr = _audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.None,
                    hundredMs,
                    0,
                    mixFormatPtr,
                    IntPtr.Zero);

                if (hr != 0)
                {
                    _logger.Log("WASAPI-ERR", $"IAudioClient.Initialize failed: HRESULT=0x{hr:X8}");
                    StopKeepAliveStreamInternalOnMta();
                    return false;
                }

                _audioClient.GetBufferSize(out _bufferFrameCount);
                _logger.Log("WASAPI", $"Keep-Alive stream opened | {fmt.nSamplesPerSec}Hz {_channels}ch {_bitsPerSample}bit Tag=0x{fmt.wFormatTag:X} Buffer={_bufferFrameCount}frames");
            }
            finally
            {
                Marshal.FreeCoTaskMem(mixFormatPtr);
            }

            var renderGuid = WasapiGuids.IID_IAudioRenderClient;
            if (_audioClient.GetService(ref renderGuid, out var renderObj) != 0)
            {
                StopKeepAliveStreamInternalOnMta();
                return false;
            }
            _renderClient = (IAudioRenderClient)renderObj;

            var clockGuid = WasapiGuids.IID_IAudioClock;
            if (_audioClient.GetService(ref clockGuid, out var clockObj) == 0)
            {
                _audioClock = (IAudioClock)clockObj;
            }

            WriteZeroPaddingOnMta(_bufferFrameCount);
            _audioClient.Start();
            _keepAliveStreamActive = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("WASAPI-ERR", $"Exception in EnsureKeepAliveClientInitialized: 0x{ex.HResult:X8} - {ex.Message}");
            StopKeepAliveStreamInternalOnMta();
            return false;
        }
    }

    public async Task DumpAudioEndpointsDiagnosticsAsync(string reason)
    {
        try
        {
            var renderInfos = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            var captureInfos = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());

            string defaultRenderId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            _logger.Log("AUDIO-DUMP", $"=== Endpoint Diagnostics ({reason}) | DefaultRender={defaultRenderId} | Renders={renderInfos.Count} | Captures={captureInfos.Count} ===");

            foreach (var r in renderInfos)
            {
                string mark = string.Equals(r.Id, defaultRenderId, StringComparison.OrdinalIgnoreCase) ? " [DEFAULT]" : string.Empty;
                _logger.Log("AUDIO-RENDER", $"{r.Name}{mark} | Enabled={r.IsEnabled} | Id={r.Id}");
            }

            foreach (var c in captureInfos)
            {
                _logger.Log("AUDIO-CAPTURE", $"{c.Name} | Enabled={c.IsEnabled} | Id={c.Id}");
            }

            await _mta.InvokeAsync(DumpDefaultRenderSessionsOnMta);
        }
        catch (Exception ex)
        {
            _logger.Log("AUDIO-DUMP-ERR", $"Failed to enumerate audio endpoints: {ex.Message}");
        }
    }

    private void DumpDefaultRenderSessionsOnMta()
    {
        if (!EnsureMeterInitializedOnMta() || _renderDevice is null) return;

        try
        {
            var mgrGuid = WasapiGuids.IID_IAudioSessionManager2;
            int hr = _renderDevice.Activate(ref mgrGuid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj);
            if (hr != 0)
            {
                hr = _renderDevice.Activate(ref mgrGuid, CLSCTX_INPROC_SERVER, IntPtr.Zero, out mgrObj);
            }

            if (hr != 0 || mgrObj is not IAudioSessionManager2 mgr)
            {
                _logger.Log("AUDIO-SESS-ERR", $"Activate(IAudioSessionManager2) failed: HRESULT=0x{hr:X8}");
                return;
            }

            if (mgr.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum is null)
            {
                return;
            }

            if (sessionEnum.GetCount(out int count) != 0) return;
            _logger.Log("AUDIO-SESS", $"Active sessions on DefaultRender: {count}");

            int myPid = Environment.ProcessId;
            for (int i = 0; i < count; i++)
            {
                if (sessionEnum.GetSession(i, out var ctrl) != 0 || ctrl is null) continue;
                ctrl.GetState(out var state);
                ctrl.GetDisplayName(out string? displayName);

                uint pid = 0;
                string procName = "?";
                string sessId = string.Empty;
                if (ctrl is IAudioSessionControl2 ctrl2)
                {
                    ctrl2.GetProcessId(out pid);
                    ctrl2.GetSessionIdentifier(out string? rawId);
                    sessId = rawId ?? string.Empty;
                    try
                    {
                        procName = pid == 0 ? "System" : System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                    }
                    catch
                    {
                        procName = $"PID:{pid}";
                    }
                }

                float vol = -1f;
                bool muted = false;
                if (ctrl is ISimpleAudioVolume simpleVol)
                {
                    simpleVol.GetMasterVolume(out vol);
                    simpleVol.GetMute(out muted);
                    if ((int)pid == myPid && muted)
                    {
                        simpleVol.SetMute(false, IntPtr.Zero);
                        muted = false;
                        _logger.Log("AUDIO-SESS", $"Unmuted app's own WASAPI session (PID={pid})!");
                    }
                }

                float sessPeak = 0f;
                if (ctrl is IAudioMeterInformation sessMeter)
                {
                    sessMeter.GetPeakValue(out sessPeak);
                }

                _logger.Log("AUDIO-SESS", $"  [{i}] Proc={procName} (PID={pid}) | State={state} | Vol={vol:F2} | Muted={muted} | Peak={sessPeak:F4} | Name='{displayName ?? string.Empty}' | Sid={sessId}");
            }
        }
        catch (Exception ex)
        {
            _logger.Log("AUDIO-SESS-ERR", $"Exception listing WASAPI sessions: {ex.Message}");
        }
    }

    public (float Peak, ulong ClockPosition, bool ClockAvailable) SampleTelemetry()
        => _mta.Invoke(() =>
        {
            if (!EnsureMeterInitializedOnMta() || _meterInfo is null)
            {
                return (0f, 0UL, false);
            }

            float peak = 0f;
            int peakHr = _meterInfo.GetPeakValue(out float measured);
            if (peakHr == 0)
            {
                peak = measured;
            }
            else
            {
                _logger.Log("WASAPI-WARN", $"GetPeakValue failed (HRESULT=0x{peakHr:X8}). Rebinding on next poll.");
                ResetInternalOnMta();
                return (0f, 0UL, false);
            }

            ulong clockPos = 0;
            bool clockOk = false;
            if (_audioClock is not null && _audioClock.GetPosition(out ulong pos, out _) == 0)
            {
                clockPos = pos;
                clockOk = true;
            }

            return (peak, clockPos, clockOk);
        });

    public bool SendKeepAlivePulse(KeepAliveMode mode)
    {
        if (mode == KeepAliveMode.Disabled) return false;

        return _mta.Invoke(() =>
        {
            if (!EnsureKeepAliveClientInitializedOnMta() || _audioClient is null || _renderClient is null)
                return false;

            int padHr = _audioClient.GetCurrentPadding(out uint padding);
            if (padHr != 0)
            {
                _logger.Log("WASAPI-ERR", $"GetCurrentPadding failed: HRESULT=0x{padHr:X8}. Resetting Keep-Alive stream.");
                StopKeepAliveStreamInternalOnMta();
                return false;
            }

            uint framesAvailable = _bufferFrameCount > padding ? _bufferFrameCount - padding : 0;
            uint framesToWrite = Math.Min(framesAvailable, _bufferFrameCount);
            if (framesToWrite == 0) return true;

            if (mode == KeepAliveMode.SilentBufferFlag)
            {
                if (_renderClient.GetBuffer(framesToWrite, out _) == 0)
                {
                    _renderClient.ReleaseBuffer(framesToWrite, AudioClientBufferFlags.Silent);
                    PulsesSent++;
                    _logger.Log("KEEPALIVE", $"Pulse #{PulsesSent} (SilentBufferFlag) | Frames={framesToWrite}");
                    return true;
                }
                return false;
            }

            if (_renderClient.GetBuffer(framesToWrite, out IntPtr bufferPtr) == 0 && bufferPtr != IntPtr.Zero)
            {
                WriteSubAudibleDitherOnMta(bufferPtr, framesToWrite);
                _renderClient.ReleaseBuffer(framesToWrite, AudioClientBufferFlags.None);
                PulsesSent++;
                _logger.Log("KEEPALIVE", $"Pulse #{PulsesSent} (SubAudibleDither) | Frames={framesToWrite}");
                return true;
            }

            return false;
        });
    }

    public void StopKeepAliveStream()
    {
        if (!_keepAliveStreamActive) return;
        _ = _mta.InvokeAsync(() =>
        {
            if (_audioClient is not null)
            {
                _logger.Log("KEEPALIVE", "Real audio resumed; stopping temporary Keep-Alive stream.");
                StopKeepAliveStreamInternalOnMta();
            }
        });
    }

    private void WriteSubAudibleDitherOnMta(IntPtr bufferPtr, uint frameCount)
    {
        int totalSamples = (int)(frameCount * _channels);
        if (totalSamples <= 0) return;

        _ditherSign = !_ditherSign;
        float sample = _ditherSign ? SubAudibleDitherAmplitude : -SubAudibleDitherAmplitude;

        if (_isFloatFormat && _bitsPerSample == 32)
        {
            float[] pcm = new float[totalSamples];
            pcm[0] = sample;
            pcm[totalSamples - 1] = -sample;
            Marshal.Copy(pcm, 0, bufferPtr, totalSamples);
        }
        else
        {
            int bytes = totalSamples * (_bitsPerSample / 8);
            if (bytes <= 0) return;
            byte[] zeros = new byte[bytes];
            if (bytes >= 2) zeros[0] = 1;
            Marshal.Copy(zeros, 0, bufferPtr, bytes);
        }
    }

    private void WriteZeroPaddingOnMta(uint frames)
    {
        if (_renderClient is null || frames == 0) return;
        if (_renderClient.GetBuffer(frames, out _) == 0)
        {
            _renderClient.ReleaseBuffer(frames, AudioClientBufferFlags.Silent);
        }
    }

    public void ResetEndpointCache()
    {
        _ = _mta.InvokeAsync(() =>
        {
            _logger.Log("WASAPI", "ResetEndpointCache invoked.");
            ResetInternalOnMta();
        });
    }

    private void StopKeepAliveStreamInternalOnMta()
    {
        _keepAliveStreamActive = false;
        try { _audioClient?.Stop(); } catch { }
        _audioClock = null;
        _renderClient = null;
        _audioClient = null;
    }

    private void ResetInternalOnMta()
    {
        StopKeepAliveStreamInternalOnMta();
        _meterInfo = null;
        _renderDevice = null;
        _enumerator = null;
        _boundEndpointId = string.Empty;
    }

    public void Dispose()
    {
        try
        {
            _mta.Invoke(() =>
            {
                ResetInternalOnMta();
                return true;
            });
        }
        catch { }
        _mta.Dispose();
    }
}
