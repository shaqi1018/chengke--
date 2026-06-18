# 上位机迁移修正文档：USB CDC → WCID (WinUSB) Bulk

> 下位机（STM32U575）已从 **USB CDC 虚拟串口** 改为 **WCID / WinUSB 批量(Bulk)流** 模式。
> 本文档给出上位机（C# WPF `VibrationMonitor`）需要的全部改动，照此即可改好。
> 设备已实测在 Windows「通用串行总线设备」下枚举为 **`Sensor WCID Bulk`**，免驱（自动绑定 WinUSB）。

---

## 0. 一句话概括

旧版：一个 COM 口，收发都在上面，数据是**一条 29 列的合并 CSV**。
新版：**没有 COM 口**了，是 WinUSB 设备；**3 个 Bulk IN 端点**各自独立地吐一个传感器的窄 CSV 流，**1 个 Bulk OUT 端点**收命令，**1 个 Bulk IN 端点回命令响应**。
`System.IO.Ports.SerialPort` 完全用不了，必须换成 WinUSB/libusb 访问方式。

---

## 1. 新的 USB 身份（设备发现要改）

| 项 | 旧 (CDC) | 新 (WCID) |
|---|---|---|
| VID | 0x0483 | **0x0483**（不变）|
| PID | **0x5740** | **0x5721** |
| 类型 | CDC 虚拟串口 | WinUSB Bulk |
| 产品串 | — | `Sensor WCID Bulk` |
| 厂商串 | — | `STMicroelectronics` |
| 序列号 | — | 24 字符 HEX（芯片 UID）|
| WinUSB DeviceInterfaceGUID | — | **`{F70242C7-FB25-443B-9E7E-A4260F373982}`** |

- 配置 1 / **接口 0** / 备用设置 0，`bNumEndpoints = 4`。
- WCID（Microsoft OS 1.0 描述符）会让 Windows 8+ **自动安装 WinUSB 驱动**，无需 Zadig、无需 inf。

---

## 2. 端点映射（Endpoint Map）

| 端点地址 | 方向 | 类型 | 最大包 | 用途 |
|---|---|---|---|---|
| `0x81` | IN  | Bulk | 64 B | **LSM6DSOX**（IMU：加速度+陀螺）CSV 流 |
| `0x82` | IN  | Bulk | 64 B | **H3LIS100DL**（高量程加速度）CSV 流 |
| `0x83` | IN  | Bulk | 64 B | **QMA6100P**（加速度）CSV 流（**无标签，纯 CSV**）|
| `0x84` | IN  | Bulk | 64 B | **命令响应**（ASCII 文本，见 §5）|
| `0x01` | OUT | Bulk | 64 B | **命令**（ASCII 文本，见 §6）|

> - 接口 0 共 **5 个端点**（4 IN + 1 OUT），`bNumEndpoints = 5`。
> - 三路传感器是**独立数据流**。不再有「合并帧」，各端点的 `frame_id/tick_ms` **各自独立**，不能假设三者同步成一帧。
> - `0x81/0x82/0x83` 三个端点格式完全一致（纯 CSV 字节，无任何标签）。

---

## 3. 各端点的 CSV 格式（逐端点不同！）

- 全部为 ASCII 文本，小数点用 `.`，每行以 `\r\n` 结尾，数值保留 **1 位小数**。
- **不发送 CSV 表头行**（旧版的 `frame_id,...` 表头不存在了），上位机要按下表硬编码列含义。
- 数据按 256 字节块累积后才发出（不是每行立即发），所以**一行可能跨多个 USB 包**：请把每个端点收到的字节**拼接成缓冲区，再按 `\r\n` 切分成行**。

### 3.1 端点 `0x81` — LSM6DSOX（8 列）
```
frame_id, tick_ms, accX_mg, accY_mg, accZ_mg, gyroX_mdps, gyroY_mdps, gyroZ_mdps\r\n
```
- 加速度单位 mg，陀螺单位 mdps。无温度列。

### 3.2 端点 `0x82` — H3LIS100DL（5 列）
```
frame_id, tick_ms, accX_mg, accY_mg, accZ_mg\r\n
```

### 3.3 端点 `0x83` — QMA6100P（5 列）
```
frame_id, tick_ms, accX_mg, accY_mg, accZ_mg\r\n
```

> 三路端点（0x81/0x82/0x83）都是 **256 字节整块传输、纯 CSV、无标签、无短包**。统一处理：把每个端点的字节拼接进缓冲区，按 `\r\n` 切行解析即可。**不需要**任何特殊的标签字节处理。

---

## 4.（已取消）QMA 标签字节

> 下位机已改为 4 个 IN 端点，QMA 独占 `0x83` 且**不再带标签字节**。本节原有的标签剥离逻辑**不需要了**，三路端点对称处理。

---

## 5. 命令响应通道（端点 `0x84`）—— 下位机已实现回传

下位机已把所有命令响应（`pong`、`status` 各行、`cfg`、`OK/ERR`、`Usage` 等）回传到 **IN 端点 `0x84`**。请求-应答模式可以在纯 USB 上正常工作。

**协议**：
1. 上位机把命令文本 + `\r\n` 写到 OUT `0x01`。
2. 下位机在任务上下文处理命令，把**完整响应（可能多行）累积成一个 Bulk 传输**发到 IN `0x84`。
3. 上位机从 `0x84` 读取，按 `\r\n` 切分成行解析。

**读取要点**：
- 一条命令的响应是**一次性发出的一个传输**（多行已拼在一起）。用带超时的 Bulk 读（如 300~500ms）从 `0x84` 读，读到的字节按 `\r\n` 切行。
- 若响应长度恰为 64 的整数倍（少见，文本一般以 `\r\n` 结尾不对齐），不会有短包收尾 → 靠读超时判定结束即可。
- 一次只发一条命令、读完响应再发下一条（命令是串行的，下位机单槽处理）。
- 建议给 `0x84` 单独起一个读线程，或在发命令后同步读一次（类似旧版 `SendAndWait`）。

**响应内容**与旧 CDC 完全一致（同一套 `UsbCmd_*` 生成），所以 `DataParser.ParseStatusResponse` 等解析逻辑**可直接复用**，只是输入来源从串口换成 `0x84`。

> **数据流前提不变**：上位机连上后必须先发 `acq_start usb\r\n`，传感器数据才会从 `0x81/0x82/0x83` 输出；`acq_stop\r\n` 停止。这两条命令的确认响应也会从 `0x84` 回来。

---

## 6. 命令集（写到 OUT `0x01`，ASCII + `\r\n`，单条 ≤ 63 字节）

与旧版命令字一致，**响应均从 IN `0x84` 返回**（见 §5）：

| 命令 | 说明 / 响应 |
|---|---|
| `ping` | 心跳，响应 `pong\r\n` |
| `help` | 帮助，响应一行命令列表 |
| `status` | 设备状态，响应多行（`tick=…` / `flow …` / `sensor …` / `frame …` / `mode …` / `acq …` / `cfg …`），格式与旧版一致，`ParseStatusResponse` 可复用 |
| `acq_start usb` | **开始采集并从 USB 输出**（上位机收数前必发）|
| `acq_start sd` | 采集写 SD（不走 USB 数据流）|
| `acq_start usb 5000` | 定时采集 5000 ms |
| `acq_stop` | 停止采集 |
| `acq_status` | 采集状态 |
| `s <sensor> <param> <value>` | 改传感器参数，如 `s lsm odr 1000` |
| `boot_msc` | 切到 USB U 盘(MSC)模式并复位（设备会断开重枚举为 U 盘）|

---

## 7. 具体到代码的改动清单（C# / `VibrationMonitor`）

### 7.1 依赖
- **移除** `System.IO.Ports` 的使用（包可留着不影响）。
- **新增** WinUSB 访问库，推荐 **`LibUsbDotNet`**（NuGet，2.2.x 或 3.x）。它在 Windows 上走 WinUSB 后端，配合本设备的 WCID 自动驱动，**无需 Zadig**。
  ```xml
  <PackageReference Include="LibUsbDotNet" Version="3.0.102-alpha" />
  <!-- 或稳定的 2.2.29，按团队习惯选 -->
  ```

### 7.2 `Services/SerialPortManager.cs` → 重写为 `WinUsbDeviceManager.cs`
- 删除 `SerialPort`、`ScanPorts`（按 COM 名枚举）等。
- 新逻辑：
  1. 按 **VID 0x0483 / PID 0x5721** 打开设备；claim **接口 0**。
  2. 打开 4 个 IN 端点读取器（`0x81/0x82/0x83` 数据 + `0x84` 命令响应）+ 1 个 OUT 写入器（`0x01`）。
  3. 起读线程：3 个数据端点各一个，把字节拼接、按 `\r\n` 切行，触发 `LsmLineReceived / H3LineReceived / QmaLineReceived`；`0x84` 单独一个读线程（或在发命令后同步读），触发 `ResponseReceived`。三路数据端点处理逻辑完全一致（无标签）。
  4. `SendCommand(string)`：`OUT 0x01` 写 `cmd + "\r\n"`（ASCII）。
- 设备发现：用 LibUsbDotNet 的设备枚举（按 VID/PID 或 GUID），替代 `Win32_PnPEntity` COM 扫描。

### 7.3 `Services/DataParser.cs`
- **删除** 29 列的 `ParseCsvLine`。
- **新增 3 个窄解析器**（与 §3 列对齐）：
  - `ParseLsm(string) -> LsmSample`（8 列）
  - `ParseH3(string)  -> H3Sample`（5 列）
  - `ParseQma(string) -> QmaSample`（5 列）
- `ParseStatusResponse` **直接复用**——输入改为来自 `0x84` 的响应文本（格式与旧版一致）。

### 7.4 `Models/SensorData.cs`
- 把合并的 `SensorFrame`（29 字段）拆成 **3 个独立 record**：`LsmSample / H3Sample / QmaSample`，各含自己的 `FrameId/TickMs + 数值`。
- UI/绘图层改为**分别订阅**三路样本（它们不再同步成一帧；按各自 `tick_ms` 对齐时间轴即可）。

### 7.5 `Services/DeviceCommander.cs`
- `Send()` 改为走 `WinUsbDeviceManager.SendCommand`（写 OUT `0x01`）。
- `SendAndWait / SendAndCollect / Ping / Status`：**逻辑基本保留**，只把「响应来源」从串口 `LineReceived` 换成 `0x84` 的 `ResponseReceived`。即：发命令到 `0x01` → 等 `0x84` 回数据 → 收齐后按行返回。`Ping()`/`Status()` 等可照常工作。
- 连接后初始化序列：发 `acq_start usb\r\n` 开流；断开前发 `acq_stop\r\n`。
- 注意命令**串行**：一条命令收到响应（或超时）后再发下一条。

### 7.6 设备发现 / 连接 UI
- 「扫描串口」改为「扫描 WinUSB 设备」（按 VID/PID）。
- 连接成功的判据从 `_port.IsOpen` 改为 WinUSB 句柄有效 + 3 个 IN 读线程已起。

---

## 8. 读取策略与注意点（避免踩坑）

- **0x81/0x82/0x83**：传输是 256B 的整 64 倍数，**不会有短包**，且三者格式一致（纯 CSV、无标签）。用 LibUsbDotNet 的 reader（带超时，如 100ms）循环读，读到多少拼多少，按 `\r\n` 切行。不要假设一次读 = 一行。
- **0x84（响应）**：一条命令的响应是一个 Bulk 传输，多行已拼好。带超时读，按 `\r\n` 切行。
- 单端点读缓冲建议 ≥ 512B；命令写缓冲 ≤ 64B。
- 重新 `acq_start` 会重置下位机的双缓冲，**上位机应清空各数据端点的拼接缓冲区**，避免跨会话残留半行。
- 数值用 `CultureInfo.InvariantCulture` 解析（小数点固定 `.`）。
- 断开/异常时关闭 reader、释放 WinUSB 句柄。

---

## 9. 验收标准

1. 程序能枚举到 `Sensor WCID Bulk`（VID 0483 / PID 5721），无需装驱动。
2. 发 `ping` → 从 `0x84` 收到 `pong`；发 `status` → 从 `0x84` 收到多行状态并能用 `ParseStatusResponse` 解析。
3. 发 `acq_start usb`，能分别从 `0x81/0x82/0x83` 收到**可解析的 CSV 行**（LSM 8 列、H3 5 列、QMA 5 列），无错行（三路均无标签）。
4. 三路实时曲线各自刷新（按各自 `tick_ms`）。
5. 发 `acq_stop` 后数据停止。

---

### 附：下位机关键常量出处（便于核对）
- VID/PID/产品串：`Core/Src/usbd_desc.c`（VID 0x0483 / PID 0x5721）
- 端点地址 / 包大小 / WCID GUID / `N_IN_ENDPOINTS=4`：`Middlewares/.../SensorStreaming_WCID/Inc/usbd_wcid_streaming.h`、`.../Src/usbd_wcid_streaming.c`
- 各传感器 CSV 列：`Core/Src/app_freertos.c`（LSM、H3、QMA 三个采集任务里的 `rowbuf` 拼装处）
- 命令集与响应：`Core/Src/app_freertos.c` `UsbCmd_Process`；响应经 `UsbCdcService_Write` 宏 → `UsbWcidApp_RespAppend` → IN `0x84`
- 命令处理在 `StartUsbUploadTask`（任务上下文），由 `WcidCmdCallback`（USB ISR）唤醒
