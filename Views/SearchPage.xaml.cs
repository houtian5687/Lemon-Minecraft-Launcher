using System;
using App1.Models;
using App1.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace App1.Views
{
    /// <summary>
    /// 独立搜索页：提供比模组页更完整的筛选条件与翻页检索。
    /// 复用 StoreViewModel 的检索逻辑，但使用独立实例，避免与模组页状态相互影响。
    /// </summary>
    public sealed partial class SearchPage : Page
    {
        public StoreViewModel ViewModel { get; } = new();

        public SearchPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
        }

        /// <summary>
        /// 导航参数可以是关键词字符串，也可以是 SearchRequest（携带排序条件）。
        /// </summary>
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.InitializeForSearchAsync();

            switch (e.Parameter)
            {
                case SearchRequest request:
                    if (request.SortIndex > 0) ViewModel.SortIndex = request.SortIndex;
                    await ViewModel.SearchWithAsync(request.Query);
                    break;

                case string query:
                    await ViewModel.SearchWithAsync(query);
                    break;

                default:
                    // 无参进入时先给出一批默认结果，避免空白页
                    await ViewModel.SearchWithAsync(null);
                    break;
            }
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
            => App.MainWindowInstance?.NavigateTo("store");

        private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                await ViewModel.SearchWithAsync(ViewModel.Query);
        }

        private async void OnSearchClick(object sender, RoutedEventArgs e)
            => await ViewModel.SearchWithAsync(ViewModel.Query);

        private void OnResultItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not ResourceSearchResult resource) return;
            App.MainWindowInstance?.NavigateToResourceDetail(resource);
        }
    }
}
