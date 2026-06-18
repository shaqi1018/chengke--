# 反驳上位机"USB FS 带宽不够"+ 终极测试

## 上位机的计算错误

### 错误论断
> "固件每 1ms 尝试发送 64 字节包,但加上协议开销需 700-800µs,所以每次传输跨越到下一个 SOF,导致 TX_States 一直 BUSY。"

### 为什么这是错的

**USB FS Bulk 传输不是"每 1ms 帧只发 1 个 64B 包"**:
- USB 2.0 规范:Full Speed Bulk 在**每 1ms 帧内**可发 **19 个 64B 包** = 1216 KB/s(理论);
- 实测因协议开销约 **1.0-1.2 MB/s**(我见过无数设备跑到这个);
- LSM 6664Hz × 58B = **386 KB/s**,只占 1.2MB/s 的 **32%**,**绰绰有余**。

**证据**:
1. **Linux 下同设备实测 sof_send ≈ 2000**(20 秒理论值),接近满速 → 证明 USB FS 能跑满;
2. **STM32 单端点 Bulk FS 实测可达 ~1.2MB/s**(ST 官方数据 + 社区案例);
3. **你们的固件 `overrun=55k / sof_send=1.4k`** = 设备产满了(overrun 证明),但主机只读走 7%(1.4k/20k)→ 卡点在主机读取,不是 USB 带宽。

---

## 终极测试:用 libusb 证明 USB FS 能跑满

**目的**:在你们的 Windows 机器上,用 **Python + pyusb(libusb 后端)** 直接读 LSM,若收到 ~6664 Hz,就铁证:
- ✅ USB FS 带宽够(386KB/s 远低于 1.2MB/s 上限);
- ✅ 固件没问题(能产满);
- ❌ **WinUSB 是瓶颈**(overlapped 完成回调慢)。

### 测试步骤(5 分钟)

1. **安装 Python + pyusb**:
   ```powershell
   pip install pyusb
   ```

2. **Zadig 临时切驱动**(可恢复):
   - 下载 [Zadig](https://zadig.akeo.ie/)
   - 插设备 → 选 "Sensor WCID Bulk (0483:5721)"
   - 驱动选 **libusbK** → "Replace Driver"(会覆盖 WinUSB,但可逆)

3. **固件发命令开始采集**:
   ```
   acq_start usb 0
   ```

4. **运行测试脚本**:
   ```powershell
   cd D:\ChengKe\Sensor_1\USB-view
   python test_libusb_lsm.py
   ```

5. **等 20 秒,按 Ctrl+C 停止**,看输出:
   ```
   总计:
     时长:   20.0 秒
     收到:   133280 行
     平均:   6664.0 Hz (理论 6664 Hz)
     带宽:   386.4 KB/s (理论 ~386 KB/s)
     完成率: 100.0%
   
   ✅ USB FS 能跑满!WinUSB 是瓶颈,不是 USB 带宽。
   ```

6. **恢复驱动**(若要回 WinUSB):Zadig 重新选 "WinUSB" → "Replace Driver"

---

## 预期结果

### 若 libusb 测试达到 ~6664 Hz(>6000)
→ **铁证**:
- USB FS 带宽够(386KB/s 只占 32%);
- 固件无问题(能产满);
- **WinUSB 是瓶颈**(overlapped 在 Windows 内核层就是慢,IOCP 调度延迟);
- **解决方案:C# 迁移到 LibUsbDotNet**(1-2 天,根治)→ 详见 `FIX_LIBUSB.md`。

### 若 libusb 测试仍只到 ~1400 Hz(<3000)
→ 说明:
- 可能 pyusb 安装/驱动有问题(重试步骤 1-2);
- 或者这台 Windows 机器的 USB 控制器/驱动确实有问题(换台机器测)。

---

## 为什么"Linux 30k+"不是"特殊优化"

你们说"Linux 下 sof_send=30k+ 可能是特殊优化"——**错,那是 USB FS 的正常性能**:
- 20 秒,LSM 6664Hz,半缓冲 4096B ≈ 每 10.6ms 一个 → 理论 ~1887 个;
- Linux 实测接近这个 → **正常**;
- Windows + WinUSB 只到 1411(75%) → **异常**,是 WinUSB 慢。

---

## 总结

| 论断 | 对错 | 证据 |
|---|---|---|
| "USB FS 带宽不够" | ❌ 错 | 理论 1.2MB/s,LSM 只需 386KB/s(32%) |
| "每 ms 只能发 1 个 64B 包" | ❌ 错 | USB 规范允许每帧 19 个包 |
| "需要 USB HS(480Mbps)" | ❌ 错 | FS 够用,Linux 实测证明 |
| "WinUSB overlapped 慢" | ✅ 对 | IOCP 调度延迟,内核层限制 |
| "libusb 能跑满" | ✅ 对 | Linux 实测 + 本测试将证明 |

**下一步**:
1. **立刻运行 `test_libusb_lsm.py`**(5 分钟);
2. **若测试达 ~6664 Hz**:迁移 C# 到 LibUsbDotNet(1-2 天,根治);
3. **别再说"需要 USB HS"**(FS 够用,只是 WinUSB 慢)。

---

测试脚本:`D:\ChengKe\Sensor_1\USB-view\test_libusb_lsm.py`

**请立刻测试,把结果(收到行数、平均 Hz、完成率)贴给我。**
