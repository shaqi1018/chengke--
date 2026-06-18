# 三传感器振动采集系统 — 上位机开发提示词

## 你的任务

为一个 STM32U575 三传感器振动采集设备开发一个简易上位机（PC 端），通过 USB CDC 串口与设备通信。

---

## 1. 设备简介

这是一台三传感器同步振动采集设备，通过 USB 虚拟串口（CDC）与 PC 通信。设备同时运行 3 个加速度传感器，数据可存 SD 卡或通过 USB 实时上传。

### 三个传感器

| 传感器 | 测量内容 | 最高采样率 | 量程 |
|--------|---------|-----------|------|
| LSM6DSOX | 3 轴加速度 + 3 轴陀螺仪 + 温度 | 6664 Hz | ±4g / ±2000dps |
| QMA6100P | 3 轴加速度 | 1600 Hz | ±4g |
| H3LIS100DL | 3 轴加速度（高冲击） | 400 Hz | ±100g |

---

## 2. USB CDC 通信协议

### 基本参数
- **接口**: USB Full-Speed，虚拟串口（CDC ACM）
- **波特率**: 不需要设置（USB CDC 自带速率，115200 只是显示用）
- **换行符**: 设备以 `\r\n` 结尾，命令也需以 `\r\n` 结尾
- **命令最大长度**: 64 字节
- **响应最大单行**: 约 160 字节

### 命令格式
所有命令都是纯文本 ASCII，以 `\n` 或 `\r\n` 结尾。设备收到完整一行后执行。

---

## 3. 完整命令列表

### 3.1 基础命令

| 命令 | 功能 | 响应示例 |
|------|------|---------|
| `ping` | 连通性测试 | `pong` |
| `help` | 显示帮助 | `Commands: ping, help, status, acq_start, acq_stop, s <lsm\|h3\|qma> <odr\|range\|en> <val>, msc` |
| `status` | 系统状态 | 多行输出（见下文） |

### 3.2 采集控制

| 命令 | 功能 | 响应示例 |
|------|------|---------|
| `acq_start sd` | 开始 SD 卡采集，无限时长 | `OK acq_start sink=sd duration_ms=0` |
| `acq_start sd 5000` | 开始 SD 卡采集，5 秒后自动停止 | `OK acq_start sink=sd duration_ms=5000` |
| `acq_start usb` | 开始 USB 实时上传，无限时长 | `OK acq_start sink=usb duration_ms=0` |
| `acq_start usb 3000` | 开始 USB 实时上传，3 秒后自动停止 | `OK acq_start sink=usb duration_ms=3000` |
| `acq_stop` | 停止采集 | `OK acq_stop` 或 `ACQ already stopped` |

**注意**: 传感器采样率不由 `acq_start` 控制，而是由配置文件或 `s` 命令设定。`acq_start` 只控制"采集到哪里"（SD 或 USB）和"采集多久"。

### 3.3 传感器参数调整

| 命令 | 功能 | 响应示例 |
|------|------|---------|
| `s lsm odr 1666` | 设置 LSM6DSOX 采样率 | `OK lsm odr=1666` |
| `s lsm range 8` | 设置 LSM6DSOX 加速度量程 (±8g) | `OK lsm range=8` |
| `s lsm en 0` | 禁用 LSM6DSOX | `OK lsm en=0` |
| `s h3 odr 400` | 设置 H3LIS100DL 采样率 | `OK h3 odr=400` |
| `s qma odr 1600` | 设置 QMA6100P 采样率 | `OK qma odr=1600` |

**支持的 ODR 值（设备会自动匹配最近值）:**
- LSM6DSOX: 12, 26, 52, 104, 208, 416, 833, 1666, 3332, 6664
- H3LIS100DL: 50, 100, 400
- QMA6100P: 100, 200, 400, 800, 1600

**注意**: `s` 命令修改后立即生效，并写入 SD 卡的 DEVCFG.JSN 配置文件。下次开机自动加载。

### 3.4 模式切换

| 命令 | 功能 | 响应 |
|------|------|------|
| `msc` | 切换到 USB MSC 模式（SD 卡当 U 盘） | 设备会重启，USB 重新枚举为 U 盘 |

### 3.5 隐藏调试命令（帮助里不显示，但可用）

| 命令 | 功能 |
|------|------|
| `snapshot` | 查看三传感器实时快照（最新一帧数据） |
| `flowstat` | 数据流统计（帧数、丢帧、队列深度等） |
| `dmastat` / `stat` | SPI DMA 传输统计 |
| `acq_status` | 采集状态（等同 status 里的 acq 行） |

---

## 4. `status` 命令响应格式

`status` 返回多行，格式如下：

```
tick=12345 heap=45678
flow frames=30467 write_fail=0 stale=0 mixed=0 coherent=30467
sensor updates lsm=30467 h3=1886 qma=9788
frame io sd_written=30467 usb_sent=0 last_frame=30467
frame queue depth=0 dropped=0 high=3
mode usb=0 sd=active
acq state=running sink=sd duration_ms=5000 elapsed_ms=3200 remaining_ms=1800
cfg lsm=+-4g/833Hz gyro=+-2000dps/833Hz h3=+-100g/400Hz qma=+-4g/1600Hz
```

**字段说明:**
- `tick`: 系统运行毫秒数
- `heap`: FreeRTOS 剩余堆内存 (字节)
- `flow frames`: Logger 已写入 SD 卡的总行数
- `sensor updates`: 各传感器更新次数
- `frame io sd_written`: SD 卡写入帧数 / `usb_sent`: USB 发送帧数
- `frame queue depth/dropped/high`: 帧缓冲队列状态
- `mode usb/sd`: 当前活跃的输出通道
- `acq state`: 采集状态 (running/stopped)、sink (sd/usb)、duration、elapsed、remaining
- `cfg`: 当前传感器配置摘要

---

## 5. USB 实时数据流格式

当使用 `acq_start usb` 启动后，设备会通过同一 USB CDC 接口推送 CSV 数据。

### 数据头（仅第一行）
```
frame_id,tick_ms,enabled_mask,present_mask,lsm_sample_seq,lsm_valid,lsm_acc_x_mg,lsm_acc_y_mg,lsm_acc_z_mg,lsm_gyro_x_mdps,lsm_gyro_y_mdps,lsm_gyro_z_mdps,lsm_temp_c,h3_sample_seq,h3_valid,h3_raw_x,h3_raw_y,h3_raw_z,h3_acc_x_mg,h3_acc_y_mg,h3_acc_z_mg,qma_sample_seq,qma_valid,qma_raw_x,qma_raw_y,qma_raw_z,qma_acc_x_mg,qma_acc_y_mg,qma_acc_z_mg
```

### 数据行示例
```
1,5272,0x07,0x07,100,1,0.0,12.2,981.5,100.5,-50.3,25.1,36.2,100,1,0,12,200,0.0,780.0,15600.0,100,1,0,5,100,0.0,24.4,488.0
```

### 字段含义

| 列号 | 字段名 | 类型 | 说明 |
|------|--------|------|------|
| 1 | frame_id | uint32 | 帧序号（递增） |
| 2 | tick_ms | uint32 | 时间戳（系统毫秒） |
| 3 | enabled_mask | hex | 使能掩存器 (bit0=LSM, bit1=H3, bit2=QMA) |
| 4 | present_mask | hex | 数据有效掩存器 |
| 5-7 | lsm_sample_seq, lsm_valid, ... | | LSM6DSOX 元数据 |
| 8-13 | lsm_acc_x/y/z_mg, lsm_gyro_x/y/z_mdps | float | LSM 加速度 (mg) + 陀螺仪 (mdps) |
| 14 | lsm_temp_c | float | LSM 温度 (°C) |
| 15-17 | h3_sample_seq, h3_valid, h3_raw_x/y/z | | H3LIS 元数据 + 原始值 |
| 18-20 | h3_acc_x/y/z_mg | float | H3LIS 加速度 (mg) |
| 21-23 | qma_sample_seq, qma_valid, qma_raw_x/y/z | | QMA 元数据 + 原始值 |
| 24-26 | qma_acc_x/y/z_mg | float | QMA 加速度 (mg) |

**关键点**: USB 上传的数据已经是物理值（mg / mdps / °C），不需要再换算。但 `raw_x/y/z` 列是传感器原始寄存器值，用于调试。

---

## 6. SD 卡文件结构

设备采集的 SD 卡数据在拔卡后可通过上位机读取分析：

```
SD卡/
├── DEVCFG.JSN              ← 设备全局配置
├── CKBX0001/               ← 第 1 次采集会话
│   ├── CONFIG.JSN          ← 本次采集的配置快照
│   ├── LSM_IMU.CSV         ← LSM6DSOX 加速度+陀螺仪 (原始 int16)
│   ├── LSM_TMP.CSV         ← LSM6DSOX 温度
│   ├── H3_ACC.CSV          ← H3LIS100DL 加速度 (原始 int8)
│   └── QMA_ACC.CSV         ← QMA6100P 加速度 (原始 int14)
├── CKBX0002/
│   └── ...
```

### CSV 文件格式

**LSM_IMU.CSV** (每秒最多 6664 行):
```csv
frame_id,tick_ms,acc_x,acc_y,acc_z,gyr_x,gyr_y,gyr_z
1,5272,82,-43,8192,3,-1,0
```
- `acc_x/y/z`: int16，乘以 0.122 得到 mg
- `gyr_x/y/z`: int16，乘以 70.0 得到 mdps

**LSM_TMP.CSV**:
```csv
frame_id,tick_ms,temp_C
1,54882,36.2
```

**H3_ACC.CSV / QMA_ACC.CSV**:
```csv
frame_id,tick_ms,raw_x,raw_y,raw_z
1,5443,0,12,200
```
- H3: raw 是 int8，乘以 780 得到 mg
- QMA: raw 是 int14，乘以 0.488 得到 mg

---

## 7. 上位机功能需求

### 核心功能

1. **串口连接管理**
   - 自动扫描可用 COM 口（VID:PID 或描述匹配 STM32 VCP）
   - 连接/断开按钮
   - 连接状态显示

2. **设备信息获取**
   - 连接后自动发送 `status` 获取设备状态
   - 解析并展示传感器配置、采集状态

3. **采集控制**
   - 选择输出目标: SD 卡 / USB 上传
   - 设置采集时长（可选，0 = 无限）
   - 开始/停止按钮
   - 倒计时显示（如果设了时长）

4. **传感器配置**
   - 三个传感器的 ODR 选择（下拉框，只显示支持的值）
   - 量程选择
   - 启用/禁用开关
   - 修改后实时下发 `s` 命令

5. **状态监控**
   - 定时轮询 `status`（每 1-2 秒）
   - 显示: 系统运行时间、堆内存、帧计数、丢帧数、队列深度
   - 采集进度条（如有时长限制）

6. **实时数据可视化**（USB 上传模式）
   - 解析 USB 流的 CSV 数据
   - 三轴加速度实时波形（可选传感器）
   - 数据率显示（实际 Hz）

7. **SD 卡数据查看**（可选）
   - 切换到 MSC 模式后读取 SD 卡上的 CSV 文件
   - 简单波形显示和数据导出

### 进阶功能（可选）

- 数据录制: USB 流模式下自动保存 CSV 文件到 PC
- 命令历史: 记录发送过的命令，支持快速重发
- 日志窗口: 显示所有收发的原始数据
- 配置文件编辑: 可视化编辑 DEVCFG.JSN

---

## 8. 技术建议

### 推荐技术栈
- **Python 3** + **pyserial**（串口通信）+ **PyQt5/PySide6**（GUI）
- 或 **Python** + **tkinter**（更轻量）
- 波形显示: **pyqtgraph**（实时）或 **matplotlib**（离线）

### 关键实现要点

1. **收发分离**: USB CDC 收发应在独立线程，避免阻塞 GUI
2. **命令/数据分离**: USB 上传模式下，命令响应和 CSV 数据流共用同一接口。需要通过行首特征区分：
   - 命令响应以 `OK`/`ERR`/`ACQ`/`FLOW`/`SNAP`/`tick=` 等开头
   - CSV 数据行以数字开头（frame_id）
   - CSV 数据头以 `frame_id,` 开头
3. **自动重连**: USB CDC 设备可能因 `msc` 命令重启，需要处理断开事件
4. **波特率**: USB CDC 不需要真实波特率，随便填 115200 即可
5. **编码**: 设备输出全部是 ASCII 文本，无二进制协议

### 设备 VID/PID（用于自动识别）
STM32 USB CDC 默认 VID/PID 取决于 CubeMX 配置，常见为:
- VID: `0x0483` (STMicroelectronics)
- PID: `0x5740` (STM32 VCP)

---

## 9. 错误响应格式

所有错误响应以 `ERR` 开头:
```
ERR invalid sink (use usb or sd)
ERR acq_start failed
ERR: use lsm/h3/qma
ERR\r\n
```

设备不会主动断开连接，命令可反复发送。

---

## 10. 时序注意事项

- 设备 CDC 轮询间隔: 10ms，命令响应延迟 ≤ 10ms
- `status` 命令执行时间: < 5ms
- `s` 命令执行时间: < 50ms（涉及传感器寄存器写入 + SD 卡写入）
- `acq_start` 执行时间: < 100ms（涉及传感器重配置）
- USB 流模式数据延迟: 取决于传感器 ODR，最快约 0.15ms/帧 (6664Hz)
