using App1.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace App1.Views
{
    /// <summary>
    /// 已下载版本管理：查看并修改已安装游戏版本的设置。
    /// </summary>
    public sealed partial class InstancesPage : Page
    {
        public InstancesViewModel ViewModel { get; } = new();

        public InstancesPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.NavigateToDownload = () => App.MainWindowInstance?.NavigateTo("download");
            Loaded += async (_, _) => await ViewModel.InitializeAsync();
        }
    }
}
