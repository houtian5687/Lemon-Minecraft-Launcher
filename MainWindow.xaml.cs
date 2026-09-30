using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using App1.Core;
using App1.Services;
using App1.ViewModels;
using App1.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;

namespace App1
{
    /// <summary>
    /// 主窗口：导航框架 + 全局通知宿主。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly Dictionary<string, Type> _pages = new()
        {
            ["home"] = typeof(HomePage),
            ["download"] = typeof(DownloadPage),
            ["instances"] = typeof(InstancesPage),
            ["store"] = typeof(StorePage),
            ["downloads"] = typeof(DownloadsPage),
            ["accounts"] = typeof(AccountsPage),
            ["usercenter"] = typeof(AccountsPage),
            ["crash"] = typeof(CrashPage),
            ["settings"] = typeof(SettingsPage)
        };

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            WindowHelper.Initialize(this);
            UiDispatcher.Queue ??= DispatcherQueue;

            // 显式设置窗口图标：确保任务栏与窗口使用应用 Logo，
            // 不依赖系统图标缓存（缓存常导致更换图标后仍显示旧图）。
            try
            {
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
                if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            }
            catch (Exception ex)
            {
                Logger.Warn("设置窗口图标失败：" + ex.Message);
            }

            VersionText.Text = "v" + AppServices.Updater.CurrentVersion;

            AppServices.ErrorRaised += OnErrorRaised;
            AppServices.InfoRaised += OnInfoRaised;
            Closed += OnClosed;

            ApplyTheme(AppServices.Settings.Settings.Theme);
            RefreshUserCenter();

            // 先导航再同步选中项，避免重复创建首页实例（重复订阅游戏输出事件）
            ContentFrame.Navigate(typeof(HomePage), null, new EntranceNavigationTransitionInfo());
            NavView.SelectedItem = NavView.MenuItems[0];

            WindowHelper.ApplyWindowSize(
                AppServices.Settings.Settings.DefaultWindowWidth,
                AppServices.Settings.Settings.DefaultWindowHeight);
        }

        private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer is not NavigationViewItem item) return;
            var tag = item.Tag?.ToString();
            if (tag is null || !_pages.TryGetValue(tag, out var pageType)) return;
            NavigateToPage(pageType, tag);
        }

        /// <summary>
        /// 兼容入口：界面若绑定 SelectionChanged 而非 ItemInvoked，同样可以正常导航。
        /// 与 ItemInvoked 同时绑定会导致重复导航，因此二者只应启用其一。
        /// </summary>
        private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item) return;
            var tag = item.Tag?.ToString();
            if (tag is null || !_pages.TryGetValue(tag, out var pageType)) return;
            NavigateToPage(pageType, tag);
        }

        private void NavigateToPage(Type pageType, string tag)
        {
            RefreshUserCenter();
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
        }

        /// <summary>
        /// 打开资源详情独立页面。
        /// </summary>
        public void NavigateToResourceDetail(Models.ResourceSearchResult resource)
            => ContentFrame.Navigate(typeof(ResourceDetailPage), resource, new DrillInNavigationTransitionInfo());

        /// <summary>
        /// 打开独立搜索页。参数可以是关键词字符串，也可以是携带排序条件的 SearchRequest。
        /// </summary>
        public void NavigateToSearch(object? parameter = null)
            => ContentFrame.Navigate(typeof(SearchPage), parameter, new DrillInNavigationTransitionInfo());

        /// <summary>
        /// 打开集合主题页：点击资源卡片上的分类标签或加载器标签时跳转。
        /// </summary>
        public void NavigateToCategory(string key, string name, string kindText = "类别")
            => ContentFrame.Navigate(typeof(CategoryPage),
                new CategoryRequest { Key = key, Name = name, KindText = kindText },
                new DrillInNavigationTransitionInfo());

        /// <summary>
        /// 刷新导航底部用户中心的账户展示。
        /// </summary>
        public void RefreshUserCenter()
        {
            var account = AppServices.Auth.Active ?? AppServices.Auth.Accounts.FirstOrDefault();
            if (account is null)
            {
                UserCenterName.Text = "未登录";
                UserCenterType.Text = "点击登录或添加账户";
                UserCenterAvatar.DisplayName = "Guest";
                return;
            }

            UserCenterName.Text = account.PlayerName;
            UserCenterType.Text = $"{account.TypeText} · {account.StatusText}";
            UserCenterAvatar.DisplayName = account.PlayerName;
        }

        public void NavigateTo(string tag)
        {
            if (!_pages.TryGetValue(tag, out var pageType)) return;

            foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
            {
                if (item.Tag?.ToString() == tag)
                {
                    NavView.SelectedItem = item;
                    NavigateToPage(pageType, tag);
                    return;
                }
            }
            foreach (var item in NavView.FooterMenuItems.OfType<NavigationViewItem>())
            {
                if (item.Tag?.ToString() == tag)
                {
                    NavView.SelectedItem = item;
                    NavigateToPage(pageType, tag);
                    return;
                }
            }
        }

        public void ApplyTheme(string theme)
        {
            RootGrid.RequestedTheme = theme switch
            {
                "Dark" => ElementTheme.Dark,
                "Light" => ElementTheme.Light,
                _ => ElementTheme.Default
            };

            // 标题栏按钮颜色跟随主题
            var titleBar = WindowHelper.AppWindow?.TitleBar;
            if (titleBar is not null)
            {
                titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            }
        }

        private void OnErrorRaised(string message, string? detail)
        {
            NotificationBar.Severity = InfoBarSeverity.Error;
            NotificationBar.Title = message;
            NotificationBar.Message = detail ?? "";
            NotificationBar.IsOpen = true;
            _ = AutoCloseAsync();
        }

        private void OnInfoRaised(string message)
        {
            NotificationBar.Severity = InfoBarSeverity.Success;
            NotificationBar.Title = message;
            NotificationBar.Message = "";
            NotificationBar.IsOpen = true;
            _ = AutoCloseAsync();
        }

        private async Task AutoCloseAsync()
        {
            await Task.Delay(6000);
            if (!NotificationBar.IsClosable) return;
            NotificationBar.IsOpen = false;
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            AppServices.ErrorRaised -= OnErrorRaised;
            AppServices.InfoRaised -= OnInfoRaised;
            AppServices.Shutdown();
        }
    }
}
