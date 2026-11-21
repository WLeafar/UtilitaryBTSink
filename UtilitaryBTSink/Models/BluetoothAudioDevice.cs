using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.Media.Audio;

namespace UtilitaryBTSink.Models;

public sealed class BluetoothAudioDevice : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private AudioPlaybackConnectionState _state = AudioPlaybackConnectionState.Closed;
    private bool _isAutoConnectEnabled;
    private bool _isConnecting;

    public required string Id { get; init; }

    public required string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public AudioPlaybackConnectionState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(ActionButtonText));
            }
        }
    }

    public bool IsAutoConnectEnabled
    {
        get => _isAutoConnectEnabled;
        set
        {
            if (_isAutoConnectEnabled != value)
            {
                _isAutoConnectEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set
        {
            if (_isConnecting != value)
            {
                _isConnecting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(ActionButtonText));
            }
        }
    }

    public DateTimeOffset? ConnectedSince { get; set; }

    public string StatusLabel
    {
        get
        {
            if (_isConnecting) return "Requesting A2DP connection...";
            return _state switch
            {
                AudioPlaybackConnectionState.Opened => "Connected (receiving audio)",
                AudioPlaybackConnectionState.Closed => "Paired (disconnected)",
                _ => "Unknown"
            };
        }
    }

    public string ActionButtonText
    {
        get
        {
            if (_isConnecting) return "Connecting...";
            return _state == AudioPlaybackConnectionState.Opened ? "Disconnect" : "Connect";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record StreamTelemetrySnapshot(
    int ActiveDevicesCount,
    string ActiveDevicesSummary,
    string ConnectionStatus,
    float PeakAmplitude,
    double SilenceSeconds,
    double PhantomStallSeconds,
    ulong AudioClockFrames,
    bool IsClockStalled,
    int KeepAlivePulsesSent,
    int AutoRecoveryCount,
    string LastDiagnosticMessage
);
