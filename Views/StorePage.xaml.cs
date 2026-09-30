using System;
using App1.Models;
using App1.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace App1.Views
{
    /// <summary>
    /// 模组页（商店首页）：展示精选、热门与最近更新的推荐内容。
    /// 检索操作统一交给独立搜索页完成，本页只保留一个搜索入口。
    /// </summary>
    public sealed partial class StorePage : Page
    {
        public StoreViewModel ViewModel { get; } = new();

        /// <summary>
        /// 最近一次点击卡片「内部控件」（分类标签、查看详情按钮）的时间。
        /// 这些控件位于可点击的卡片内部，用一个小时间窗抑制紧随其后的卡片点击，
        /// 确保一次操作只走一条跳转路径，不会重复导航。
        /// </summary>
        private DateTime _lastInnerClickAt = DateTime.MinValue;

        private bool IsJustInnerClicked => (DateTime.UtcNow - _lastInnerClickAt).TotalMilliseconds < 500;

        public StorePage()
        {
            InitializeComponent();
            DataContext = ViewModel;
            Loaded += async (_, _) => await ViewModel.InitializeAsync();
        }

        /// <summary>
        /// 焦点进入搜索框即跳转到独立搜索页，输入与筛选都在那边完成。
        /// </summary>
        private void OnSearchBoxGotFocus(object sender, RoutedEventArgs e)
            => GoToSearch();

        private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter) GoToSearch();
        }

        private void OnSearchClick(object sender, RoutedEventArgs e) => GoToSearch();

        /// <summary>携带当前输入内容（若有）跳转到搜索页，并清空本页残留文字。</summary>
        private void GoToSearch()
        {
            var query = StoreSearchBox.Text?.Trim();
            StoreSearchBox.Text = "";
            App.MainWindowInstance?.NavigateToSearch(string.IsNullOrWhiteSpace(query) ? null : query);
        }

        /// <summary>
        /// 卡片网格固定 4 列：按可用宽度反算单个卡片的宽度。
        /// 这样无论窗口多大（含全屏）列数都保持一致，
        /// 不会出现最后一行只剩几张、或者卡片被挤到换行的错位。
        /// </summary>
        private void OnTileGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not GridView grid) return;

            const int columns = 4;
            const double spacing = 12;
            var itemWidth = (e.NewSize.Width - spacing * (columns - 1)) / columns;
            if (itemWidth <= 0) return;

            if (grid.ItemsPanelRoot is ItemsWrapGrid wrap) wrap.ItemWidth = itemWidth;
        }

        /// <summary>点击卡片任意位置 → 打开资源详情页。</summary>
        private void OnTileTapped(object sender, TappedRoutedEventArgs e)
        {
            // 刚点过卡片内部控件（类别／加载器标签）时，忽略这次卡片点击
            if (IsJustInnerClicked) return;
            if (sender is not FrameworkElement { DataContext: ResourceSearchResult resource }) return;
            App.MainWindowInstance?.NavigateToResourceDetail(resource);
        }

        /// <summary>点击 Hero 区的封面小卡 → 打开该资源的详情页。</summary>
        private void OnHeroTileTapped(object sender, TappedRoutedEventArgs e)
        {
            if (IsJustInnerClicked) return;
            if (sender is not FrameworkElement { DataContext: ResourceSearchResult resource }) return;
            App.MainWindowInstance?.NavigateToResourceDetail(resource);
        }

        /// <summary>点击主推大卡空白处 → 同样打开该资源的详情页。</summary>
        private void OnHeroMainTapped(object sender, TappedRoutedEventArgs e)
        {
            if (IsJustInnerClicked) return;
            if (ViewModel.HeroMain is { } resource)
                App.MainWindowInstance?.NavigateToResourceDetail(resource);
        }

        /// <summary>主推大卡内的「查看详情」按钮 → 打开详情页。</summary>
        private void OnHeroMainClick(object sender, RoutedEventArgs e)
        {
            _lastInnerClickAt = DateTime.UtcNow;
            if (ViewModel.HeroMain is { } resource)
                App.MainWindowInstance?.NavigateToResourceDetail(resource);
        }

        /// <summary>热门下载「查看更多」→ 搜索页按下载量排序。</summary>
        private void OnBrowsePopularClick(object sender, RoutedEventArgs e)
            => App.MainWindowInstance?.NavigateToSearch(new SearchRequest { SortIndex = 1 });

        /// <summary>最近更新「查看更多」→ 搜索页按最近更新排序。</summary>
        private void OnBrowseLatestClick(object sender, RoutedEventArgs e)
            => App.MainWindowInstance?.NavigateToSearch(new SearchRequest { SortIndex = 3 });

        /// <summary>
        /// 点击卡片上的分类标签 → 进入该分类的主题页。
        /// 标签是独立的 Button，指针事件不会外传，因此不会同时触发卡片的「打开详情」。
        /// </summary>
        private void OnCategoryTagClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ResourceSearchResult resource }) return;
            if (string.IsNullOrEmpty(resource.PrimaryCategoryKey)) return;

            _lastInnerClickAt = DateTime.UtcNow;
            App.MainWindowInstance?.NavigateToCategory(resource.PrimaryCategoryKey, resource.PrimaryCategoryText, "类别");
        }

        /// <summary>
        /// 点击类别胶囊（Hero 区下方的那一排）→ 进入该类别的独立页面，
        /// 那里以卡片形式列出该类别下的全部模组。
        /// </summary>
        private void OnCategoryChipClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: CategoryOption option }) return;

            _lastInnerClickAt = DateTime.UtcNow;
            App.MainWindowInstance?.NavigateToCategory(option.Key, option.Name, "类别");
        }

        /// <summary>
        /// 点击加载器标签 → 进入使用该加载器的资源列表页。
        /// 加载器与分类在 Modrinth 里同属 categories 维度，因此复用同一个页面。
        /// </summary>
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
