using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using VibrationMonitor.Models;
using VibrationMonitor.Services;

namespace VibrationMonitor.ViewModels
{
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly WinUsbDeviceManager _usb = new();
    private readonly DeviceCommander _commander;
    private readonly DispatcherTimer _chartTimer;

    // 各路独立数据缓冲（不再合并成一帧）
    private readonly List<LsmSample> _lsmBuffer = new();
    private readonly List<H3Sample> _h3Buffer = new();
    private readonly List<QmaSample> _qmaBuffer = new();
    private readonly List<MagSample> _magBuffer = new();   // LIS2MDL 磁力 100Hz
    private readonly List<AhtSample> _ahtBuffer = new();   // AHT20 温湿度 1Hz
    private readonly object _bufferLock = new();

    private int _dataRateCounter;
    private DateTime _dataRateStart = DateTime.Now;

    // 录制：每路一个文件
    private StreamWriter? _lsmCsv;
    private StreamWriter? _h3Csv;
    private StreamWriter? _qmaCsv;
    private StreamWriter? _magCsv;
    private StreamWriter? _ahtCsv;

    // 麦克风录制：原始 PCM 落盘，停止时补写 WAV 头
    private FileStream? _micWav;
    private string? _micWavPath;
    private long _micPcmBytes;
    private int _micWavSampleRate = 96000;
    private readonly object _micLock = new();

    // === OxyPlot 图表 ===
    public PlotModel LsmAccPlot { get; }
    public PlotModel LsmGyroPlot { get; }
    public PlotModel H3AccPlot { get; }
    public PlotModel QmaAccPlot { get; }
    public PlotModel MagPlot { get; }

    private readonly LineSeries _lsmAccXSeries, _lsmAccYSeries, _lsmAccZSeries;
    private readonly LineSeries _lsmGyroXSeries, _lsmGyroYSeries, _lsmGyroZSeries;
    private readonly LineSeries _h3AccXSeries, _h3AccYSeries, _h3AccZSeries;
    private readonly LineSeries _qmaAccXSeries, _qmaAccYSeries, _qmaAccZSeries;
    private readonly LineSeries _magXSeries, _magYSeries, _magZSeries;

    // === 设备发现 / 连接 ===
    public ObservableCollection<string> Ports { get; } = new();
    private string _selectedPort = "";
    public string SelectedPort { get => _selectedPort; set => Set(ref _selectedPort, value); }

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; set { Set(ref _isConnected, value); OnPropertyChanged(nameof(ButtonText)); } }

    public string ButtonText => IsConnected ? "断开" : "连接";

    // === 设备状态 ===
    private string _statusText = "未连接";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private DeviceStatus? _deviceStatus;
    public DeviceStatus? DeviceStatus { get => _deviceStatus; set { Set(ref _deviceStatus, value); OnPropertyChanged(nameof(AcqStateText)); OnPropertyChanged(nameof(FrameCountText)); } }

    public string AcqStateText => AcqRunning ? "运行中" : "已停止";

    public string FrameCountText => DeviceStatus != null
        ? $"帧:{DeviceStatus.FlowFrames} 丢:{DeviceStatus.QueueDropped} USB:{DeviceStatus.UsbSent} SD:{DeviceStatus.SdWritten}"
        : "";

    // === 采集控制 ===
    private string _acqSink = "usb";
    public string AcqSink { get => _acqSink; set => Set(ref _acqSink, value); }

    private int _acqDuration;
    public int AcqDuration { get => _acqDuration; set => Set(ref _acqDuration, value); }

    private bool _acqRunning;
    public bool AcqRunning { get => _acqRunning; set { Set(ref _acqRunning, value); OnPropertyChanged(nameof(AcqStateText)); } }

    // === 传感器配置 ===
    public ObservableCollection<string> LsmOdrValues { get; } = new() { "12", "26", "52", "104", "208", "416", "833", "1666", "3332", "6664" };
    public ObservableCollection<string> LsmRangeValues { get; } = new() { "2", "4", "8", "16" };
    public ObservableCollection<string> H3OdrValues { get; } = new() { "50", "100", "400" };
    public ObservableCollection<string> QmaOdrValues { get; } = new() { "12", "25", "50", "80", "200", "400", "800", "1600" };
    public ObservableCollection<string> QmaRangeValues { get; } = new() { "2", "4", "8", "16", "32" };

    private string _lsmOdr = "833";
    public string LsmOdr { get => _lsmOdr; set => Set(ref _lsmOdr, value); }
    private string _lsmRange = "4";
    public string LsmRange { get => _lsmRange; set => Set(ref _lsmRange, value); }
    private string _h3Odr = "400";
    public string H3Odr { get => _h3Odr; set => Set(ref _h3Odr, value); }
    private string _qmaOdr = "1600";
    public string QmaOdr { get => _qmaOdr; set => Set(ref _qmaOdr, value); }

    private string _qmaRange = "8";
    public string QmaRange { get => _qmaRange; set => Set(ref _qmaRange, value); }

    // LIS2MDL 磁力 ODR：固件吸附到最近档 10/20/50/100 Hz
    public ObservableCollection<string> MagOdrValues { get; } = new() { "10", "20", "50", "100" };
    private string _magOdr = "100";
    public string MagOdr { get => _magOdr; set => Set(ref _magOdr, value); }

    // === 麦克风配置（端点 0x84 原始 PCM）===
    public ObservableCollection<string> MicSrValues { get; } = new() { "8000", "16000", "48000", "96000" };
    private string _micSr = "96000";
    public string MicSr { get => _micSr; set => Set(ref _micSr, value); }
    private string _micGain = "24";
    public string MicGain { get => _micGain; set => Set(ref _micGain, value); }

    // === 传感器/麦克风启用开关：勾选即发命令（s <sensor> en 0/1），无需"应用" ===
    // 程序内部赋初值时置 true 抑制发送，仅用户交互才发命令。
    private bool _suppressEnableCmd;

    private bool _lsmEnabled = true;
    public bool LsmEnabled { get => _lsmEnabled; set { if (Set(ref _lsmEnabled, value)) SendEnable("lsm", value); } }
    private bool _h3Enabled = true;
    public bool H3Enabled { get => _h3Enabled; set { if (Set(ref _h3Enabled, value)) SendEnable("h3", value); } }
    private bool _qmaEnabled = true;
    public bool QmaEnabled { get => _qmaEnabled; set { if (Set(ref _qmaEnabled, value)) SendEnable("qma", value); } }
    private bool _micEnabled = true;
    public bool MicEnabled { get => _micEnabled; set { if (Set(ref _micEnabled, value)) SendEnable("mic", value); } }
    private bool _magEnabled = true;
    public bool MagEnabled { get => _magEnabled; set { if (Set(ref _magEnabled, value)) SendEnable("mag", value); } }
    private bool _ahtEnabled = true;
    public bool AhtEnabled { get => _ahtEnabled; set { if (Set(ref _ahtEnabled, value)) SendEnable("aht", value); } }

    // AHT20 最新读数（1Hz），显示在顶部状态栏
    private string _ahtText = "温湿度 --";
    public string AhtText { get => _ahtText; set => Set(ref _ahtText, value); }

    // 发送启用/禁用命令；mic 走 SetMicParam，其余走 SetSensorParam。
    private void SendEnable(string sensor, bool on)
    {
        if (_suppressEnableCmd) return;
        if (!_usb.IsConnected) return;
        var val = on ? "1" : "0";
        System.Threading.Tasks.Task.Run(() =>
        {
            if (sensor == "mic") _commander.SetMicParam("en", val);
            else _commander.SetSensorParam(sensor, "en", val);
        });
    }

    private double _dataRate;
    public double DataRate { get => _dataRate; set => Set(ref _dataRate, value); }

    // === 日志 ===
    public ObservableCollection<string> LogLines { get; } = new();

    // === 录制 ===
    private bool _isRecording;
    public bool IsRecording { get => _isRecording; set => Set(ref _isRecording, value); }

    // === 图表更新标记 ===
    private volatile bool _chartDirty;
    private DateTime _lastFrameTime = DateTime.Now;
    private bool _dataStalled;

    // === 命令 ===
    public ICommand RefreshPortsCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand AcqStartCommand { get; }
    public ICommand AcqStopCommand { get; }
    public ICommand ApplyLsmOdrCommand { get; }
    public ICommand ApplyLsmRangeCommand { get; }
    public ICommand ApplyH3OdrCommand { get; }
    public ICommand ApplyQmaOdrCommand { get; }
    public ICommand ApplyQmaRangeCommand { get; }
    public ICommand ApplyMagOdrCommand { get; }
    public ICommand ApplyMicGainCommand { get; }
    public ICommand ApplyMicSrCommand { get; }
    public ICommand ApplyMicEnCommand { get; }
    public ICommand SetRtcTimeCommand { get; }
    public ICommand StatusCommand { get; }
    public ICommand MscCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand ToggleRecordCommand { get; }
    public ICommand ExportCsvCommand { get; }

    private const int ChartPoints = 2000;   // 实时滚动窗口大小
    private const int BufferCap = 1_000_000; // 完整采集缓冲（100s@6664Hz≈66万帧）

    // X=红, Y=绿, Z=蓝 (适合白色背景的深色)
    private static readonly OxyColor ColorX = OxyColor.FromRgb(211, 47, 47);
    private static readonly OxyColor ColorY = OxyColor.FromRgb(56, 142, 60);
    private static readonly OxyColor ColorZ = OxyColor.FromRgb(25, 118, 210);

    public MainViewModel()
    {
        LsmAccPlot = CreatePlot("LSM6DSOX 加速度", "mg");
        _lsmAccXSeries = AddSeries(LsmAccPlot, "X", ColorX);
        _lsmAccYSeries = AddSeries(LsmAccPlot, "Y", ColorY);
        _lsmAccZSeries = AddSeries(LsmAccPlot, "Z", ColorZ);

        LsmGyroPlot = CreatePlot("LSM6DSOX 陀螺仪", "mdps");
        _lsmGyroXSeries = AddSeries(LsmGyroPlot, "X", ColorX);
        _lsmGyroYSeries = AddSeries(LsmGyroPlot, "Y", ColorY);
        _lsmGyroZSeries = AddSeries(LsmGyroPlot, "Z", ColorZ);

        H3AccPlot = CreatePlot("H3LIS100DL 加速度", "mg");
        _h3AccXSeries = AddSeries(H3AccPlot, "X", ColorX);
        _h3AccYSeries = AddSeries(H3AccPlot, "Y", ColorY);
        _h3AccZSeries = AddSeries(H3AccPlot, "Z", ColorZ);

        QmaAccPlot = CreatePlot("QMA6100P 加速度", "mg");
        _qmaAccXSeries = AddSeries(QmaAccPlot, "X", ColorX);
        _qmaAccYSeries = AddSeries(QmaAccPlot, "Y", ColorY);
        _qmaAccZSeries = AddSeries(QmaAccPlot, "Z", ColorZ);

        MagPlot = CreatePlot("LIS2MDL 磁力", "mG");
        _magXSeries = AddSeries(MagPlot, "X", ColorX);
        _magYSeries = AddSeries(MagPlot, "Y", ColorY);
        _magZSeries = AddSeries(MagPlot, "Z", ColorZ);

        _commander = new DeviceCommander(_usb);
        _commander.LogMessage += msg =>
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    LogLines.Add(msg);
                    if (LogLines.Count > 500) LogLines.RemoveAt(0);
                });
            }
            catch { }
        };

        // 三路独立数据流 + 命令响应
        _usb.LsmLineReceived += OnLsmLine;
        _usb.H3LineReceived += OnH3Line;
        _usb.QmaLineReceived += OnQmaLine;
        _usb.MicDataReceived += OnMicData;
        _usb.MagLineReceived += OnMagLine;
        _usb.AhtLineReceived += OnAhtLine;
        _usb.ResponseReceived += OnResponseLine;

        _usb.Disconnected += () =>
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _chartTimer!.Stop();
                    IsConnected = false;
                });
            }
            catch { }
        };
        _usb.ErrorOccurred += err =>
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    LogLines.Add("[ERR] " + err);
                });
            }
            catch { }
        };
        _usb.ConnectionChanged += connected =>
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (connected)
                    {
                        IsConnected = true;
                        StatusText = $"已连接 {_usb.DeviceName}";
                    }
                    else if (StatusText != "已断开")
                    {
                        IsConnected = false;
                        StatusText = "连接已断开";
                    }
                });
            }
            catch { }
        };

        // 命令
        RefreshPortsCommand = new RelayCommand(_ => RefreshPorts());
        ConnectCommand = new RelayCommand(_ => ToggleConnect());
        AcqStartCommand = new RelayCommand(_ => DoAcqStart());
        AcqStopCommand = new RelayCommand(_ => DoAcqStop());
        ApplyLsmOdrCommand = new RelayCommand(_ => _commander.SetSensorParam("lsm", "odr", StripNonDigit(LsmOdr)));
        ApplyLsmRangeCommand = new RelayCommand(_ => _commander.SetSensorParam("lsm", "range", StripNonDigit(LsmRange)));
        ApplyH3OdrCommand = new RelayCommand(_ => _commander.SetSensorParam("h3", "odr", StripNonDigit(H3Odr)));
        ApplyQmaOdrCommand = new RelayCommand(_ => _commander.SetSensorParam("qma", "odr", StripNonDigit(QmaOdr)));
        ApplyQmaRangeCommand = new RelayCommand(_ => _commander.SetSensorParam("qma", "range", StripNonDigit(QmaRange)));
        ApplyMagOdrCommand = new RelayCommand(_ => _commander.SetSensorParam("mag", "odr", StripNonDigit(MagOdr)));
        ApplyMicGainCommand = new RelayCommand(_ => _commander.SetMicParam("gain", StripNonDigit(MicGain)));
        ApplyMicSrCommand = new RelayCommand(_ => _commander.SetMicParam("sr", StripNonDigit(MicSr)));
        ApplyMicEnCommand = new RelayCommand(_ => _commander.SetMicParam("en", MicEnabled ? "1" : "0"));
        SetRtcTimeCommand = new RelayCommand(_ => DoSetRtcTime());
        StatusCommand = new RelayCommand(_ => DoStatus());
        MscCommand = new RelayCommand(_ => DoMsc());
        ClearLogCommand = new RelayCommand(_ => LogLines.Clear());
        ToggleRecordCommand = new RelayCommand(_ => ToggleRecord());
        ExportCsvCommand = new RelayCommand(_ => ExportCsv());

        // 图表刷新定时器 (30fps)
        _chartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _chartTimer.Tick += (s, e) => UpdateCharts();

        RefreshPorts();
    }

    // === OxyPlot 辅助方法 ===

    private static PlotModel CreatePlot(string title, string unit)
    {
        var model = new PlotModel
        {
            Title = title,
            Background = OxyColors.White,
            PlotAreaBackground = OxyColors.White,
            TextColor = OxyColor.FromRgb(68, 68, 96),
            TitleColor = OxyColor.FromRgb(26, 26, 46),
            PlotAreaBorderColor = OxyColor.FromRgb(221, 224, 230),
        };

        var legend = new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.TopRight,
            LegendPlacement = OxyPlot.Legends.LegendPlacement.Inside,
            LegendBackground = OxyColor.FromArgb(200, 255, 255, 255),
            LegendBorder = OxyColor.FromRgb(221, 224, 230),
            LegendFontSize = 11,
        };
        model.Legends.Add(legend);

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "时间 (s)",
            TitleColor = OxyColor.FromRgb(136, 136, 170),
            TicklineColor = OxyColor.FromRgb(221, 224, 230),
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(232, 234, 239),
            MinorGridlineColor = OxyColor.FromRgb(240, 242, 245),
        });

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = unit,
            TitleColor = OxyColor.FromRgb(136, 136, 170),
            TicklineColor = OxyColor.FromRgb(221, 224, 230),
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(232, 234, 239),
        });

        return model;
    }

    private static LineSeries AddSeries(PlotModel model, string title, OxyColor color)
    {
        var series = new LineSeries
        {
            Title = title,
            Color = color,
            StrokeThickness = 1.5,
            MarkerType = MarkerType.None,
            LineStyle = LineStyle.Solid,
        };
        model.Series.Add(series);
        return series;
    }

    // === 方法 ===

    private static string StripNonDigit(string value)
    {
        // 保留数字和小数点（如 12.5），其余字符（Hz、空格等）剔除
        return new string(value.Where(c => char.IsDigit(c) || c == '.').ToArray());
    }

    private void RefreshPorts()
    {
        Ports.Clear();
        foreach (var (id, desc) in WinUsbDeviceManager.ScanDevices())
            Ports.Add($"★ {desc}");

        if (Ports.Count == 0)
            Ports.Add("（未检测到 WCID 设备）");

        if (Ports.Count > 0)
            SelectedPort = Ports[0];
    }

    private void ToggleConnect()
    {
        if (IsConnected)
        {
            _chartTimer.Stop();
            _usb.Disconnect();
            IsConnected = false;
            StatusText = "已断开";
        }
        else
        {
            StatusText = "正在连接 WCID 设备...";
            // 在后台线程做耗时的 CreateFile + WinUsb_Initialize，避免卡住 UI
            System.Threading.Tasks.Task.Run(() =>
            {
                bool ok = _usb.Connect();
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (ok)
                    {
                        IsConnected = true;
                        StatusText = $"已连接 {_usb.DeviceName}";
                        _chartTimer.Start();

                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                var status = _commander.Status();
                                if (status != null)
                                    Application.Current?.Dispatcher.BeginInvoke(() =>
                                        DeviceStatus = status);
                            }
                            catch { }
                        });
                    }
                    else
                    {
                        StatusText = "连接失败";
                    }
                });
            });
        }
    }

    private void DoAcqStart()
    {
        if (!_usb.IsConnected) return;

        ClearCharts();

        var durationMs = AcqDuration * 1000;

        System.Threading.Tasks.Task.Run(() =>
        {
            var result = _commander.AcqStart(AcqSink, durationMs);

            // 先开录制再查状态：Status() 是阻塞收集（固定等满 ~3s）。若放在落盘之前，会把
            // 录制起点整体推迟数秒 —— 定时采集会丢掉开头数秒（设 9s 实际只录到 6s）。
            // 故 acq_start 一返回就立即开录，把 Status() 移到其后（其阻塞不再影响落盘起点）。
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (result)
                {
                    AcqRunning = true;
                    if (AcqSink == "usb")
                        StartCsvRecording();
                }
                else
                {
                    AcqRunning = false;
                }
            });

            // 录制已起，再查一次状态用于顶栏显示。WAV 头采样率以用户在 UI 选定的 MicSr 为准，
            // 不用固件 status 的 mic sr 覆盖（该字段不可靠，会把 48k 误报成 96k 致回放变调）。
            if (result)
            {
                try
                {
                    var st = _commander.Status();
                    if (st != null)
                        Application.Current?.Dispatcher.BeginInvoke(() => DeviceStatus = st);
                }
                catch { }
            }
        });
    }

    private void DoAcqStop()
    {
        AcqRunning = false;
        if (IsRecording) StopCsvRecording();
        LogLines.Add(">> acq_stop");
        System.Threading.Tasks.Task.Run(() =>
        {
            _commander.AcqStop();
        });
    }

    private void DoStatus()
    {
        if (!IsConnected) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            var status = _commander.Status();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (status != null) DeviceStatus = status;
            });
        });
    }

    private void DoSetRtcTime()
    {
        if (!IsConnected) return;
        var now = DateTime.Now;
        System.Threading.Tasks.Task.Run(() => _commander.SetRtcTime(now));
    }

    private void DoMsc()
    {
        var result = MessageBox.Show(
            "切换到 MSC 模式后设备会重启，USB 连接将断开。\nSD 卡将作为 U 盘挂载。\n\n确认切换？",
            "切换到 MSC 模式", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _commander.Msc();
            _chartTimer.Stop();
            DeviceStatus = null;
            AcqRunning = false;

            // 延迟断开，让设备有时间处理 boot_msc 命令
            System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _usb.Disconnect();
                    RefreshPorts();
                });
            });
        }
    }

    private void ToggleRecord()
    {
        if (IsRecording)
            StopCsvRecording();
        else
            StartCsvRecording();
    }

    private void StartCsvRecording()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            // 单文件发布(IncludeAllContentForSelfExtract)运行时会解压到临时目录，
            // AppDomain.BaseDirectory 指向临时解压目录而非 exe 所在目录，导致录制文件“消失”。
            // 用 Environment.ProcessPath 取真实 exe 路径，把 Recordings 放在 exe 旁边。
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var sessionDir = Path.Combine(exeDir, "Recordings", stamp);
            Directory.CreateDirectory(sessionDir);

            _lsmCsv = new StreamWriter(Path.Combine(sessionDir, "lsm.csv"));
            _lsmCsv.WriteLine("frame_id,datetime,accX_mg,accY_mg,accZ_mg,gyroX_mdps,gyroY_mdps,gyroZ_mdps");

            _h3Csv = new StreamWriter(Path.Combine(sessionDir, "h3.csv"));
            _h3Csv.WriteLine("frame_id,datetime,accX_mg,accY_mg,accZ_mg");

            _qmaCsv = new StreamWriter(Path.Combine(sessionDir, "qma.csv"));
            _qmaCsv.WriteLine("frame_id,datetime,accX_mg,accY_mg,accZ_mg");

            _magCsv = new StreamWriter(Path.Combine(sessionDir, "mag.csv"));
            _magCsv.WriteLine("frame_id,datetime,x_mG,y_mG,z_mG");

            _ahtCsv = new StreamWriter(Path.Combine(sessionDir, "aht_env.csv"));
            _ahtCsv.WriteLine("frame_id,datetime,temp_C,humidity_pct");

            // 麦克风：先写占位 WAV 头（44字节），随后追加原始 PCM，停止时回填长度字段
            StartMicWav(Path.Combine(sessionDir, "mic.wav"));

            IsRecording = true;
            LogLines.Add($"[REC] 开始录制 → Recordings\\{stamp}\\");
        }
        catch (Exception ex)
        {
            LogLines.Add($"[ERR] 录制失败: {ex.Message}");
        }
    }

    private void StopCsvRecording()
    {
        foreach (var w in new[] { _lsmCsv, _h3Csv, _qmaCsv, _magCsv, _ahtCsv })
        {
            try { w?.Flush(); w?.Dispose(); } catch { }
        }
        _lsmCsv = _h3Csv = _qmaCsv = _magCsv = _ahtCsv = null;
        StopMicWav();
        _usb.LsmRawDumpPath = null;   // 停止原始字节转储
        IsRecording = false;
        LogLines.Add("[REC] 录制已停止");
    }

    // 麦克风 WAV：写 44 字节 PCM 头占位（长度字段先填 0），后续追加原始 PCM。
    private void StartMicWav(string path)
    {
        lock (_micLock)
        {
            try
            {
                _micWavSampleRate = int.TryParse(StripNonDigit(MicSr), out var sr) && sr > 0 ? sr : 96000;
                _micPcmBytes = 0;
                _micWavPath = path;
                _micWav = new FileStream(path, FileMode.Create, FileAccess.Write);
                _micWav.Write(BuildWavHeader(_micWavSampleRate, 0), 0, 44);
            }
            catch
            {
                _micWav = null;
                _micWavPath = null;
            }
        }
    }

    // 停止时回填 WAV 头的 RIFF/data 长度字段，使文件可被播放器识别。
    private void StopMicWav()
    {
        lock (_micLock)
        {
            if (_micWav == null) { _micWavPath = null; return; }
            try
            {
                _micWav.Seek(0, SeekOrigin.Begin);
                _micWav.Write(BuildWavHeader(_micWavSampleRate, _micPcmBytes), 0, 44);
                _micWav.Flush();
            }
            catch { }
            finally
            {
                try { _micWav.Dispose(); } catch { }
                _micWav = null;
            }

            // PCM 字节为 0（设备未发音频/未启用 mic）则删除空文件，避免误导
            if (_micPcmBytes == 0 && _micWavPath != null)
            {
                try { File.Delete(_micWavPath); } catch { }
            }
            _micWavPath = null;
        }
    }

    // 标准 16-bit 单声道 PCM WAV 头（44 字节）。dataBytes 为 PCM 负载字节数。
    private static byte[] BuildWavHeader(int sampleRate, long dataBytes)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        uint dataLen = (uint)Math.Min(dataBytes, uint.MaxValue - 44);
        uint riffLen = 36 + dataLen;

        var h = new byte[44];
        void Str(int o, string s) { for (int i = 0; i < s.Length; i++) h[o + i] = (byte)s[i]; }
        void U32(int o, uint v) { h[o] = (byte)v; h[o + 1] = (byte)(v >> 8); h[o + 2] = (byte)(v >> 16); h[o + 3] = (byte)(v >> 24); }
        void U16(int o, ushort v) { h[o] = (byte)v; h[o + 1] = (byte)(v >> 8); }

        Str(0, "RIFF");   U32(4, riffLen);   Str(8, "WAVE");
        Str(12, "fmt ");  U32(16, 16);       U16(20, 1);              // PCM
        U16(22, (ushort)channels); U32(24, (uint)sampleRate); U32(28, (uint)byteRate);
        U16(32, (ushort)blockAlign); U16(34, (ushort)bitsPerSample);
        Str(36, "data");  U32(40, dataLen);
        return h;
    }

    private void ExportCsv()
    {
        LsmSample[] lsm;
        H3Sample[] h3;
        QmaSample[] qma;
        MagSample[] mag;
        AhtSample[] aht;
        lock (_bufferLock)
        {
            lsm = _lsmBuffer.ToArray();
            h3 = _h3Buffer.ToArray();
            qma = _qmaBuffer.ToArray();
            mag = _magBuffer.ToArray();
            aht = _ahtBuffer.ToArray();
        }

        if (lsm.Length == 0 && h3.Length == 0 && qma.Length == 0 && mag.Length == 0 && aht.Length == 0)
        {
            LogLines.Add("[EXP] 无数据可导出");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"vibration_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var dir = Path.GetDirectoryName(dialog.FileName) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(dialog.FileName);
            var ci = CultureInfo.InvariantCulture;

            var lsmPath = Path.Combine(dir, baseName + "_lsm.csv");
            using (var w = new StreamWriter(lsmPath))
            {
                w.WriteLine("frame_id,datetime,accX_mg,accY_mg,accZ_mg,gyroX_mdps,gyroY_mdps,gyroZ_mdps");
                foreach (var s in lsm)
                    w.WriteLine(string.Format(ci, "{0},{1},{2},{3},{4},{5},{6},{7}",
                        s.FrameId, s.Datetime.ToString("yyMMddHHmmss"), s.AccX, s.AccY, s.AccZ, s.GyroX, s.GyroY, s.GyroZ));
            }

            var h3Path = Path.Combine(dir, baseName + "_h3.csv");
            using (var w = new StreamWriter(h3Path))
            {
                w.WriteLine("frame_id,datetime,accX_mg,accY_mg,accZ_mg");
                foreach (var s in h3)
                    w.WriteLine(string.Format(ci, "{0},{1},{2},{3},{4}",
                        s.FrameId, s.Datetime.ToString("yyMMddHHmmss"), s.AccX, s.AccY, s.AccZ));
            }

            var qmaPath = Path.Combine(dir, baseName + "_qma.csv");
            using (var w = new StreamWriter(qmaPath))
            {
                w.WriteLine("frame_id,datetime,accX_mg,accY_mg,accZ_mg");
                foreach (var s in qma)
                    w.WriteLine(string.Format(ci, "{0},{1},{2},{3},{4}",
                        s.FrameId, s.Datetime.ToString("yyMMddHHmmss"), s.AccX, s.AccY, s.AccZ));
            }

            var magPath = Path.Combine(dir, baseName + "_mag.csv");
            using (var w = new StreamWriter(magPath))
            {
                w.WriteLine("frame_id,datetime,x_mG,y_mG,z_mG");
                foreach (var s in mag)
                    w.WriteLine(string.Format(ci, "{0},{1},{2},{3},{4}",
                        s.FrameId, s.Datetime.ToString("yyMMddHHmmss"), s.X, s.Y, s.Z));
            }

            var ahtPath = Path.Combine(dir, baseName + "_aht.csv");
            using (var w = new StreamWriter(ahtPath))
            {
                w.WriteLine("frame_id,datetime,temp_C,humidity_pct");
                foreach (var s in aht)
                    w.WriteLine(string.Format(ci, "{0},{1},{2},{3}",
                        s.FrameId, s.Datetime.ToString("yyMMddHHmmss"), s.TempC, s.Humidity));
            }

            LogLines.Add($"[EXP] 已导出 LSM:{lsm.Length} H3:{h3.Length} QMA:{qma.Length} MAG:{mag.Length} AHT:{aht.Length} → {baseName}_(lsm|h3|qma|mag|aht).csv");
        }
        catch (Exception ex)
        {
            LogLines.Add($"[EXP] 导出失败: {ex.Message}");
        }
    }

    // === 数据接收（三路独立端点）===

    private void OnResponseLine(string line)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LogLines.Add("<< " + line);
            if (LogLines.Count > 500) LogLines.RemoveAt(0);

            // 固件采集完成通知：更新状态，触发全量图表刷新
            if (line.Trim() == "DONE" && AcqRunning)
            {
                AcqRunning = false;
                if (IsRecording) StopCsvRecording();
                _chartDirty = true;
            }
        });
    }

    private void OnLsmLine(string line)
    {
        var s = DataParser.ParseLsm(line);
        if (s == null) return;
        MarkData();
        lock (_bufferLock)
        {
            _lsmBuffer.Add(s);
            if (_lsmBuffer.Count > BufferCap) _lsmBuffer.RemoveRange(0, _lsmBuffer.Count - BufferCap);
        }
        try { _lsmCsv?.WriteLine(line); } catch { }
    }

    private void OnH3Line(string line)
    {
        var s = DataParser.ParseH3(line);
        if (s == null) return;
        MarkData();
        lock (_bufferLock)
        {
            _h3Buffer.Add(s);
            if (_h3Buffer.Count > BufferCap) _h3Buffer.RemoveRange(0, _h3Buffer.Count - BufferCap);
        }
        try { _h3Csv?.WriteLine(line); } catch { }
    }

    private void OnQmaLine(string line)
    {
        var s = DataParser.ParseQma(line);
        if (s == null) return;
        MarkData();
        lock (_bufferLock)
        {
            _qmaBuffer.Add(s);
            if (_qmaBuffer.Count > BufferCap) _qmaBuffer.RemoveRange(0, _qmaBuffer.Count - BufferCap);
        }
        try { _qmaCsv?.WriteLine(line); } catch { }
    }

    // LIS2MDL 磁力（0x85 上 "mag," 分流，100Hz）。
    private void OnMagLine(string line)
    {
        var s = DataParser.ParseMag(line);
        if (s == null) return;
        MarkData();
        lock (_bufferLock)
        {
            _magBuffer.Add(s);
            if (_magBuffer.Count > BufferCap) _magBuffer.RemoveRange(0, _magBuffer.Count - BufferCap);
        }
        try
        {
            _magCsv?.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4}",
                s.FrameId, s.Datetime.ToString("yyMMddHHmmss"), s.X, s.Y, s.Z));
        }
        catch { }
    }

    // AHT20 温湿度（0x85 上 "aht," 分流，约 1Hz）。更新顶部读数并入缓冲。
    private void OnAhtLine(string line)
    {
        var s = DataParser.ParseAht(line);
        if (s == null) return;
        MarkData();
        lock (_bufferLock)
        {
            _ahtBuffer.Add(s);
            if (_ahtBuffer.Count > BufferCap) _ahtBuffer.RemoveRange(0, _ahtBuffer.Count - BufferCap);
        }
        try
        {
            _ahtCsv?.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                s.FrameId, s.Datetime.ToString("yyMMddHHmmss"), s.TempC, s.Humidity));
        }
        catch { }

        var temp = s.TempC; var hum = s.Humidity;
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
                AhtText = string.Format(CultureInfo.InvariantCulture, "温度 {0:F1}°C  湿度 {1:F1}%", temp, hum));
        }
        catch { }
    }

    // 麦克风原始 PCM（16-bit 小端，单声道）。录制时直接落盘，停止时补 WAV 头。
    private void OnMicData(byte[] buffer, int length)
    {
        lock (_micLock)
        {
            if (_micWav == null) return;
            try
            {
                _micWav.Write(buffer, 0, length);
                _micPcmBytes += length;
            }
            catch { }
        }
    }

    private void MarkData()
    {
        // 三路读线程并发调用，用 Interlocked 计数
        System.Threading.Interlocked.Increment(ref _dataRateCounter);
        _lastFrameTime = DateTime.Now;
        _dataStalled = false;
        _chartDirty = true;
    }

    private void ClearCharts()
    {
        lock (_bufferLock)
        {
            _lsmBuffer.Clear();
            _h3Buffer.Clear();
            _qmaBuffer.Clear();
            _magBuffer.Clear();
            _ahtBuffer.Clear();
        }
        // 重新 acq_start 会重置下位机双缓冲，清空各端点拼接缓冲，避免跨会话残留半行
        _usb.ClearBuffers();

        _chartDirty = false;
        _dataStalled = false;
        _lastFrameTime = DateTime.MinValue;

        foreach (var s in new[] { _lsmAccXSeries, _lsmAccYSeries, _lsmAccZSeries,
                                  _lsmGyroXSeries, _lsmGyroYSeries, _lsmGyroZSeries,
                                  _h3AccXSeries, _h3AccYSeries, _h3AccZSeries,
                                  _qmaAccXSeries, _qmaAccYSeries, _qmaAccZSeries,
                                  _magXSeries, _magYSeries, _magZSeries })
            s.Points.Clear();

        ResetAxes(LsmAccPlot);  ResetAxes(LsmGyroPlot);
        ResetAxes(H3AccPlot);   ResetAxes(QmaAccPlot);
        ResetAxes(MagPlot);
        LsmAccPlot.InvalidatePlot(true);
        LsmGyroPlot.InvalidatePlot(true);
        H3AccPlot.InvalidatePlot(true);
        QmaAccPlot.InvalidatePlot(true);
        MagPlot.InvalidatePlot(true);
    }

    private void UpdateCharts()
    {
        // 数据率计算（三路合计吞吐量）
        var elapsed = (DateTime.Now - _dataRateStart).TotalSeconds;
        if (elapsed >= 1.0)
        {
            var count = System.Threading.Interlocked.Exchange(ref _dataRateCounter, 0);
            DataRate = count / elapsed;
            _dataRateStart = DateTime.Now;
        }

        if (!_chartDirty) return;

        // 数据停滞检测仅在采集运行时有效
        if (AcqRunning && (DateTime.Now - _lastFrameTime).TotalSeconds > 2)
        {
            if (!_dataStalled)
            {
                _dataStalled = true;
                LogLines.Add("数据流停滞，图表已冻结");
            }
            return;
        }
        _chartDirty = false;

        LsmSample[] lsm;
        H3Sample[] h3;
        QmaSample[] qma;
        MagSample[] mag;
        lock (_bufferLock)
        {
            if (AcqRunning)
            {
                // 实时滚动：显示最近 ChartPoints 帧
                lsm = Tail(_lsmBuffer);
                h3  = Tail(_h3Buffer);
                qma = Tail(_qmaBuffer);
                mag = Tail(_magBuffer);
            }
            else
            {
                // 采集结束：显示本次采集全部数据，支持缩放回顾
                lsm = _lsmBuffer.ToArray();
                h3  = _h3Buffer.ToArray();
                qma = _qmaBuffer.ToArray();
                mag = _magBuffer.ToArray();
            }
        }

        if (lsm.Length > 0)
        {
            double lsmOdr = double.TryParse(StripNonDigit(LsmOdr), out var v1) ? v1 : 833;
            var xt = BuildTimeAxis(lsm, s => s.Datetime, lsmOdr);
            FillSeries(_lsmAccXSeries, lsm, xt, s => s.AccX);
            FillSeries(_lsmAccYSeries, lsm, xt, s => s.AccY);
            FillSeries(_lsmAccZSeries, lsm, xt, s => s.AccZ);
            ResetAxes(LsmAccPlot);
            LsmAccPlot.InvalidatePlot(true);

            FillSeries(_lsmGyroXSeries, lsm, xt, s => s.GyroX);
            FillSeries(_lsmGyroYSeries, lsm, xt, s => s.GyroY);
            FillSeries(_lsmGyroZSeries, lsm, xt, s => s.GyroZ);
            ResetAxes(LsmGyroPlot);
            LsmGyroPlot.InvalidatePlot(true);
        }

        if (h3.Length > 0)
        {
            double h3Odr = double.TryParse(StripNonDigit(H3Odr), out var v2) ? v2 : 400;
            var xt = BuildTimeAxis(h3, s => s.Datetime, h3Odr);
            FillSeries(_h3AccXSeries, h3, xt, s => s.AccX);
            FillSeries(_h3AccYSeries, h3, xt, s => s.AccY);
            FillSeries(_h3AccZSeries, h3, xt, s => s.AccZ);
            ResetAxes(H3AccPlot);
            H3AccPlot.InvalidatePlot(true);
        }

        if (qma.Length > 0)
        {
            double qmaOdr = double.TryParse(StripNonDigit(QmaOdr), out var v3) ? v3 : 100;
            var xt = BuildTimeAxis(qma, s => s.Datetime, qmaOdr);
            FillSeries(_qmaAccXSeries, qma, xt, s => s.AccX);
            FillSeries(_qmaAccYSeries, qma, xt, s => s.AccY);
            FillSeries(_qmaAccZSeries, qma, xt, s => s.AccZ);
            ResetAxes(QmaAccPlot);
            QmaAccPlot.InvalidatePlot(true);
        }

        if (mag.Length > 0)
        {
            double magOdr = double.TryParse(StripNonDigit(MagOdr), out var vm) && vm > 0 ? vm : 100;
            var xt = BuildTimeAxis(mag, s => s.Datetime, magOdr);
            FillSeries(_magXSeries, mag, xt, s => s.X);
            FillSeries(_magYSeries, mag, xt, s => s.Y);
            FillSeries(_magZSeries, mag, xt, s => s.Z);
            ResetAxes(MagPlot);
            MagPlot.InvalidatePlot(true);
        }
    }

    private static T[] Tail<T>(List<T> buffer)
    {
        var count = Math.Min(ChartPoints, buffer.Count);
        if (count == 0) return Array.Empty<T>();
        // GetRange 为 O(count)；旧实现 Skip 为 O(n)，在锁内遍历百万级缓冲会拖慢消费线程
        return buffer.GetRange(buffer.Count - count, count).ToArray();
    }

    // 根据固件 datetime（秒级）+ 配置 ODR 推算每帧的时间偏移（秒）。
    // 若采样跨越多个秒，按实测时长线性插值；不足1秒则按配置 ODR 估算步长。
    private static double[] BuildTimeAxis<T>(T[] samples, Func<T, DateTime> getTime, double odr)
    {
        if (samples.Length == 0) return Array.Empty<double>();
        var xs = new double[samples.Length];
        var span = (getTime(samples[^1]) - getTime(samples[0])).TotalSeconds;
        double step = (span > 0 && samples.Length > 1)
            ? span / (samples.Length - 1)
            : 1.0 / odr;
        for (int i = 0; i < samples.Length; i++)
            xs[i] = i * step;
        return xs;
    }

    private static void FillSeries<T>(LineSeries series, T[] samples, double[] xs, Func<T, double> value)
    {
        series.Points.Clear();
        var len = Math.Min(samples.Length, xs.Length);
        for (int i = 0; i < len; i++)
            series.Points.Add(new DataPoint(xs[i], value(samples[i])));
    }

    // 重置坐标轴到自动缩放状态（清除用户缩放/平移）
    private static void ResetAxes(PlotModel model)
    {
        foreach (var axis in model.Axes)
            axis.Reset();
    }

    // === INotifyPropertyChanged ===

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    public void Dispose()
    {
        _chartTimer.Stop();
        StopCsvRecording();
        _commander.Dispose();
        _usb.Dispose();
    }
}

/// <summary>
/// 简单的 RelayCommand 实现
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
}
