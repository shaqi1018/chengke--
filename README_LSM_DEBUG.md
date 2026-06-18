# USB LSM 6664Hz 丢帧问题 - 完整诊断与解决方案

## 问题现状
- **固件诊断计数器**(20秒采集):
  - `overrun=55,431`:设备被迫覆盖还在发送中的半缓冲 5.5万次
  - `sof_send=1,411`:实际发送 1411 个半缓冲(理论 ~20,000)
  - **结论:主机读 EP1(LSM)太慢,设备 93% SOF 在等 DataIn 完成**

- **上位机状态**:
  - 已启用 32×overlapped 异步读
  - 已开启 RAW_IO 策略
  - 已禁用其它端点
  - **仍只达 7% 吞吐**(sof_send=1411/20000)

## 根本原因
**WinUSB 的 overlapped 完成回调在 Windows 内核层就是慢**(IOCP 调度延迟、用户态/内核态切换),应用层优化到头了。

## 上位机的错误论断
❌ "USB FS 带宽不够(每 ms 只能发 1 个 64B 包)"
- **事实**:USB FS Bulk 每帧(1ms)可发 **19 个 64B 包** = 1.2 MB/s
- **LSM 386KB/s 只占 32%**,绰绰有余
- **Linux 实测接近满速** → 证明 USB FS 够,WinUSB 慢

## 解决方案(优先级排序)

### 🚀 立刻做:Python libusb 终极测试(5 分钟)
**目的**:在 Windows 上用 libusb 证明 USB FS 能跑满,终结"带宽不够"的争论。

**步骤**:
1. `pip install pyusb`
2. Zadig 切驱动到 libusbK
3. 固件 `acq_start usb 0`
4. `python test_libusb_lsm.py`
5. 看结果:若收到 ~6664 Hz → 铁证 USB FS 够、WinUSB 慢

**文件**:`test_libusb_lsm.py` + `REFUTE_BANDWIDTH.md`

---

### ⚡ 方案 A:C# 迁移到 LibUsbDotNet(1-2 天,治本)
**适用**:若 Python 测试证明 libusb 能跑满。

**改动**:
- NuGet 装 `LibUsbDotNet`
- Zadig 驱动 WinUSB → libusbK
- 新建 `LibUsbDeviceManager.cs`(核心 ~150 行)

**预期**:`sof_send` 18k+,丢帧率 <5%

**文件**:`FIX_LIBUSB.md`

---

### 🩹 方案 B:降采样率到 1666Hz(妥协)
**适用**:若 libusb 也不行(不太可能)。

**改动**:固件 `s lsm odr 1666` 或上位机发命令
**预期**:丢帧率 <1%(实测 0.4%)
**缺点**:丢了 LSM 满速能力

---

### ❌ 不推荐:固件增大缓冲(治标不治本)
**原因**:主机持续慢(93% 等待),缓冲再大最终还是溢出,只是晚点。

---

## 固件侧已完成(本轮工作)
1. ✅ USB 麦克风功能(EP4,96kHz PCM)+ `s mic` 命令
2. ✅ 5 端点布局(LSM/H3/QMA/MIC/resp)+ tag 门控
3. ✅ auto-stop USB 修复
4. ✅ LSM USB 诊断计数器(overrun/sof_send/datain_complete)
5. ✅ 文档(`USB_MIC_HOST_CHANGES.md` / `USB_LSM_HOST_REASSEMBLY.md` / 解决方案文档)

**固件无需再改**(SD 模式 6664 drop=0,诊断已证明设备无瓶颈)。

---

## 下一步(按顺序)
1. **立刻**:上位机运行 `python test_libusb_lsm.py`,把结果贴我
2. **若 Python 测试 >6000 Hz**:做方案 A(libusb,1-2 天)
3. **若 Python 测试仍 <3000 Hz**:重试安装/驱动,或换台机器测
4. **若 libusb 确实不行**(极不可能):降速到 1666Hz

---

## 关键文档位置
- `D:\ChengKe\Sensor_1\USB-view\test_libusb_lsm.py`:Python 终极测试脚本
- `D:\ChengKe\Sensor_1\USB-view\REFUTE_BANDWIDTH.md`:反驳"带宽不够"+ 测试指南
- `D:\ChengKe\Sensor_1\USB-view\FIX_LIBUSB.md`:LibUsbDotNet 迁移方案
- `D:\ChengKe\Sensor_1\USB-view\FIX_OVERLAPPED.md`:overlapped 启用(已试,无效)
- `D:\ChengKe\Sensor_1\Sensor_Proj_V1.0\docs\USB_MIC_HOST_CHANGES.md`:端点布局
- `D:\ChengKe\Sensor_1\Sensor_Proj_V1.0\docs\USB_LSM_HOST_REASSEMBLY.md`:LSM 诊断结论

**当前最紧急:运行 Python 测试,证明 USB FS 够用。**
