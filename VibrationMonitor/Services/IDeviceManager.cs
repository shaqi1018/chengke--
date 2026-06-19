using System;

namespace VibrationMonitor.Services
{
    public interface IDeviceManager : IDisposable
    {
        event Action<string>? LsmLineReceived;
        event Action<string>? H3LineReceived;
        event Action<string>? QmaLineReceived;
        event Action<byte[], int>? MicDataReceived;
        /// <summary>AHT20 温湿度数据行（0x85 上 "aht," 前缀的包）</summary>
        event Action<string>? AhtLineReceived;
        /// <summary>LIS2MDL 磁力数据行（0x85 上 "mag," 前缀的包）</summary>
        event Action<string>? MagLineReceived;
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
