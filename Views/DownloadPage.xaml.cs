using App1.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace App1.Views
{
    /// <summary>
    /// 版本下载页（左侧版本类型 / 中间版本列表 / 右侧安装设置）。
    /// </summary>
    public sealed partial class DownloadPage : Page
    {
        public DownloadViewModel ViewModel { get; } = new();

        public DownloadPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
            Loaded += async (_, _) => await ViewModel.InitializeAsync();
        }
    }
}
