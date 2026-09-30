using System;
using App1.Core;
using App1.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace App1
{
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>当前主窗口实例（供设置页切换主题等场景使用）。</summary>
        public static MainWindow? MainWindowInstance { get; private set; }

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            UiDispatcher.Queue = DispatcherQueue.GetForCurrentThread();
            AppServices.Initialize();

            _window = new MainWindow();
            MainWindowInstance = (MainWindow)_window;
            _window.Activate();

            // 可选：启动后自动检查更新（清单地址已内置，只需判断开关）
            if (AppServices.Settings.Settings.AutoCheckUpdate)
            {
                _ = CheckUpdateOnStartupAsync();
            }

            // 后台扫描 Java
            _ = AppServices.Java.ScanAsync();
        }

        private async System.Threading.Tasks.Task CheckUpdateOnStartupAsync()
        {
            try
            {
                var result = await AppServices.Updater.CheckAsync();
                if (result.HasUpdate)
                {
                    AppServices.NotifyInfo($"发现新版本 {result.LatestVersion}（{result.FileCount} 个文件，{result.TotalSizeText}），可在设置中更新");
                }
            }
            catch
            {
                // 静默失败，不影响启动
            }
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Logger.Error("未处理的界面异常", e.Exception);
            e.Handled = true;
            AppServices.NotifyError("发生未预期的错误：" + e.Exception.Message, e.Exception.StackTrace);
        }
    }
}
