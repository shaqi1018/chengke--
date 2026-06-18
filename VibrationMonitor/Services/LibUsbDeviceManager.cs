using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace VibrationMonitor.Services
{
    public class LibUsbDeviceManager : IDeviceManager
    {
        private const int VID = 0x0483;
        private const int PID = 0x5721;
        private const byte EP_LSM_IN = 0x81;
        private const byte EP_H3_IN = 0x82;
        private const byte EP_QMA_IN = 0x83;
        private const byte EP_MIC_IN = 0x84;
        private const byte EP_RESP_IN = 0x85;
        private const byte EP_CMD_OUT = 0x01;

        private UsbContext? _context;
        private IUsbDevice? _device;
        private Thread? _lsmThread;
        private Thread? _h3Thread;
        private Thread? _qmaThread;
        private Thread? _micThread;
        private Thread? _respThread;

        private volatile bool _running;

        public event Action<string>? LsmLineReceived;
        public event Action<string>? H3LineReceived;
        public event Action<string>? QmaLineReceived;
        public event Action<byte[], int>? MicDataReceived;
        public event Action<string>? ResponseReceived;
        public event Action<string>? ErrorOccurred;
        public event Action<bool>? ConnectionChanged;
        public event Action? Disconnected;

        public string DeviceName { get; private set; } = "";
        public bool IsConnected => _device != null && _running;
        public bool ResponseEndpointAvailable { get; private set; }

        // 端点状态
        private class EndpointState
        {
            public string Name = "";
            public BlockingCollection<byte[]>? Queue;
            public List<byte> Buffer = new();
            public Action<string>? OnLine;
            public Thread? Consumer;
        }

        private readonly EndpointState _lsmState = new() { Name = "LSM" };
        private readonly EndpointState _h3State = new() { Name = "H3" };
        private readonly EndpointState _qmaState = new() { Name = "QMA" };

        // 原始数据转储路径
        public string? LsmRawDumpPath { get; set; }

        public bool Connect()
        {
            Disconnect();
            try
            {
                // 创建USB上下文
                _context = new UsbContext();
                _context.SetDebugLevel(LogLevel.Info);

                // 查找设备
                var devices = _context.List();
                _device = devices.FirstOrDefault(d => d.VendorId == VID && d.ProductId == PID);

                if (_device == null)
                {
                    ErrorOccurred?.Invoke($"未找到设备 {VID:X4}:{PID:X4} - 请确认已使用Zadig切换到libusbK驱动");
                    _context?.Dispose();
                    _context = null;
                    return false;
                }

                // 打开设备
                _device.Open();

                // Claim Interface 0
                _device.ClaimInterface(0);

                _running = true;
                ResponseEndpointAvailable = true;

                // 启动LSM端点（生产者+消费者模式）
                _lsmState.OnLine = l => LsmLineReceived?.Invoke(l);
                _lsmState.Buffer.Clear();
                try { _lsmState.Queue?.Dispose(); } catch { }
                _lsmState.Queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());

                _lsmThread = new Thread(() => ReadLoop(EP_LSM_IN, "LSM",
                    chunk => { try { _lsmState.Queue?.Add(chunk); } catch { } }))
                    { IsBackground = true, Name = "LibUsbRead_LSM", Priority = ThreadPriority.Highest };
                _lsmThread.Start();

                _lsmState.Consumer = new Thread(() => ConsumeLoop(_lsmState))
                    { IsBackground = true, Name = "LibUsbConsume_LSM" };
                _lsmState.Consumer.Start();

                // 启动H3端点
                _h3State.OnLine = l => H3LineReceived?.Invoke(l);
                _h3State.Buffer.Clear();
                try { _h3State.Queue?.Dispose(); } catch { }
                _h3State.Queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());

                _h3Thread = new Thread(() => ReadLoop(EP_H3_IN, "H3",
                    chunk => { try { _h3State.Queue?.Add(chunk); } catch { } }))
                    { IsBackground = true, Name = "LibUsbRead_H3" };
                _h3Thread.Start();

                _h3State.Consumer = new Thread(() => ConsumeLoop(_h3State))
                    { IsBackground = true, Name = "LibUsbConsume_H3" };
                _h3State.Consumer.Start();

                // 启动QMA端点
                _qmaState.OnLine = l => QmaLineReceived?.Invoke(l);
                _qmaState.Buffer.Clear();
                try { _qmaState.Queue?.Dispose(); } catch { }
                _qmaState.Queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());

                _qmaThread = new Thread(() => ReadLoop(EP_QMA_IN, "QMA",
                    chunk => { try { _qmaState.Queue?.Add(chunk); } catch { } }))
                    { IsBackground = true, Name = "LibUsbRead_QMA" };
                _qmaThread.Start();

                _qmaState.Consumer = new Thread(() => ConsumeLoop(_qmaState))
                    { IsBackground = true, Name = "LibUsbConsume_QMA" };
                _qmaState.Consumer.Start();

                // 启动麦克风端点（纯二进制）
                _micThread = new Thread(() => ReadLoop(EP_MIC_IN, "MIC",
                    chunk => MicDataReceived?.Invoke(chunk, chunk.Length)))
                    { IsBackground = true, Name = "LibUsbRead_MIC" };
                _micThread.Start();

                // 启动响应端点
                _respThread = new Thread(ReadLoopResp)
                    { IsBackground = true, Name = "LibUsbRead_RESP" };
                _respThread.Start();

                DeviceName = "Sensor WCID Bulk (libusb)";
                ConnectionChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"连接失败: {ex.Message}");
                Disconnect();
                return false;
            }
        }

        private void ReadLoop(byte endpoint, string name, Action<byte[]> onData)
        {
            if (_device == null) return;

            byte[] buf = new byte[65536];
            FileStream? rawDump = null;

            try
            {
                var reader = _device.OpenEndpointReader((ReadEndpointID)endpoint);

                while (_running)
                {
                    // libusb 3.0读取（返回读取的字节数通过out参数）
                    reader.Read(buf, 1000, out int transferred);

                    if (transferred > 0)
                    {
                        byte[] chunk = new byte[transferred];
                        Array.Copy(buf, chunk, transferred);

                        // LSM原始数据转储
                        if (endpoint == EP_LSM_IN)
                        {
                            var p = LsmRawDumpPath;
                            if (p != null && rawDump == null)
                            {
                                try { rawDump = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read, 65536); } catch { }
                            }
                            else if (p == null && rawDump != null)
                            {
                                try { rawDump.Dispose(); } catch { }
                                rawDump = null;
                            }
                            if (rawDump != null)
                                try { rawDump.Write(chunk, 0, chunk.Length); } catch { }
                        }

                        onData(chunk);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                    ErrorOccurred?.Invoke($"端点 0x{endpoint:X2}({name}) 异常: {ex.Message}");
            }
            finally
            {
                try { rawDump?.Dispose(); } catch { }
            }
        }

        private void ConsumeLoop(EndpointState st)
        {
            try
            {
                foreach (var chunk in st.Queue!.GetConsumingEnumerable())
                {
                    st.Buffer.AddRange(chunk);

                    // 按行切分
                    while (true)
                    {
                        int idx = st.Buffer.IndexOf((byte)'\n');
                        if (idx < 0) break;

                        var lineBytes = st.Buffer.GetRange(0, idx).ToArray();
                        st.Buffer.RemoveRange(0, idx + 1);

                        string line = Encoding.ASCII.GetString(lineBytes).TrimEnd('\r');
                        if (!string.IsNullOrWhiteSpace(line))
                            st.OnLine?.Invoke(line);
                    }
                }
            }
            catch { }
        }

        private void ReadLoopResp()
        {
            if (_device == null) return;

            byte[] buf = new byte[4096];
            var buffer = new List<byte>();

            try
            {
                var reader = _device.OpenEndpointReader((ReadEndpointID)EP_RESP_IN);

                while (_running)
                {
                    reader.Read(buf, 200, out int transferred);

                    if (transferred > 0)
                    {
                        buffer.AddRange(buf.Take(transferred));

                        // 按行切分
                        while (true)
                        {
                            int idx = buffer.IndexOf((byte)'\n');
                            if (idx < 0) break;

                            var lineBytes = buffer.GetRange(0, idx).ToArray();
                            buffer.RemoveRange(0, idx + 1);

                            string line = Encoding.UTF8.GetString(lineBytes).TrimEnd('\r');
                            if (!string.IsNullOrWhiteSpace(line))
                                ResponseReceived?.Invoke(line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                    ErrorOccurred?.Invoke($"响应端点异常: {ex.Message}");
            }
        }

        public bool SendCommand(string cmd)
        {
            if (_device == null || !_running) return false;

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(cmd + "\n");
                var writer = _device.OpenEndpointWriter((WriteEndpointID)EP_CMD_OUT);
                writer.Write(data, 500, out int transferred);
                return transferred == data.Length;
            }
            catch
            {
                return false;
            }
        }

        public void Disconnect()
        {
            _running = false;
            ResponseEndpointAvailable = false;

            // 等待读取线程结束
            _lsmThread?.Join(600);
            _h3Thread?.Join(600);
            _qmaThread?.Join(600);
            _micThread?.Join(600);
            _respThread?.Join(600);

            // 关闭队列
            try { _lsmState.Queue?.CompleteAdding(); } catch { }
            try { _h3State.Queue?.CompleteAdding(); } catch { }
            try { _qmaState.Queue?.CompleteAdding(); } catch { }

            // 等待消费线程结束
            _lsmState.Consumer?.Join(600);
            _h3State.Consumer?.Join(600);
            _qmaState.Consumer?.Join(600);

            // 释放资源
            try
            {
                _device?.Close();
                _device = null;
            }
            catch { }

            try
            {
                _context?.Dispose();
                _context = null;
            }
            catch { }

            DeviceName = "";
            Disconnected?.Invoke();
            ConnectionChanged?.Invoke(false);
        }

        public void ClearBuffers()
        {
            // 清空所有端点的内部缓冲区
            lock (_lsmState.Buffer) { _lsmState.Buffer.Clear(); }
            lock (_h3State.Buffer) { _h3State.Buffer.Clear(); }
            lock (_qmaState.Buffer) { _qmaState.Buffer.Clear(); }
        }

        public void Dispose()
        {
            Disconnect();
            try { _lsmState.Queue?.Dispose(); } catch { }
            try { _h3State.Queue?.Dispose(); } catch { }
            try { _qmaState.Queue?.Dispose(); } catch { }
        }
    }
}
