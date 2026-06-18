using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VibrationMonitor.ViewModels;

namespace VibrationMonitor
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private System.Windows.Media.Brush? _connectedBrush;
        private System.Windows.Media.Brush? _disconnectedBrush;
        private System.Windows.Controls.ScrollViewer? _logScroller;

        public MainWindow()
        {
            // 旧固件兼容模式：设环境变量 LEGACY_FW=1 启用（3端点无麦克风, RESP=0x84）。
            var legacy = Environment.GetEnvironmentVariable("LEGACY_FW");
            if (legacy == "1" || string.Equals(legacy, "true", StringComparison.OrdinalIgnoreCase))
                Services.WinUsbDeviceManager.LegacyFirmwareMode = true;

            // 跳过 MIC 端点：设环境变量 SKIP_MIC=1（5端点固件但 mic-over-USB 未开时用）。
            var skipMic = Environment.GetEnvironmentVariable("SKIP_MIC");
            if (skipMic == "1" || string.Equals(skipMic, "true", StringComparison.OrdinalIgnoreCase))
                Services.WinUsbDeviceManager.SkipMicEndpoint = true;

            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            _connectedBrush = FindResource("ConnectedBrush") as System.Windows.Media.Brush;
            _disconnectedBrush = FindResource("DisconnectedBrush") as System.Windows.Media.Brush;

            StatusTextBlock.Text = _viewModel.StatusText;
            StatusTextBlock.Foreground = _disconnectedBrush ?? StatusTextBlock.Foreground;

            _viewModel.PropertyChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (e.PropertyName == nameof(MainViewModel.StatusText) ||
                            e.PropertyName == nameof(MainViewModel.IsConnected))
                        {
                            StatusTextBlock.Text = _viewModel.StatusText;
                            StatusTextBlock.Foreground = _viewModel.IsConnected
                                ? (_connectedBrush ?? StatusTextBlock.Foreground)
                                : (_disconnectedBrush ?? StatusTextBlock.Foreground);
                        }
                    }
                    catch { }
                });
            };

            // 日志自动滚到底部：ListBox 加载后取到内部 ScrollViewer，
            // 每次新增条目后用 Background 优先级延迟调用（规避 CollectionChanged 重入限制）
            LogListBox.Loaded += (_, _) =>
            {
                _logScroller = FindVisualChild<System.Windows.Controls.ScrollViewer>(LogListBox);
            };
            _viewModel.LogLines.CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add)
                    Dispatcher.BeginInvoke(
                        () => _logScroller?.ScrollToEnd(),
                        System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) return hit;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _viewModel.Dispose();
            base.OnClosing(e);
        }
    }
}
