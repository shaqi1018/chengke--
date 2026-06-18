using System;
using System.Windows;
using System.Windows.Threading;

namespace VibrationMonitor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (s, ex) =>
            {
                MessageBox.Show(ex.Exception.ToString(), "UI 线程异常", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                MessageBox.Show(ex.ExceptionObject?.ToString() ?? "未知错误", "后台线程异常", MessageBoxButton.OK, MessageBoxImage.Error);
            };
        }
    }
}
