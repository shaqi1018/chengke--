using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using VibrationMonitor.Models;

namespace VibrationMonitor.Services
{
    /// <summary>
    /// 命令请求-应答封装。命令写到 OUT 0x01，响应从 IN 0x84 回来。
    /// 响应内容与旧 CDC 完全一致（同一套 UsbCmd_* 生成），
    /// 故 <see cref="DataParser.ParseStatusResponse"/> 等逻辑可直接复用，
    /// 只是响应来源从串口换成 <see cref="IDeviceManager.ResponseReceived"/>。
    /// </summary>
    public sealed class DeviceCommander : IDisposable
    {
        private readonly IDeviceManager _usb;
        private readonly ConcurrentQueue<string> _responseBuffer = new();
        private readonly ManualResetEventSlim _responseReady = new(false);

        public event Action<string>? LogMessage;

        public DeviceCommander(IDeviceManager usb)
        {
            _usb = usb;
            _usb.ResponseReceived += OnResponseLine;
        }

        public string? SendAndWait(string command, int timeoutMs = 2000)
        {
            if (!_usb.IsConnected)
            {
                LogMessage?.Invoke("!! 设备未连接，跳过: " + command);
                return null;
            }
            if (!_usb.ResponseEndpointAvailable)
            {
                LogMessage?.Invoke("!! 响应端点不可用，跳过: " + command);
                return null;
            }

            try
            {
                _responseReady.Reset();
                while (_responseBuffer.TryDequeue(out _)) { }

                var sent = _usb.SendCommand(command);
                LogMessage?.Invoke(">> " + command + (sent ? "" : " (发送失败!)"));

                if (!sent) return null;

                if (_responseReady.Wait(timeoutMs))
                {
                    var lines = new List<string>();
                    while (_responseBuffer.TryDequeue(out var line))
                        lines.Add(line);

                    return string.Join("\n", lines);
                }

                LogMessage?.Invoke("!! 超时: " + command);
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Send(string command)
        {
            if (!_usb.IsConnected) return;
            _usb.SendCommand(command);
            LogMessage?.Invoke(">> " + command);
        }

        public string[]? SendAndCollect(string command, int timeoutMs = 3000, int settleMs = 200, bool silent = false)
        {
            if (!_usb.IsConnected) return null;
            if (!_usb.ResponseEndpointAvailable) return null;

            _responseReady.Reset();
            while (_responseBuffer.TryDequeue(out _)) { }

            _usb.SendCommand(command);
            if (!silent) LogMessage?.Invoke(">> " + command);

            var lines = new List<string>();
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < deadline)
            {
                if (_responseReady.Wait(settleMs))
                {
                    while (_responseBuffer.TryDequeue(out var line))
                        lines.Add(line);
                    _responseReady.Reset();
                }
            }

            // 收尾：取走剩余
            while (_responseBuffer.TryDequeue(out var remaining))
                lines.Add(remaining);

            if (lines.Count == 0 && !silent)
                LogMessage?.Invoke($"!! 超时（无响应）: {command}");

            return lines.Count > 0 ? lines.ToArray() : null;
        }

        public bool Ping() => SendAndWait("ping")?.Contains("pong") == true;

        public string? Help() => SendAndWait("help");

        public DeviceStatus? Status()
        {
            var lines = SendAndCollect("status", timeoutMs: 3000, settleMs: 200, silent: false);
            if (lines == null) return null;
            return DataParser.ParseStatusResponse(lines);
        }

        public bool AcqStart(string sink, int durationMs = 0)
        {
            var cmd = durationMs > 0 ? $"acq_start {sink} {durationMs}" : $"acq_start {sink}";
            // 无响应通道时 fire-and-forget；数据流出现即证明命令生效
            if (!_usb.ResponseEndpointAvailable)
            {
                Send(cmd);
                return true;
            }
            // 有响应通道：尝试等响应，但即使没等到也按成功处理
            // （数据流出现即证明命令生效；某些固件响应端点映射不同步时不应阻塞录制）
            var resp = SendAndWait(cmd, timeoutMs: 1500);
            if (resp == null)
                LogMessage?.Invoke("!! acq_start 未收到响应，按 fire-and-forget 继续（数据流为准）");
            return true;
        }

        public bool AcqStop()
        {
            if (!_usb.ResponseEndpointAvailable)
            {
                Send("acq_stop");
                return true;
            }
            var resp = SendAndWait("acq_stop", timeoutMs: 3000);
            return resp != null;
        }

        public bool SetSensorParam(string sensor, string param, string value)
        {
            var cmd = "s " + sensor + " " + param + " " + value;
            if (!_usb.ResponseEndpointAvailable)
            {
                Send(cmd);
                return true;
            }
            var resp = SendAndWait(cmd, timeoutMs: 3000);
            return resp != null;
        }

        /// <summary>麦克风配置：s mic &lt;gain|sr|en&gt; &lt;val&gt;。下次 acq_start 时随会话生效。</summary>
        public bool SetMicParam(string param, string value)
        {
            var cmd = "s mic " + param + " " + value;
            if (!_usb.ResponseEndpointAvailable)
            {
                Send(cmd);
                return true;
            }
            var resp = SendAndWait(cmd, timeoutMs: 3000);
            return resp != null;
        }

        public bool SetRtcTime(DateTime time)
        {
            var ts = time.ToString("yyyy-MM-ddTHH:mm:ss");
            var cmd = $"set_time {ts}";
            if (!_usb.ResponseEndpointAvailable)
            {
                Send(cmd);
                return true;
            }
            var resp = SendAndWait(cmd, timeoutMs: 2000);
            return resp != null;
        }

        public void Msc()
        {
            // WCID 固件命令为 boot_msc：切到 U 盘模式并复位（设备会断开重枚举）。
            Send("boot_msc");
        }

        private void OnResponseLine(string line)
        {
            try
            {
                _responseBuffer.Enqueue(line);
                _responseReady.Set();
            }
            catch { }
        }

        public void Dispose()
        {
            _usb.ResponseReceived -= OnResponseLine;
            _responseReady.Dispose();
        }
    }
}
