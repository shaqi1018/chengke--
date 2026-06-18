using System;

namespace VibrationMonitor.Services
{
    public interface IDeviceManager : IDisposable
    {
        event Action<string>? LsmLineReceived;
        event Action<string>? H3LineReceived;
        event Action<string>? QmaLineReceived;
        event Action<byte[], int>? MicDataReceived;
        event Action<string>? ResponseReceived;
        event Action<string>? ErrorOccurred;
        event Action<bool>? ConnectionChanged;
        event Action? Disconnected;

        string DeviceName { get; }
        bool IsConnected { get; }
        bool ResponseEndpointAvailable { get; }
        string? LsmRawDumpPath { get; set; }

        bool Connect();
        void Disconnect();
        bool SendCommand(string cmd);
    }
}
