using System;
using System.Collections.Generic;
using App1.Core;
using App1.Services;
using App1.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace App1.Views
{
    /// <summary>
    /// 设置页：仿 Windows 设置的层级式导航。
    /// 主页面列出各分类入口，进入分类后顶部面包屑显示「设置 › 分类名」，
    /// 点击面包屑的第一级即可返回主页面。
    /// 所有设置项仍绑定同一个 SettingsViewModel，功能与原单页版完全一致。
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>主设置页在面包屑中的标识，同时表示「当前未进入任何分组」。</summary>
        private const string RootKey = "";

        /// <summary>分类显示名与对应视图容器，键与入口按钮的 Tag 一一对应。</summary>
        private readonly Dictionary<string, (string Title, FrameworkElement View)> _sections = new();

        public SettingsViewModel ViewModel { get; } = new();

        public SettingsPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
            Loaded += async (_, _) => await ViewModel.InitializeAsync();

            _sections["launch"] = ("启动设置", ViewLaunch);
            _sections["game"] = ("游戏设置", ViewGame);
            _sections["java"] = ("Java 设置", ViewJava);
            _sections["ui"] = ("界面设置", ViewUi);
            _sections["download"] = ("下载设置", ViewDownload);
            _sections["advanced"] = ("高级设置", ViewAdvanced);
            _sections["about"] = ("关于", ViewAbout);

            ShowSection(RootKey);
        }

        /// <summary>点击主设置页的分类入口 → 进入对应子页。</summary>
        private void OnSectionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string key }) return;
            if (!_sections.ContainsKey(key)) return;

            ShowSection(key);
        }

        /// <summary>
        /// 点击面包屑中的某一级。
        /// 索引 0 是「设置」主页面；最后一级就是当前页，因此不做跳转。
        /// </summary>
        private void OnBreadcrumbItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
        {
            if (args.Index == 0) ShowSection(RootKey);
        }

        /// <summary>切换到指定分类视图；传入空字符串（或未知键）回到主设置页。</summary>
        private void ShowSection(string key)
        {
            var isRoot = string.IsNullOrEmpty(key) || !_sections.ContainsKey(key);

            ViewRoot.Visibility = isRoot ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (sectionKey, section) in _sections)
            {
                section.View.Visibility = !isRoot && sectionKey == key
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // 面包屑：主页面只有「设置」，子页追加分类名
            NavBreadcrumb.ItemsSource = isRoot
                ? new List<string> { "设置" }
                : new List<string> { "设置", _sections[key].Title };

            // 切换层级后回到顶部，避免沿用上一页的滚动位置
            MainScroll.ChangeView(null, 0, null, true);

            // 淡入，让层级跳转有明确的过渡感
            PlayFadeIn(isRoot ? ViewRoot : _sections[key].View);
        }

        /// <summary>给刚切换出来的视图播放一次淡入动画（180ms，缓出）。</summary>
        private static void PlayFadeIn(UIElement element)
        {
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }

        private void OnRestartClick(object sender, RoutedEventArgs e)
        {
            AppServices.Settings.Save();
            UpdateService.Restart();
        }

        /// <summary>
        /// 在系统默认浏览器中打开项目主页。
        /// </summary>
        private async void OnHomepageClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(AppConstants.Homepage));
            }
            catch (Exception ex)
            {
                Logger.Warn("打开项目主页失败：" + ex.Message);
            }
        }
    }
}
