using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VibrationMonitor.Services
{
    /// <summary>
    /// WCID / WinUSB Bulk 传输层（纯 P/Invoke，不依赖 LibUsbDotNet）。
    ///
    /// 通过 GUID_DEVINTERFACE_USB_DEVICE 枚举，过滤 VID_0483 PID_5721，
    /// 用 WinUsb_Initialize 直接获取 WinUSB 句柄，全程调用 winusb.dll。
    /// </summary>
    public sealed class WinUsbDeviceManager : IDeviceManager
    {
        public const int Vid = 0x0483;
        public const int Pid = 0x5721;

        /// <summary>
        /// 旧固件兼容模式。true = 3 端点旧固件(无麦克风, RESP=0x84)；
        /// false = 4/5 端点新固件(MIC=0x84, RESP=0x85)。
        /// 旧固件模式下不读 MIC 端点。
        /// </summary>
        public static bool LegacyFirmwareMode = false;  // 4端点终版固件: MIC=0x84, RESP=0x85

        /// <summary>
        /// 跳过 MIC 端点读取（用于 5 端点固件但暂未开启 mic-over-USB 的测试，
        /// 避免读 0x84 空转超时干扰）。环境变量 SKIP_MIC=1 启用。
        /// </summary>
        public static bool SkipMicEndpoint = false;

        // 端点地址。LSM/H3/QMA 两版一致；MIC/RESP 随固件版本变。
        private const byte EP_LSM_IN  = 0x81;
        private const byte EP_H3_IN   = 0x82;
        private const byte EP_QMA_IN  = 0x83;   // 纯净 CSV，无 tag
        private const byte EP_MIC_IN  = 0x84;   // 新固件麦克风 PCM；旧固件无此端点
        // 响应端点：新固件 0x85，旧固件 0x84
        private static byte EP_RESP_IN => LegacyFirmwareMode ? (byte)0x84 : (byte)0x85;
        private const byte EP_CMD_OUT = 0x01;

        // 读端点 100ms 超时，响应端点 500ms 超时（等命令返回）
        private const uint READ_TIMEOUT_MS = 100;
        private const uint RESP_TIMEOUT_MS = 500;

        private SafeFileHandle? _fileHandle;
        private IntPtr _winUsbHandle = IntPtr.Zero;

        private Thread? _lsmThread, _h3Thread, _qmaThread, _micThread, _respThread;
        private volatile bool _running;
        private int _errorFired;
        private readonly object _writeLock = new();

        private readonly EndpointReadState _lsmState  = new("LSM");
        private readonly EndpointReadState _h3State   = new("H3");
        private readonly EndpointReadState _qmaState  = new("QMA");
        private readonly EndpointReadState _respState = new("RESP");

        public event Action<string>? LsmLineReceived;
        public event Action<string>? H3LineReceived;
        public event Action<string>? QmaLineReceived;
        public event Action<string>? ResponseReceived;
        /// <summary>AHT20 温湿度数据行（0x85 上以 "aht," 开头的包，从命令响应中分流）</summary>
        public event Action<string>? AhtLineReceived;
        /// <summary>LIS2MDL 磁力数据行（0x85 上以 "mag," 开头的包，从命令响应中分流）</summary>
        public event Action<string>? MagLineReceived;
        /// <summary>麦克风原始 PCM（16-bit 小端，单声道）。参数为本次 bulk 传输的字节缓冲与有效长度。</summary>
        public event Action<byte[], int>? MicDataReceived;
        public event Action<string>? RawDataReceived;
        public event Action? Disconnected;
        public event Action<string>? ErrorOccurred;
        public event Action<bool>? ConnectionChanged;

        public bool IsConnected => _running && _winUsbHandle != IntPtr.Zero;
        public string? DeviceName { get; private set; }
        /// <summary>命令响应端点 0x85 是否可用（通过 GUID_DEVINTERFACE_USB_DEVICE 打开时可能不可用）</summary>
        public bool ResponseEndpointAvailable { get; private set; } = true;

        // 诊断：非 null 时，LSM 读线程把 ReadPipe 拿到的原始字节原样写入此文件（不切行/不解析）。
        public string? LsmRawDumpPath { get; set; }

        // ──────────────────────────────────────────────────────────
        //  设备扫描 / 连接 / 断开
        // ──────────────────────────────────────────────────────────

        public static List<(string Id, string Description)> ScanDevices()
        {
            var result = new List<(string, string)>();
            try
            {
                foreach (var path in FindDevicePaths())
                    result.Add(("WCID#0",
                        $"Sensor WCID Bulk (VID {Vid:X4} / PID {Pid:X4})"));
            }
            catch { }
            return result;
        }

        public bool Connect()
        {
            Disconnect(fireEvent: false);
            try
            {
                var paths = FindDevicePaths();
                if (paths.Count == 0)
                {
                    ErrorOccurred?.Invoke(
                        $"未找到 WCID 设备 (VID {Vid:X4} / PID {Pid:X4})");
                    return false;
                }
                _fileHandle = NativeMethods.CreateFile(
                    paths[0],
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);

                if (_fileHandle.IsInvalid)
                {
                    int e = Marshal.GetLastWin32Error();
                    ErrorOccurred?.Invoke($"连接失败: CreateFile 错误 {e}");
                    return false;
                }

                if (!NativeMethods.WinUsb_Initialize(_fileHandle, out _winUsbHandle))
                {
                    int e = Marshal.GetLastWin32Error();
                    ErrorOccurred?.Invoke($"连接失败: WinUsb_Initialize 错误 {e}");
                    CleanHandles();
                    return false;
                }

                // 数据端点使用无限超时（0=等待直到有数据），避免空轮询浪费CPU
                SetPipeTimeout(EP_LSM_IN,  0);
                SetPipeTimeout(EP_H3_IN,   0);
                SetPipeTimeout(EP_QMA_IN,  0);
                // 旧固件模式下 0x84 是响应端点(非MIC)，不能当数据端点配置
                if (!LegacyFirmwareMode && !SkipMicEndpoint)
                    SetPipeTimeout(EP_MIC_IN,  0);
                // 响应端点改用 overlapped 多缓冲读（消除 100Hz MAG 在浅队列上的偶发丢包），
                // 配合无限超时(0)避免空转 churn；不开 RAW_IO 以保留任意读长（无最大包整除约束）。
                SetPipeTimeout(EP_RESP_IN, 0);
                SetPipeTimeout(EP_CMD_OUT, 500);

                // 启用RAW_IO策略：绕过WinUSB排队，直接传递给USB驱动栈（高性能模式）。
                // 仅对真正的数据端点启用；旧固件/跳过MIC时 0x84 是响应端点，绝不能开 RAW_IO。
                EnableRawIO(EP_LSM_IN);
                EnableRawIO(EP_H3_IN);
                EnableRawIO(EP_QMA_IN);
                if (!LegacyFirmwareMode && !SkipMicEndpoint)
                    EnableRawIO(EP_MIC_IN);

                _errorFired = 0;
                _running = true;
                ResponseEndpointAvailable = true;

                // LSM端点：使用overlapped异步读取
                _lsmState.OnLine = l => LsmLineReceived?.Invoke(l);
                _lsmState.Buffer.Clear();
                try { _lsmState.Queue?.Dispose(); } catch { }
                _lsmState.Queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());

                _lsmThread = new Thread(() => ReadLoopOverlapped(EP_LSM_IN, 65536, "LSM",
                    chunk => { try { _lsmState.Queue?.Add(chunk); } catch { } }))
                    { IsBackground = true, Name = "WinUsbRead_LSM", Priority = ThreadPriority.Highest };
                _lsmThread.Start();

                // 启动消费线程(切行→解析→落盘)
                _lsmState.Consumer = new Thread(() => ConsumeLoop(_lsmState))
                    { IsBackground = true, Name = "WinUsbConsume_LSM" };
                _lsmState.Consumer.Start();

                // 其他端点也使用overlapped
                _h3Thread   = StartReader(EP_H3_IN,   _h3State,   l => H3LineReceived?.Invoke(l));
                _qmaThread  = StartReader(EP_QMA_IN,  _qmaState,  l => QmaLineReceived?.Invoke(l));

                // EP4(0x84) 麦克风：overlapped 多缓冲读，纯二进制 PCM。
                // 旧固件无麦克风端点（0x84 是响应端点）；5端点固件暂未开 mic 时也跳过，避免空转超时。
                if (!LegacyFirmwareMode && !SkipMicEndpoint)
                {
                    _micThread  = new Thread(() => ReadLoopOverlapped(EP_MIC_IN, 4096, "MIC",
                        chunk => MicDataReceived?.Invoke(chunk, chunk.Length)))
                        { IsBackground = true, Name = "WinUsbReader_MIC" };
                    _micThread.Start();
                }

                // EP5(0x85) 命令响应读循环：overlapped 多缓冲，UTF-8 解码，逐行分流
                _respThread = new Thread(ReadLoopRespOverlapped) { IsBackground = true, Name = "WinUsbReader_RESP" };
                _respThread.Start();

                DeviceName = "Sensor WCID Bulk";
                ConnectionChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("连接失败: " + ex.Message);
                Disconnect(fireEvent: false);
                return false;
            }
        }

        public void Disconnect(bool fireEvent = true)
        {
            DisconnectInternal(fireEvent);
        }

        // 显式实现接口
        void IDeviceManager.Disconnect()
        {
            DisconnectInternal(true);
        }

        private void DisconnectInternal(bool fireEvent)
        {
            _running = false;

            // 先中止挂起的 I/O，让读线程能退出
            if (_winUsbHandle != IntPtr.Zero)
            {
                foreach (var ep in new byte[] { EP_LSM_IN, EP_H3_IN, EP_QMA_IN, EP_MIC_IN, EP_RESP_IN })
                {
                    try { NativeMethods.WinUsb_AbortPipe(_winUsbHandle, ep); } catch { }
                }
            }

            foreach (var t in new[] { _lsmThread, _h3Thread, _qmaThread, _micThread, _respThread })
            {
                try { t?.Join(600); } catch { }
            }
            _lsmThread = _h3Thread = _qmaThread = _micThread = _respThread = null;

            // 读线程已停，通知消费线程把剩余队列收完后退出
            foreach (var st in new[] { _lsmState, _h3State, _qmaState })
            {
                try { st.Queue?.CompleteAdding(); } catch { }
            }
            foreach (var st in new[] { _lsmState, _h3State, _qmaState })
            {
                try { st.Consumer?.Join(600); } catch { }
                st.Consumer = null;
            }

            CleanHandles();
            DeviceName = null;
            if (fireEvent) ConnectionChanged?.Invoke(false);
        }

        public bool SendCommand(string command)
        {
            if (!IsConnected) return false;
            lock (_writeLock)
            {
                try
                {
                    var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
                    bool ok = NativeMethods.WinUsb_WritePipe(
                        _winUsbHandle, EP_CMD_OUT,
                        bytes, (uint)bytes.Length,
                        out uint transferred, IntPtr.Zero);
                    return ok && transferred == (uint)bytes.Length;
                }
                catch { return false; }
            }
        }

        public void ClearBuffers()
        {
            _lsmState.RequestClear();
            _h3State.RequestClear();
            _qmaState.RequestClear();
        }

        public void Dispose() => Disconnect();

        // ──────────────────────────────────────────────────────────
        //  内部：设备路径查找
        // ──────────────────────────────────────────────────────────

        // 优先 WinUSB 类 GUID（Zadig 安装后可用，全部端点含 0x84 均可访问）；
        // 回退通用 USB 设备 GUID（总可枚举，但 0x84 可能因 err=87 不可用）。
        private static readonly Guid WinUsbClassGuid =
            new Guid("{88bae032-5a81-49f0-bc3d-a4ff138216d6}");
        private static readonly Guid UsbDeviceGuid =
            new Guid("{a5dcbf10-6530-11d2-901f-00c04fb951ed}");

        private static List<string> FindDevicePaths()
        {
            foreach (var guid in new[] { WinUsbClassGuid, UsbDeviceGuid })
            {
                var paths = EnumPaths(guid);
                if (paths.Count > 0) return paths;
            }
            return new List<string>();
        }

        private static List<string> EnumPaths(Guid searchGuid)
        {
            var result = new List<string>();
            var guidCopy = searchGuid;

            IntPtr hInfo = NativeMethods.SetupDiGetClassDevs(
                ref guidCopy, null, IntPtr.Zero,
                NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

            if (hInfo == new IntPtr(-1)) return result;
            try
            {
                var ifd = new NativeMethods.SP_DEVICE_INTERFACE_DATA
                    { cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>() };

                for (uint i = 0;
                    NativeMethods.SetupDiEnumDeviceInterfaces(
                        hInfo, IntPtr.Zero, ref guidCopy, i, ref ifd);
                    i++)
                {
                    NativeMethods.SetupDiGetDeviceInterfaceDetail(
                        hInfo, ref ifd, IntPtr.Zero, 0, out uint req, IntPtr.Zero);
                    var ptr = Marshal.AllocHGlobal((int)req);
                    try
                    {
                        // cbSize: 8 on 64-bit, 6 on 32-bit
                        Marshal.WriteInt32(ptr, IntPtr.Size == 8 ? 8 : 6);
                        if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                                hInfo, ref ifd, ptr, req, out _, IntPtr.Zero))
                            continue;

                        var path = Marshal.PtrToStringAuto(ptr + 4) ?? "";
                        if (path.IndexOf("VID_0483", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            path.IndexOf("PID_5721", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            result.Add(path);
                        }
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
            }
            finally { NativeMethods.SetupDiDestroyDeviceInfoList(hInfo); }

            return result;
        }

        // ──────────────────────────────────────────────────────────
        //  内部：读线程
        // ──────────────────────────────────────────────────────────

        // 读缓冲 64KB：多线程并发读取同一端点，增加主机端并发度
        private Thread StartReader(byte endpoint, EndpointReadState state, Action<string> onLine, int bufferSize = 65536)
        {
            state.OnLine = onLine;
            state.Buffer.Clear();
            try { state.Queue?.Dispose(); } catch { }
            state.Queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());

            // 消费线程：从队列取原始字节 → 切行 → 解析 → 落盘。与读线程隔离，
            // 解析/磁盘 I/O 再慢也只是排队，绝不回压 USB 读取。
            state.Consumer = new Thread(() => ConsumeLoop(state))
            {
                IsBackground = true,
                Name = "WinUsbConsume_" + state.Name
            };
            state.Consumer.Start();

            // 启动4个并发读取线程，同时读取同一端点
            for (int i = 0; i < 4; i++)
            {
                int threadId = i;
                var t = new Thread(() => ReadLoopSync(endpoint, bufferSize, state.Name + "_T" + threadId,
                    chunk => { try { state.Queue!.Add(chunk); } catch { } }))
                {
                    IsBackground = true,
                    Name = "WinUsbReader_" + state.Name + "_T" + threadId,
                    Priority = ThreadPriority.Highest
                };
                t.Start();
                if (i == 0) return t;  // 返回第一个线程作为代表
            }
            return null!;
        }

        // 消费线程主体：按 FIFO 顺序取字节块，无缝拼接切行。
        private void ConsumeLoop(EndpointReadState st)
        {
            try
            {
                foreach (var chunk in st.Queue!.GetConsumingEnumerable())
                {
                    if (st.ConsumeClearRequest()) st.Buffer.Clear();
                    SplitLines(st, chunk, chunk.Length);
                }
            }
            catch { }
        }

        // 同时挂起的未决读请求数。32 个常驻接收，配合严格 FIFO 轮转回收。
        // 实测：N=1(最串行)与 N=32 在 4 端点固件下倒退次数同量级(40 vs 51)，
        // 证明 frame_id 倒退根因不在上位机 overlapped 并发，而在固件 4 端点版的
        // LSM 双缓冲发送时序（3 端点固件同款代码倒退仅 1~2 次）。详见对照数据。
        private const int OVERLAP_COUNT = 32;

        // 同步读循环：使用超大缓冲（64KB）+ 高优先级线程减少ReadPipe调用频率
        private void ReadLoopSync(byte endpoint, int bufferSize, string name, Action<byte[]> onData)
        {
            var buf = new byte[bufferSize];
            FileStream? rawDump = null;

            try
            {
                while (_running)
                {
                    bool ok = NativeMethods.WinUsb_ReadPipe(
                        _winUsbHandle, endpoint, buf, (uint)bufferSize, out uint transferred, IntPtr.Zero);

                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == 995) break;       // 已中止（Disconnect）
                        if (!_running) break;
                        if (err == 87)
                        {
                            ErrorOccurred?.Invoke($"端点 0x{endpoint:X2}({name}) 不可访问(err=87)");
                            break;
                        }
                        if (err == 121) continue;    // 超时，继续
                        // 其他错误，继续尝试
                        Thread.Sleep(1);
                        continue;
                    }

                    if (transferred > 0)
                    {
                        var chunk = new byte[transferred];
                        System.Buffer.BlockCopy(buf, 0, chunk, 0, (int)transferred);

                        // 诊断：LSM 原始字节转储
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
            catch { }
            finally
            {
                try { rawDump?.Dispose(); } catch { }
            }
        }

        // Overlapped 多缓冲读循环：单端点用。N 个固定（pin 住）缓冲轮流提交，
        // 按提交顺序（=字节到达顺序）消费，既不丢字节也保序。onData 收到每块字节。
        private void ReadLoopOverlapped(byte endpoint, int bufferSize, string name, Action<byte[]> onData)
        {
            int N = OVERLAP_COUNT;
            var bufs = new byte[N][];
            var handles = new GCHandle[N];
            var ptrs = new IntPtr[N];
            var ovl = new IntPtr[N];
            var evts = new IntPtr[N];
            int ovlSize = Marshal.SizeOf<NativeMethods.OVERLAPPED>();
            FileStream? rawDump = null;

            try
            {
                for (int i = 0; i < N; i++)
                {
                    bufs[i] = new byte[bufferSize];
                    handles[i] = GCHandle.Alloc(bufs[i], GCHandleType.Pinned);
                    ptrs[i] = handles[i].AddrOfPinnedObject();
                    evts[i] = NativeMethods.CreateEvent(IntPtr.Zero, false, false, null);  // auto-reset
                    ovl[i] = Marshal.AllocHGlobal(ovlSize);
                    ZeroOverlapped(ovl[i], ovlSize, evts[i]);
                    if (!SubmitRead(endpoint, ptrs[i], (uint)bufferSize, ovl[i]))
                    {
                        int e = Marshal.GetLastWin32Error();
                        if (e == 87)
                        {
                            ErrorOccurred?.Invoke($"端点 0x{endpoint:X2}({name}) 不可访问(err=87)");
                            return;
                        }
                    }
                }

                // 严格 FIFO 顺序回收：N 个槽轮流提交（端点始终有 N 个缓冲挂着接收，
                // 保持高吞吐），但消费时只按提交顺序逐个等待队头槽完成。
                // USB 单端点按提交序完成，故"按槽轮转回收"= 字节到达序 = frame_id 递增序，
                // 绝不乱序。WaitForMultipleObjects 按事件索引而非提交序返回，会乱序——故不用。
                int cur = 0;
                while (_running)
                {
                    // 只等当前队头槽完成（其余槽仍在后台接收，不丢字节）
                    uint wr = NativeMethods.WaitForSingleObject(evts[cur], 0xFFFFFFFF);
                    if (wr != 0) break;  // 非 WAIT_OBJECT_0（错误/放弃）

                    bool ok = NativeMethods.WinUsb_GetOverlappedResult(
                        _winUsbHandle, ovl[cur], out uint transferred, true);

                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == 995) break;       // 已中止（Disconnect）
                        if (!_running) break;
                        if (err == 87)
                        {
                            ErrorOccurred?.Invoke($"端点 0x{endpoint:X2}({name}) 不可访问(err=87)");
                            break;
                        }
                        // 其他错误：重置并重新提交本槽，轮转到下一个槽
                        ZeroOverlapped(ovl[cur], ovlSize, evts[cur]);
                        SubmitRead(endpoint, ptrs[cur], (uint)bufferSize, ovl[cur]);
                        cur = (cur + 1) % N;
                        continue;
                    }

                    if (transferred > 0)
                    {
                        var chunk = new byte[transferred];
                        System.Buffer.BlockCopy(bufs[cur], 0, chunk, 0, (int)transferred);

                        // 诊断：LSM 原始字节转储（ReadPipe 拿到即写，未经任何处理）
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

                    // 重新提交本槽并轮转到下一个槽（保持 N 个缓冲常驻 + 严格 FIFO 回收）
                    ZeroOverlapped(ovl[cur], ovlSize, evts[cur]);
                    SubmitRead(endpoint, ptrs[cur], (uint)bufferSize, ovl[cur]);
                    cur = (cur + 1) % N;
                }
            }
            catch { }
            finally
            {
                // 取消所有未决传输，并等它们真正结束后再释放缓冲/overlapped，避免 use-after-free
                try { NativeMethods.WinUsb_AbortPipe(_winUsbHandle, endpoint); } catch { }
                for (int i = 0; i < N; i++)
                {
                    if (ovl[i] != IntPtr.Zero)
                        try { NativeMethods.WinUsb_GetOverlappedResult(_winUsbHandle, ovl[i], out _, true); } catch { }
                }
                try { rawDump?.Dispose(); } catch { }
                for (int i = 0; i < N; i++)
                {
                    if (ovl[i] != IntPtr.Zero) { try { Marshal.FreeHGlobal(ovl[i]); } catch { } }
                    if (evts[i] != IntPtr.Zero) { try { NativeMethods.CloseHandle(evts[i]); } catch { } }
                    if (handles[i].IsAllocated) handles[i].Free();
                }
            }
        }

        // 提交一个异步读。返回 true 表示已挂起(ERROR_IO_PENDING)或立即完成，均正常。
        private bool SubmitRead(byte endpoint, IntPtr buffer, uint len, IntPtr overlapped)
        {
            bool ok = NativeMethods.WinUsb_ReadPipeOverlapped(
                _winUsbHandle, endpoint, buffer, len, out _, overlapped);
            if (ok) return true;
            int err = Marshal.GetLastWin32Error();
            return err == 997;   // ERROR_IO_PENDING（异步进行中，正常）
        }

        // 清零 OVERLAPPED 并写入事件句柄（最后一个字段）
        private static void ZeroOverlapped(IntPtr ptr, int size, IntPtr hEvent)
        {
            for (int o = 0; o < size; o++) Marshal.WriteByte(ptr, o, 0);
            Marshal.StructureToPtr(new NativeMethods.OVERLAPPED { hEvent = hEvent }, ptr, false);
        }

        // EP5(0x85) 专用 overlapped 多缓冲读循环。
        // N 个 pin 住的缓冲常驻接收（端点始终有读挂起，杜绝两次读之间的丢包窗口），
        // 严格按提交顺序逐槽回收 → 字节到达序 = 行到达序，命令响应与 MAG/AHT 均不乱序。
        // 不开 RAW_IO，故读长任意；每行=一个短包，512 缓冲一次取一行。跨读残行由 sb 拼接。
        private void ReadLoopRespOverlapped()
        {
            const int N = 16;            // 16 个常驻缓冲，足够吸收 100Hz MAG 突发
            const int bufferSize = 512;  // > 最大包(64)，单行短包一次读完
            var bufs = new byte[N][];
            var handles = new GCHandle[N];
            var ptrs = new IntPtr[N];
            var ovl = new IntPtr[N];
            var evts = new IntPtr[N];
            int ovlSize = Marshal.SizeOf<NativeMethods.OVERLAPPED>();
            var sb = new StringBuilder(256);   // 跨读拼接，按 \r/\n 切完整行

            try
            {
                for (int i = 0; i < N; i++)
                {
                    bufs[i] = new byte[bufferSize];
                    handles[i] = GCHandle.Alloc(bufs[i], GCHandleType.Pinned);
                    ptrs[i] = handles[i].AddrOfPinnedObject();
                    evts[i] = NativeMethods.CreateEvent(IntPtr.Zero, false, false, null);
                    ovl[i] = Marshal.AllocHGlobal(ovlSize);
                    ZeroOverlapped(ovl[i], ovlSize, evts[i]);
                    if (!SubmitRead(EP_RESP_IN, ptrs[i], (uint)bufferSize, ovl[i]))
                    {
                        int e = Marshal.GetLastWin32Error();
                        if (e == 87)
                        {
                            ErrorOccurred?.Invoke("响应端点不可访问(err=87)，命令响应通道已禁用");
                            ResponseEndpointAvailable = false;
                            return;
                        }
                    }
                }

                int cur = 0;
                while (_running)
                {
                    uint wr = NativeMethods.WaitForSingleObject(evts[cur], 0xFFFFFFFF);
                    if (wr != 0) break;

                    bool ok = NativeMethods.WinUsb_GetOverlappedResult(
                        _winUsbHandle, ovl[cur], out uint transferred, true);

                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == 995) break;       // 已中止（Disconnect）
                        if (!_running) break;
                        if (err == 87)
                        {
                            ErrorOccurred?.Invoke("响应端点错误 err=87，命令响应通道已禁用");
                            ResponseEndpointAvailable = false;
                            break;
                        }
                        // 其它错误（含超时 121）：重置并重提交本槽，轮转继续
                        ZeroOverlapped(ovl[cur], ovlSize, evts[cur]);
                        SubmitRead(EP_RESP_IN, ptrs[cur], (uint)bufferSize, ovl[cur]);
                        cur = (cur + 1) % N;
                        continue;
                    }

                    if (transferred > 0)
                    {
                        sb.Append(Encoding.UTF8.GetString(bufs[cur], 0, (int)transferred));
                        DrainRespLines(sb);
                    }

                    ZeroOverlapped(ovl[cur], ovlSize, evts[cur]);
                    SubmitRead(EP_RESP_IN, ptrs[cur], (uint)bufferSize, ovl[cur]);
                    cur = (cur + 1) % N;
                }
            }
            catch { }
            finally
            {
                try { NativeMethods.WinUsb_AbortPipe(_winUsbHandle, EP_RESP_IN); } catch { }
                for (int i = 0; i < N; i++)
                {
                    if (ovl[i] != IntPtr.Zero)
                        try { NativeMethods.WinUsb_GetOverlappedResult(_winUsbHandle, ovl[i], out _, true); } catch { }
                }
                for (int i = 0; i < N; i++)
                {
                    if (ovl[i] != IntPtr.Zero) { try { Marshal.FreeHGlobal(ovl[i]); } catch { } }
                    if (evts[i] != IntPtr.Zero) { try { NativeMethods.CloseHandle(evts[i]); } catch { } }
                    if (handles[i].IsAllocated) handles[i].Free();
                }
            }
        }

        // 从拼接缓冲切出完整行（以 \r 或 \n 结尾），逐行按包首分流；尾部残行留在 sb 等下次。
        private void DrainRespLines(StringBuilder sb)
        {
            while (true)
            {
                int nl = -1;
                for (int i = 0; i < sb.Length; i++)
                {
                    char c = sb[i];
                    if (c == '\r' || c == '\n') { nl = i; break; }
                }
                if (nl < 0)
                {
                    if (sb.Length > 8192) sb.Clear();  // 异常超长行保护
                    return;
                }

                var line = sb.ToString(0, nl).Trim();
                sb.Remove(0, nl + 1);
                if (line.Length == 0) continue;

                // 新固件在 0x85 上复用命令响应端点，按包首分流两路传感器数据。
                // 严格匹配 "aht,"/"mag,"（带逗号）的数据行；status 里的 "aht "(空格)
                // 配置行不含逗号，不会被误判，仍走 ResponseReceived → ParseStatusResponse。
                // 必须在这里分流，避免 100Hz 磁力数据涌入命令响应缓冲、扰乱 status 解析。
                if (line.StartsWith("aht,", StringComparison.Ordinal))
                    AhtLineReceived?.Invoke(line);
                else if (line.StartsWith("mag,", StringComparison.Ordinal))
                    MagLineReceived?.Invoke(line);
                else
                    ResponseReceived?.Invoke(line);
            }
        }

        private void SplitLines(EndpointReadState st, byte[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                char c = (char)buffer[i];
                if (c == '\n')
                {
                    var line = st.Buffer.ToString().TrimEnd('\r', '\n');
                    st.Buffer.Clear();
                    if (line.Length > 0)
                    {
                        RawDataReceived?.Invoke(line);
                        st.OnLine!(line);
                    }
                }
                else if (c != '\0')
                {
                    st.Buffer.Append(c);
                    if (st.Buffer.Length > 8192) st.Buffer.Clear();
                }
            }
        }

        private void HandleError(string source = "", int winErr = 0)
        {
            if (Interlocked.Exchange(ref _errorFired, 1) != 0) return;
            _running = false;
            if (winErr != 0)
                ErrorOccurred?.Invoke($"USB 读取错误 [{source}] WinErr={winErr}");
            Disconnected?.Invoke();
            ConnectionChanged?.Invoke(false);
        }

        private void SetPipeTimeout(byte endpoint, uint ms)
        {
            NativeMethods.WinUsb_SetPipePolicy(
                _winUsbHandle, endpoint,
                NativeMethods.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref ms);
        }

        private void EnableRawIO(byte endpoint)
        {
            uint enable = 1;
            NativeMethods.WinUsb_SetPipePolicy(
                _winUsbHandle, endpoint,
                NativeMethods.RAW_IO, sizeof(uint), ref enable);
        }

        private void CleanHandles()
        {
            if (_winUsbHandle != IntPtr.Zero)
            {
                try { NativeMethods.WinUsb_Free(_winUsbHandle); } catch { }
                _winUsbHandle = IntPtr.Zero;
            }
            if (_fileHandle != null && !_fileHandle.IsInvalid)
            {
                try { _fileHandle.Dispose(); } catch { }
                _fileHandle = null;
            }
        }

        // ──────────────────────────────────────────────────────────
        //  P/Invoke 声明
        // ──────────────────────────────────────────────────────────

        private static class NativeMethods
        {
            public const uint GENERIC_READ    = 0x80000000;
            public const uint GENERIC_WRITE   = 0x40000000;
            public const uint FILE_SHARE_READ  = 0x00000001;
            public const uint FILE_SHARE_WRITE = 0x00000002;
            public const uint OPEN_EXISTING   = 3;
            public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
            public const uint FILE_FLAG_OVERLAPPED  = 0x40000000;
            public const uint DIGCF_PRESENT         = 0x02;
            public const uint DIGCF_DEVICEINTERFACE = 0x10;
            public const uint PIPE_TRANSFER_TIMEOUT = 3;
            public const uint RAW_IO = 7;
            public const uint ALLOW_PARTIAL_READS = 5;

            [StructLayout(LayoutKind.Sequential)]
            public struct SP_DEVICE_INTERFACE_DATA
            {
                public int cbSize;
                public Guid InterfaceClassGuid;
                public uint Flags;
                public IntPtr Reserved;
            }

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern SafeFileHandle CreateFile(
                string lpFileName, uint dwDesiredAccess, uint dwShareMode,
                IntPtr lpSecurityAttributes, uint dwCreationDisposition,
                uint dwFlagsAndAttributes, IntPtr hTemplateFile);

            [DllImport("winusb.dll", SetLastError = true)]
            public static extern bool WinUsb_Initialize(
                SafeFileHandle DeviceHandle, out IntPtr InterfaceHandle);

            [DllImport("winusb.dll", SetLastError = true)]
            public static extern bool WinUsb_Free(IntPtr InterfaceHandle);

            [DllImport("winusb.dll", SetLastError = true)]
            public static extern bool WinUsb_AbortPipe(
                IntPtr InterfaceHandle, byte PipeID);

            [DllImport("winusb.dll", SetLastError = true)]
            public static extern bool WinUsb_ReadPipe(
                IntPtr InterfaceHandle, byte PipeID,
                byte[] Buffer, uint BufferLength,
                out uint LengthTransferred, IntPtr Overlapped);

            [DllImport("winusb.dll", SetLastError = true)]
            public static extern bool WinUsb_WritePipe(
                IntPtr InterfaceHandle, byte PipeID,
                byte[] Buffer, uint BufferLength,
                out uint LengthTransferred, IntPtr Overlapped);

            [DllImport("winusb.dll", SetLastError = true)]
            public static extern bool WinUsb_SetPipePolicy(
                IntPtr InterfaceHandle, byte PipeID,
                uint PolicyType, uint ValueLength, ref uint Value);

            // === Overlapped(异步) 读相关 ===

            [StructLayout(LayoutKind.Sequential)]
            public struct OVERLAPPED
            {
                public IntPtr Internal;
                public IntPtr InternalHigh;
                public uint Offset;
                public uint OffsetHigh;
                public IntPtr hEvent;
            }

            // Buffer 为已 pin 住的非托管指针（异步期间不能让 GC 移动），故用 IntPtr 重载
            [DllImport("winusb.dll", SetLastError = true, EntryPoint = "WinUsb_ReadPipe")]
            public static extern bool WinUsb_ReadPipeOverlapped(
                IntPtr InterfaceHandle, byte PipeID,
                IntPtr Buffer, uint BufferLength,
                out uint LengthTransferred, IntPtr Overlapped);

            [DllImport("winusb.dll", SetLastError = true)]
            public static extern bool WinUsb_GetOverlappedResult(
                IntPtr InterfaceHandle, IntPtr lpOverlapped,
                out uint lpNumberOfBytesTransferred,
                [MarshalAs(UnmanagedType.Bool)] bool bWait);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr CreateEvent(
                IntPtr lpEventAttributes,
                [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
                [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
                string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool ResetEvent(IntPtr hEvent);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

            [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern IntPtr SetupDiGetClassDevs(
                ref Guid ClassGuid, string? Enumerator,
                IntPtr hwndParent, uint Flags);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiEnumDeviceInterfaces(
                IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
                ref Guid InterfaceClassGuid, uint MemberIndex,
                ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

            [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern bool SetupDiGetDeviceInterfaceDetail(
                IntPtr DeviceInfoSet,
                ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
                IntPtr DeviceInterfaceDetailData,
                uint DeviceInterfaceDetailDataSize,
                out uint RequiredSize,
                IntPtr DeviceInfoData);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiDestroyDeviceInfoList(
                IntPtr DeviceInfoSet);
        }

        // ──────────────────────────────────────────────────────────
        //  每端点读状态
        // ──────────────────────────────────────────────────────────

        private sealed class EndpointReadState
        {
            public Action<string>? OnLine;
            public readonly string Name;
            public readonly StringBuilder Buffer = new(512);
            // 读线程把原始字节块投到这里，消费线程取出做切行/解析/落盘。
            // 单生产者→单消费者，FIFO 保证字节严格按到达顺序处理，不会乱序。
            public BlockingCollection<byte[]>? Queue;
            public Thread? Consumer;
            private volatile bool _clearRequested;

            public EndpointReadState(string name) => Name = name;

            public void RequestClear() => _clearRequested = true;

            public bool ConsumeClearRequest()
            {
                if (!_clearRequested) return false;
                _clearRequested = false;
                return true;
            }
        }
    }
}
