# 修复方案 A:启用现有 overlapped 读取(最快)

## 问题根因
代码已实现 `ReadLoopOverlapped`(32 个 overlapped 异步读),但 139 行调用的是 `StartReader` → 4 线程 × **同步** `ReadLoopSync`。同步调用每次阻塞等内核返回,4 线程也救不了。

## 修复(一行)
`WinUsbDeviceManager.cs` 第 139 行,从:
```csharp
_lsmThread  = StartReader(EP_LSM_IN,  _lsmState,  l => LsmLineReceived?.Invoke(l));
```
改成:
```csharp
_lsmThread = new Thread(() => ReadLoopOverlapped(EP_LSM_IN, 65536, "LSM",
    chunk => _lsmState.Queue?.TryAdd(chunk))) { IsBackground = true, Name = "WinUsbRead_LSM" };
_lsmThread.Start();

// 启动消费线程(切行→解析→落盘)
_lsmState.Consumer = new Thread(() => ConsumeLoop(_lsmState))
    { IsBackground = true, Name = "WinUsbConsume_LSM" };
_lsmState.Consumer.Start();
```

## 预期结果
- **`sof_send` 从 ~1600 提升到 >30k**(理论 33k,接近满速)
- **overrun 降到个位数**(overlapped 让主机读取完成快得多)

## 如果还不够
若 overlapped 也只到 ~5k(仍远低于 33k),说明 **WinUSB 的 overlapped 完成回调在 Windows 下确实慢**(内核调度延迟)→ 转方案 B(libusb)。

---

测试:**编译→运行→`acq_start usb 0`→等 20s→`acq_stop`→看固件日志 `[LSM-USB] FINAL sof_send=?`**。贴结果给我。
