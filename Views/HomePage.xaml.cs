using System;
using App1.Core;
using App1.Services;
using App1.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace App1.Views
{
    public sealed partial class HomePage : Page
    {
        /// <summary>启动页可用背景图（顺序轮换）。</summary>
        private static readonly string[] Backgrounds =
        {
            "ms-appx:///Assets/Backgrounds/bg-dungeon.jpg",
            "ms-appx:///Assets/Backgrounds/bg-biomes.jpg",
            "ms-appx:///Assets/Backgrounds/bg-castle.jpg"
        };

        /// <summary>本次运行是否已选过背景（首页有缓存，可能多次导航，只轮换一次）。</summary>
        private static bool _backgroundPicked;

        public HomeViewModel ViewModel { get; } = new();

        public HomePage()
        {
            InitializeComponent();
            DataContext = ViewModel;
            ApplyBackground();
        }

        /// <summary>
        /// 应用启动页背景：每次启动启动器轮换一张，同一次运行内保持不变。
        /// </summary>
        private void ApplyBackground()
        {
            var settings = AppServices.Settings.Settings;
            if (!_backgroundPicked)
            {
                _backgroundPicked = true;
                // 索引 -1 起步，首次运行取第 0 张，之后依次轮换
                settings.BackgroundIndex = (settings.BackgroundIndex + 1) % Backgrounds.Length;
                AppServices.Settings.Save();
            }

            var index = settings.BackgroundIndex;
            if (index < 0 || index >= Backgrounds.Length) index = 0;

            try
            {
                BackgroundImage.Source = new BitmapImage(new Uri(Backgrounds[index]));
            }
            catch (Exception ex)
            {
                Logger.Warn("加载启动页背景失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 每次导航到主页都重新同步版本 / 账户选择，避免页面缓存导致启动按钮一直禁用。
        /// </summary>
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.InitializeAsync();
        }

        /// <summary>
        /// 展开「启动按钮」的下拉部分时，动态重建版本列表，
        /// 保证与最新实例保持一致并标记当前正在使用的版本。
        /// </summary>
        private void OnVersionFlyoutOpening(object? sender, object e)
        {
            if (sender is not MenuFlyout flyout) return;
            flyout.Items.Clear();

            if (ViewModel.Instances.Count == 0)
            {
                flyout.Items.Add(new MenuFlyoutItem
                {
                    Text = "还没有游戏版本，请先前往「游戏下载」",
                    IsEnabled = false
                });
                return;
            }

            foreach (var instance in ViewModel.Instances)
            {
                var target = instance;
                flyout.Items.Add(new ToggleMenuFlyoutItem
                {
                    Text = $"{target.Name} · {target.VersionText}",
                    IsChecked = ReferenceEquals(ViewModel.SelectedInstance, target),
                    Command = new RelayCommand(() => ViewModel.SelectedInstance = target)
                });
            }
        }

        private void OnGoDownloadClick(object sender, RoutedEventArgs e)
            => App.MainWindowInstance?.NavigateTo("download");

        private void OnGoAccountClick(object sender, RoutedEventArgs e)
            => App.MainWindowInstance?.NavigateTo("usercenter");
    }
}
