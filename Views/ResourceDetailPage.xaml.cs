using System;
using App1.Models;
using App1.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace App1.Views
{
    /// <summary>
    /// 资源详情独立页面（由资源中心点击条目进入）。
    /// </summary>
    public sealed partial class ResourceDetailPage : Page
    {
        public ResourceDetailViewModel ViewModel { get; } = new();

        public ResourceDetailPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is not ResourceSearchResult resource) return;
            try
            {
                await ViewModel.LoadAsync(resource);
            }
            catch (Exception ex)
            {
                Core.Logger.Error("加载资源详情失败", ex);
            }
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
                return;
            }
            App.MainWindowInstance?.NavigateTo("store");
        }

    }
}
