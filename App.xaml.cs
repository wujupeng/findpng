using System;
using System.Windows;

namespace ImageSearch
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            MessageBox.Show($"发生未处理的异常: {e.ExceptionObject}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}