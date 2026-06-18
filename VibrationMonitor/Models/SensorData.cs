using System;

namespace VibrationMonitor.Models
{
/// <summary>
/// LSM6DSOX 样本（来自 Bulk IN 端点 0x81，8 列）
/// frame_id, tick_ms, accX_mg, accY_mg, accZ_mg, gyroX_mdps, gyroY_mdps, gyroZ_mdps
/// </summary>
public record LsmSample
{
    public uint FrameId { get; init; }
    public DateTime Datetime { get; init; }  // YYMMDDHHMMSS → UTC DateTime
    public float AccX { get; init; }   // mg
    public float AccY { get; init; }
    public float AccZ { get; init; }
    public float GyroX { get; init; }  // mdps
    public float GyroY { get; init; }
    public float GyroZ { get; init; }
}

/// <summary>
/// H3LIS100DL 样本（来自 Bulk IN 端点 0x82，5 列）
/// frame_id, tick_ms, accX_mg, accY_mg, accZ_mg
/// </summary>
public record H3Sample
{
    public uint FrameId { get; init; }
    public DateTime Datetime { get; init; }
    public float AccX { get; init; }   // mg
    public float AccY { get; init; }
    public float AccZ { get; init; }
}

/// <summary>
/// QMA6100P 样本（来自 Bulk IN 端点 0x83，5 列，无标签）
/// frame_id, tick_ms, accX_mg, accY_mg, accZ_mg
/// </summary>
public record QmaSample
{
    public uint FrameId { get; init; }
    public DateTime Datetime { get; init; }
    public float AccX { get; init; }   // mg
    public float AccY { get; init; }
    public float AccZ { get; init; }
}

/// <summary>
/// 传感器配置信息
/// </summary>
public record SensorConfig
{
    public string LsmRange { get; init; } = "";
    public string LsmOdr { get; init; } = "";
    public string LsmGyroRange { get; init; } = "";
    public string LsmGyroOdr { get; init; } = "";
    public string H3Range { get; init; } = "";
    public string H3Odr { get; init; } = "";
    public string QmaRange { get; init; } = "";
    public string QmaOdr { get; init; } = "";
}

/// <summary>
/// 采集状态
/// </summary>
public record AcqState
{
    public string State { get; init; } = "stopped";
    public string Sink { get; init; } = "";
    public int DurationMs { get; init; }
    public int ElapsedMs { get; init; }
    public int RemainingMs { get; init; }
}

/// <summary>
/// 完整设备状态（来自命令响应端点 0x84 的 status 多行响应）
/// </summary>
public record DeviceStatus
{
    public uint Tick { get; init; }
    public uint Heap { get; init; }
    public uint FlowFrames { get; init; }
    public uint WriteFail { get; init; }
    public uint Stale { get; init; }
    public uint Coherent { get; init; }
    public uint LsmUpdates { get; init; }
    public uint H3Updates { get; init; }
    public uint QmaUpdates { get; init; }
    public uint SdWritten { get; init; }
    public uint UsbSent { get; init; }
    public uint LastFrame { get; init; }
    public uint QueueDepth { get; init; }
    public uint QueueDropped { get; init; }
    public uint QueueHigh { get; init; }
    public string ModeUsb { get; init; } = "";
    public string ModeSd { get; init; } = "";
    public AcqState Acq { get; init; } = new();
    public SensorConfig Config { get; init; } = new();
}
}
