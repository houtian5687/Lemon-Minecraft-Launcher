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
    /// 分类主题页：展示某个分类下的资源，由模组页卡片上的分类标签跳转进入。
    /// 使用独立的 StoreViewModel 实例，与模组页、搜索页的状态互不影响。
    /// </summary>
    public sealed partial class CategoryPage : Page
    {
        public StoreViewModel ViewModel { get; } = new();

        /// <summary>当前固定的分类键；为空表示尚未完成初始化。</summary>
        private string? _categoryKey;

        /// <summary>最近一次点击卡片内部控件（分类/加载器标签）的时间，用于抑制紧随其后的卡片点击。</summary>
        private DateTime _lastInnerClickAt = DateTime.MinValue;

        private bool IsJustInnerClicked => (DateTime.UtcNow - _lastInnerClickAt).TotalMilliseconds < 500;

        public CategoryPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is not CategoryRequest request) return;

            _categoryKey = request.Key;
            CategoryTitle.Text = string.IsNullOrEmpty(request.Name) ? "资源" : request.Name;
            CategoryKindText.Text = $"模组 · {request.KindText}";

            await ViewModel.SearchByCategoryAsync(request.Key, SortBox.SelectedIndex);
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
            => App.MainWindowInstance?.NavigateTo("store");

        /// <summary>切换排序方式后，重新检索当前分类。</summary>
        private async void OnSortChanged(object sender, SelectionChangedEventArgs e)
        {
            // 首次加载时 XAML 会先触发一次，此时还没拿到分类键，跳过即可
            if (_categoryKey is null) return;
            await ViewModel.SearchByCategoryAsync(_categoryKey, SortBox.SelectedIndex);
        }

        /// <summary>与模组页一致：按可用宽度反算卡片宽度，保证始终 4 列。</summary>
        private void OnTileGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not GridView grid) return;

            const int columns = 4;
            const double spacing = 12;
            var itemWidth = (e.NewSize.Width - spacing * (columns - 1)) / columns;
            if (itemWidth <= 0) return;

            if (grid.ItemsPanelRoot is ItemsWrapGrid wrap) wrap.ItemWidth = itemWidth;
        }

        private void OnTileTapped(object sender, TappedRoutedEventArgs e)
        {
            // 刚点过卡片上的类别／加载器标签时，忽略这次卡片点击
            if (IsJustInnerClicked) return;
            if (sender is not FrameworkElement { DataContext: ResourceSearchResult resource }) return;
            App.MainWindowInstance?.NavigateToResourceDetail(resource);
        }

        /// <summary>
        /// 点击卡片上的其他分类标签 → 平级切换到该分类的主题页。
        /// 标签是独立的 Button，指针事件不会外传，因此不会同时打开资源详情。
        /// </summary>
        private void OnCategoryTagClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ResourceSearchResult resource }) return;
            if (string.IsNullOrEmpty(resource.PrimaryCategoryKey)) return;

            _lastInnerClickAt = DateTime.UtcNow;
            App.MainWindowInstance?.NavigateToCategory(resource.PrimaryCategoryKey, resource.PrimaryCategoryText, "类别");
        }

        /// <summary>点击加载器标签 → 平级切换到该加载器的资源列表页。</summary>
        private void OnLoaderTagClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ResourceSearchResult resource }) return;
            if (string.IsNullOrEmpty(resource.PrimaryLoaderKey)) return;

            _lastInnerClickAt = DateTime.UtcNow;
            App.MainWindowInstance?.NavigateToCategory(
                resource.PrimaryLoaderKey, resource.PrimaryLoaderText, "加载器");
        }
    }
}
