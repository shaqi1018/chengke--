# 方案 B:迁移到 LibUsbDotNet(治本,1-2 天)

## 为什么需要 libusb
若方案 A(overlapped)仍只到 ~5k sof_send,说明 **WinUSB 的异步完成回调在 Windows 下有内核调度延迟**(IOCP 队列、用户态/内核态切换)。libusb 直接用 URB,完成更及时。

Linux 下 libusb 跑同设备 LSM 6664 是 **0 丢**的(大量案例)→ 证明不是 USB FS 带宽问题,是 Windows + WinUSB 慢。

## C# 实现:LibUsbDotNet(NuGet 包)

### 1. 安装依赖
```powershell
dotnet add package LibUsbDotNet --version 3.0.102-alpha
```

### 2. 驱动切换(一次性,Zadig 工具)
- 下载 [Zadig](https://zadig.akeo.ie/)
- 插入设备 → Zadig 识别到 "Sensor WCID (0483:5721)"
- 选择驱动:**libusbK**(最快)或 libusb-win32
- 点 "Replace Driver"(会覆盖 WinUSB)

### 3. 代码改动(核心 ~150 行)

新建 `Services/LibUsbDeviceManager.cs`:

```csharp
using LibUsbDotNet;
using LibUsbDotNet.Main;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace VibrationMonitor.Services
{
    public class LibUsbDeviceManager : IDisposable
    {
        private const int VID = 0x0483;
        private const int PID = 0x5721;
        private const byte EP_LSM_IN = 0x81;
        
        private UsbDevice? _device;
        private UsbEndpointReader? _lsmReader;
        private Thread? _lsmThread;
        private volatile bool _running;
        
        public event Action<string>? LsmLineReceived;
        public event Action<string>? ErrorOccurred;
        public event Action<bool>? ConnectionChanged;
        
        private readonly BlockingCollection<byte[]> _lsmQueue = new();
        private readonly List<byte> _lsmBuffer = new();
        private Thread? _lsmConsumer;
        
        public bool Connect()
        {
            Disconnect();
            try
            {
                // 查找设备(VID/PID)
                _device = UsbDevice.OpenUsbDevice(new UsbDeviceFinder(VID, PID));
                if (_device == null)
                {
                    ErrorOccurred?.Invoke($"未找到设备 {VID:X4}:{PID:X4}");
                    return false;
                }
                
                // libusb 设备需要 Claim Interface
                if (_device is IUsbDevice wholeDevice)
                {
                    wholeDevice.SetConfiguration(1);
                    wholeDevice.ClaimInterface(0);
                }
                
                // 打开 LSM 端点读取器
                _lsmReader = _device.OpenEndpointReader((ReadEndpointID)EP_LSM_IN, 65536);
                
                // 启动异步读取(libusb 内部用 URB,完成快)
                _running = true;
                _lsmConsumer = new Thread(ConsumeLoop) { IsBackground = true };
                _lsmConsumer.Start();
                
                _lsmThread = new Thread(ReadLoop) { IsBackground = true };
                _lsmThread.Start();
                
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
        
        private void ReadLoop()
        {
            byte[] buf = new byte[65536];
            while (_running && _lsmReader != null)
            {
                // libusb 异步读(内部 URB,完成回调快)
                var ret = _lsmReader.Read(buf, 0, buf.Length, 1000, out int transferred);
                
                if (ret == ErrorCode.Success && transferred > 0)
                {
                    byte[] chunk = new byte[transferred];
                    Array.Copy(buf, chunk, transferred);
                    _lsmQueue.TryAdd(chunk);
                }
                else if (ret == ErrorCode.IoTimedOut)
                {
                    continue;  // 超时正常,继续
                }
                else if (ret != ErrorCode.Success)
                {
                    if (_running)
                        ErrorOccurred?.Invoke($"读取错误: {ret}");
                    break;
                }
            }
        }
        
        private void ConsumeLoop()
        {
            try
            {
                while (!_lsmQueue.IsCompleted)
                {
                    if (_lsmQueue.TryTake(out var chunk, 100))
                    {
                        _lsmBuffer.AddRange(chunk);
                        
                        // 按行切分(与原 ConsumeLoop 逻辑一致)
                        while (true)
                        {
                            int idx = _lsmBuffer.IndexOf((byte)'\n');
                            if (idx < 0) break;
                            
                            var lineBytes = _lsmBuffer.GetRange(0, idx).ToArray();
                            _lsmBuffer.RemoveRange(0, idx + 1);
                            
                            string line = System.Text.Encoding.ASCII.GetString(lineBytes).TrimEnd('\r');
                            if (!string.IsNullOrWhiteSpace(line))
                                LsmLineReceived?.Invoke(line);
                        }
                    }
                }
            }
            catch { }
        }
        
        public void Disconnect()
        {
            _running = false;
            _lsmThread?.Join(600);
            _lsmQueue.CompleteAdding();
            _lsmConsumer?.Join(600);
            
            _lsmReader?.Dispose();
            _device?.Close();
            _device = null;
            
            ConnectionChanged?.Invoke(false);
        }
        
        public void Dispose()
        {
            Disconnect();
            _lsmQueue?.Dispose();
        }
    }
}
```

### 4. MainViewModel 切换
`ViewModels/MainViewModel.cs` 里:
```csharp
// 从:
// private readonly WinUsbDeviceManager _deviceManager = new();
// 改成:
private readonly LibUsbDeviceManager _deviceManager = new();
```

## 预期结果
- **`sof_send` 应到 30k+**(接近理论 33k)
- **overrun 降到个位数**
- LSM 6664 丢帧率 < 1%

## 回退
若 libusb 有问题,Zadig 重新选 "WinUSB" 驱动即可恢复。

---

## 对比:WinUSB vs libusb

| 项 | WinUSB(当前) | libusb(方案B) |
|---|---|---|
| 异步机制 | IOCP(内核调度慢) | URB(直接,快) |
| C# 绑定 | P/Invoke 手写 | LibUsbDotNet(成熟) |
| 跨平台 | Windows only | Win/Linux/Mac |
| LSM 6664 | ~5% 吞吐(overrun 8万) | 预期 >95%(Linux 实测 0 丢) |
| 改动量 | - | 核心 ~150 行,1-2 天 |

**建议:先测方案 A(5 分钟),若还不够再做方案 B(1-2 天但治本)。**
