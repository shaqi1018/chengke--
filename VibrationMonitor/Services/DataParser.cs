using System;
using System.Collections.Generic;
using System.Globalization;
using VibrationMonitor.Models;

namespace VibrationMonitor.Services
{
    public static class DataParser
    {
        // === 三路窄解析器（每个端点的 CSV 格式不同）===

        /// <summary>端点 0x81 — LSM6DSOX（8 列）: frame_id, tick_ms, accX, accY, accZ, gyroX, gyroY, gyroZ</summary>
        public static LsmSample? ParseLsm(string line)
        {
            try
            {
                var p = line.Split(',');
                if (p.Length < 8) return null;
                return new LsmSample
                {
                    FrameId = UInt(p[0]),
                    Datetime = ParseDatetime(p[1]),
                    AccX = Float(p[2]),
                    AccY = Float(p[3]),
                    AccZ = Float(p[4]),
                    GyroX = Float(p[5]),
                    GyroY = Float(p[6]),
                    GyroZ = Float(p[7]),
                };
            }
            catch { return null; }
        }

        /// <summary>端点 0x82 — H3LIS100DL（5 列）: frame_id, tick_ms, accX, accY, accZ</summary>
        public static H3Sample? ParseH3(string line)
        {
            try
            {
                var p = line.Split(',');
                if (p.Length < 5) return null;
                return new H3Sample
                {
                    FrameId = UInt(p[0]),
                    Datetime = ParseDatetime(p[1]),
                    AccX = Float(p[2]),
                    AccY = Float(p[3]),
                    AccZ = Float(p[4]),
                };
            }
            catch { return null; }
        }

        /// <summary>端点 0x83 — QMA6100P（5 列，无标签）: frame_id, tick_ms, accX, accY, accZ</summary>
        public static QmaSample? ParseQma(string line)
        {
            try
            {
                var p = line.Split(',');
                if (p.Length < 5) return null;
                return new QmaSample
                {
                    FrameId = UInt(p[0]),
                    Datetime = ParseDatetime(p[1]),
                    AccX = Float(p[2]),
                    AccY = Float(p[3]),
                    AccZ = Float(p[4]),
                };
            }
            catch { return null; }
        }

        public static DeviceStatus? ParseStatusResponse(string[] lines)
        {
            try
            {
                var status = new DeviceStatus();

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("tick="))
                    {
                        var dict = ParseKeyValue(trimmed);
                        status = status with
                        {
                            Tick = uint.Parse(dict.GetValueOrDefault("tick", "0")),
                            Heap = uint.Parse(dict.GetValueOrDefault("heap", "0"))
                        };
                    }
                    else if (trimmed.StartsWith("flow "))
                    {
                        var dict = ParseKeyValue(trimmed);
                        status = status with
                        {
                            FlowFrames = uint.Parse(dict.GetValueOrDefault("frames", "0")),
                            WriteFail = uint.Parse(dict.GetValueOrDefault("write_fail", "0")),
                            Stale = uint.Parse(dict.GetValueOrDefault("stale", "0")),
                            Coherent = uint.Parse(dict.GetValueOrDefault("coherent", "0"))
                        };
                    }
                    else if (trimmed.StartsWith("sensor "))
                    {
                        var dict = ParseKeyValue(trimmed);
                        status = status with
                        {
                            LsmUpdates = uint.Parse(dict.GetValueOrDefault("lsm", "0")),
                            H3Updates = uint.Parse(dict.GetValueOrDefault("h3", "0")),
                            QmaUpdates = uint.Parse(dict.GetValueOrDefault("qma", "0"))
                        };
                    }
                    else if (trimmed.StartsWith("frame "))
                    {
                        var dict = ParseKeyValue(trimmed);
                        status = status with
                        {
                            SdWritten = uint.Parse(dict.GetValueOrDefault("sd_written", "0")),
                            UsbSent = uint.Parse(dict.GetValueOrDefault("usb_sent", "0")),
                            LastFrame = uint.Parse(dict.GetValueOrDefault("last_frame", "0")),
                            QueueDepth = uint.Parse(dict.GetValueOrDefault("depth", "0")),
                            QueueDropped = uint.Parse(dict.GetValueOrDefault("dropped", "0")),
                            QueueHigh = uint.Parse(dict.GetValueOrDefault("high", "0"))
                        };
                    }
                    else if (trimmed.StartsWith("mode "))
                    {
                        var dict = ParseKeyValue(trimmed);
                        status = status with
                        {
                            ModeUsb = dict.GetValueOrDefault("usb", ""),
                            ModeSd = dict.GetValueOrDefault("sd", "")
                        };
                    }
                    else if (trimmed.StartsWith("acq "))
                    {
                        var dict = ParseKeyValue(trimmed);
                        status = status with
                        {
                            Acq = new AcqState
                            {
                                State = dict.GetValueOrDefault("state", "stopped"),
                                Sink = dict.GetValueOrDefault("sink", ""),
                                DurationMs = int.Parse(dict.GetValueOrDefault("duration_ms", "0")),
                                ElapsedMs = int.Parse(dict.GetValueOrDefault("elapsed_ms", "0")),
                                RemainingMs = int.Parse(dict.GetValueOrDefault("remaining_ms", "0"))
                            }
                        };
                    }
                    else if (trimmed.StartsWith("cfg "))
                    {
                        var dict = ParseKeyValue(trimmed);
                        var lsm = dict.GetValueOrDefault("lsm", "");
                        var gyro = dict.GetValueOrDefault("gyro", "");
                        var h3 = dict.GetValueOrDefault("h3", "");
                        var qma = dict.GetValueOrDefault("qma", "");

                        status = status with
                        {
                            Config = new SensorConfig
                            {
                                LsmRange = ExtractPart(lsm, 0),
                                LsmOdr = ExtractPart(lsm, 1),
                                LsmGyroRange = ExtractPart(gyro, 0),
                                LsmGyroOdr = ExtractPart(gyro, 1),
                                H3Range = ExtractPart(h3, 0),
                                H3Odr = ExtractPart(h3, 1),
                                QmaRange = ExtractPart(qma, 0),
                                QmaOdr = ExtractPart(qma, 1)
                            }
                        };
                    }
                }

                return status;
            }
            catch
            {
                return null;
            }
        }

        private static float Float(string s) =>
            float.Parse(s.Trim(), CultureInfo.InvariantCulture);

        private static uint UInt(string s) =>
            uint.Parse(s.Trim(), CultureInfo.InvariantCulture);

        // YYMMDDHHMMSS 12位定长 → UTC DateTime
        // 新固件可能在该字段前缀非数字字符（如 'F'），先剥除所有非数字再解析，
        // 兼容旧版纯数字与新版带前缀两种格式。
        private static DateTime ParseDatetime(string s)
        {
            var raw = s.Trim();
            // 仅保留数字
            Span<char> digits = stackalloc char[raw.Length];
            int len = 0;
            foreach (var ch in raw)
                if (ch >= '0' && ch <= '9') digits[len++] = ch;
            var n = new string(digits[..len]);
            if (n.Length < 12)
                return DateTime.UtcNow; // 字段异常时退回当前时间，避免整行丢弃
            // 取最后 12 位（防前缀残留多位）
            n = n.Substring(n.Length - 12);
            return new DateTime(
                2000 + int.Parse(n.Substring(0, 2)),
                int.Parse(n.Substring(2, 2)),
                int.Parse(n.Substring(4, 2)),
                int.Parse(n.Substring(6, 2)),
                int.Parse(n.Substring(8, 2)),
                int.Parse(n.Substring(10, 2)),
                DateTimeKind.Utc);
        }

        private static Dictionary<string, string> ParseKeyValue(string input)
        {
            var dict = new Dictionary<string, string>();
            foreach (var part in input.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0)
                    dict[part[..eq]] = part[(eq + 1)..];
            }
            return dict;
        }

        private static string ExtractPart(string s, int index)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var parts = s.Split('/');
            return index < parts.Length ? parts[index] : "";
        }
    }
}
