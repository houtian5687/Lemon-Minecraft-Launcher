using System;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;
using App1.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace App1.Views
{
    public sealed partial class AccountsPage : Page
    {
        public AccountsViewModel ViewModel { get; } = new();

        public AccountsPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.ShowMicrosoftLogin = ShowMicrosoftLoginAsync;
            ViewModel.ShowOfflineDialog = ShowOfflineDialogAsync;
            Loaded += async (_, _) =>
            {
                // 兜底同步选中项，避免绑定初始化时序导致导航无高亮
                NavList.SelectedIndex = ViewModel.NavIndex;
                ApplyResponsiveLayout(ActualWidth);
                await ViewModel.InitializeAsync();
            };
            SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
        }

        /// <summary>
        /// 导航切换后，为新显示的面板播放「淡入 + 上移」过渡。
        /// 使用 Storyboard 驱动渲染层的 Opacity 与 RenderTransform，
        /// 不触碰由布局系统管理的属性，避免与 ThemeShadow 的 Translation 相互干扰。
        /// </summary>
        private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var target = NavList.SelectedIndex switch
            {
                1 => PanelAdd,
                2 => PanelDetail,
                _ => PanelAccounts
            };

            // 等可见性绑定完成后再播放，避免动画落在尚未显示的控件上
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    PlayPanelEnter(target);
                }
                catch (Exception ex)
                {
                    // 动画失败不允许影响页面切换
                    Logger.Warn("导航切换动画播放失败：" + ex.Message);
                }
            });
        }

        private static void PlayPanelEnter(UIElement panel)
        {
            if (panel.Visibility != Visibility.Visible) return;

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeIn, panel);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");

            var slideIn = new DoubleAnimation
            {
                From = 18,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slideIn, panel);
            Storyboard.SetTargetProperty(slideIn,
                "(UIElement.RenderTransform).(TranslateTransform.Y)");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fadeIn);
            storyboard.Children.Add(slideIn);
            storyboard.Begin();
        }

        /// <summary>
        /// 响应式布局：窗口变窄时导航栏收窄为纯图标模式，避免挤压右侧内容区。
        /// </summary>
        private void ApplyResponsiveLayout(double width)
        {
            var compact = width < 760;

            NavColumn.Width = new GridLength(compact ? 64 : 264);
            NavHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

            var labels = compact ? Visibility.Collapsed : Visibility.Visible;
            var align = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;

            NavText0.Visibility = labels;
            NavText1.Visibility = labels;
            NavText2.Visibility = labels;

            foreach (var label in new[] { NavText0, NavText1, NavText2 })
            {
                if (label.Parent is StackPanel host) host.HorizontalAlignment = align;
            }
        }

        /// <summary>
        /// 微软设备代码登录对话框。
        /// </summary>
        private async Task<Account?> ShowMicrosoftLoginAsync()
        {
            var codeBlock = new TextBlock
            {
                FontSize = 30,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                CharacterSpacing = 40
            };
            var statusText = new TextBlock
            {
                Text = "正在创建登录会话…",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            var ring = new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center };
            var openButton = new Button
            {
                Content = "打开浏览器并复制代码",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"]
            };
            var cancelButton = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Stretch };

            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = "请在浏览器中访问 microsoft.com/link，输入以下代码完成登录：",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(codeBlock);
            panel.Children.Add(openButton);
            panel.Children.Add(ring);
            panel.Children.Add(statusText);

            var dialog = new ContentDialog
            {
                Title = "微软账户登录",
                Content = panel,
                XamlRoot = XamlRoot,
                CloseButtonText = null,
                PrimaryButtonText = null,
                SecondaryButtonText = null
            };
            // 使用自定义按钮控制关闭
            var result = new TaskCompletionSource<Account?>();
            var cts = new CancellationTokenSource();
            var completed = false;

            openButton.Click += (_, _) =>
            {
                try
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(codeBlock.Text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.microsoft.com/link")
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    statusText.Text = "打开浏览器失败：" + ex.Message;
                }
            };
            cancelButton.Click += (_, _) =>
            {
                cts.Cancel();
                dialog.Hide();
                result.TrySetResult(null);
            };
            panel.Children.Add(cancelButton);
            dialog.Closing += (_, _) =>
            {
                if (completed) return;
                cts.Cancel();
                result.TrySetResult(null);
            };

            _ = dialog.ShowAsync();

            try
            {
                var session = await AppServices.Auth.BeginMicrosoftLoginAsync(cts.Token);
                codeBlock.Text = session.UserCode;
                statusText.Text = $"代码有效期 {session.ExpiresIn / 60} 分钟，完成授权后将自动登录…";

                var account = await AppServices.Auth.CompleteMicrosoftLoginAsync(session,
                    new Progress<string>(s => UiDispatcher.Run(() => statusText.Text = s)), cts.Token);

                completed = true;
                dialog.Hide();
                result.TrySetResult(account);
            }
            catch (OperationCanceledException)
            {
                result.TrySetResult(null);
                try { dialog.Hide(); } catch { }
            }
            catch (Exception ex)
            {
                statusText.Text = "登录失败：" + ex.Message;
                ring.IsActive = false;
                await Task.Delay(2500, CancellationToken.None);
                try { dialog.Hide(); } catch { }
                result.TrySetResult(null);
            }

            return await result.Task;
        }

        private async Task<string?> ShowOfflineDialogAsync()
        {
            var input = new TextBox { Header = "玩家名称", PlaceholderText = "例如：Steve", MaxLength = 16 };
            var dialog = new ContentDialog
            {
                Title = "添加离线账户",
                Content = input,
                XamlRoot = XamlRoot,
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? input.Text.Trim() : null;
        }
    }
}
