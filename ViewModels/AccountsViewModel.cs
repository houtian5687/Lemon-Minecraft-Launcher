using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;

namespace App1.ViewModels
{
    /// <summary>
    /// 账户页：微软正版登录（设备代码流）与离线账户。
    /// </summary>
    public sealed class AccountsViewModel : ObservableObject
    {
        private Account? _selected;
        private bool _isBusy;
        private string _statusText = "";
        private int _navIndex;

        public ObservableCollection<Account> Accounts => AppServices.Auth.Accounts;

        /// <summary>由页面注入的登录对话框回调。</summary>
        public Func<Task<Account?>>? ShowMicrosoftLogin { get; set; }

        /// <summary>由页面注入的离线账户对话框回调。</summary>
        public Func<Task<string?>>? ShowOfflineDialog { get; set; }

        public Account? Selected
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value))
                {
                    Raise(nameof(HasSelection), nameof(HasNoSelection),
                        nameof(SelectedUuidText), nameof(SelectedExpiresText), nameof(SelectedOwnsGameText));
                    RemoveCommand.NotifyCanExecuteChanged();
                    SetActiveCommand.NotifyCanExecuteChanged();
                    RefreshCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool HasSelection => Selected is not null;

        public bool HasNoSelection => Selected is null;

        /// <summary>左侧导航当前选中的分区（0 账户 / 1 添加账户 / 2 账户详情）。</summary>
        public int NavIndex
        {
            get => _navIndex;
            set
            {
                if (!Set(ref _navIndex, value)) return;
                Raise(nameof(IsAccountsTab), nameof(IsAddTab), nameof(IsDetailTab));
            }
        }

        public bool IsAccountsTab => NavIndex == 0;
        public bool IsAddTab => NavIndex == 1;
        public bool IsDetailTab => NavIndex == 2;

        public bool HasAccounts => Accounts.Count > 0;

        public bool HasNoAccounts => !HasAccounts;

        /// <summary>当前正在使用的账户（用于左侧导航顶部的摘要）。</summary>
        public Account? ActiveAccount => AppServices.Auth.Active;

        public bool HasActiveAccount => ActiveAccount is not null;

        public string ActiveAccountName => ActiveAccount?.PlayerName ?? "未登录";

        public string ActiveAccountStatus => ActiveAccount?.StatusText ?? "添加账户后即可开始游戏";

        public string SelectedUuidText =>
            string.IsNullOrWhiteSpace(Selected?.Uuid) ? "—" : Selected!.Uuid;

        public string SelectedExpiresText => Selected?.ExpiresAt is { } expires
            ? expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "—";

        public string SelectedOwnsGameText => Selected is null
            ? "—"
            : Selected.Type == AccountType.Offline
                ? "不适用（离线账户）"
                : Selected.OwnsGame ? "已拥有 Java 版" : "未检出游戏授权";

        /// <summary>当前使用账户的摘要变化后刷新左侧导航。</summary>
        private void RefreshActiveSummary()
            => Raise(nameof(ActiveAccount), nameof(HasActiveAccount),
                nameof(ActiveAccountName), nameof(ActiveAccountStatus));

        public bool IsBusy
        {
            get => _isBusy;
            set => Set(ref _isBusy, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public AsyncRelayCommand LoginMicrosoftCommand { get; }
        public AsyncRelayCommand AddOfflineCommand { get; }
        public RelayCommand RemoveCommand { get; }
        public RelayCommand SetActiveCommand { get; }
        public AsyncRelayCommand RefreshCommand { get; }

        public AccountsViewModel()
        {
            LoginMicrosoftCommand = new AsyncRelayCommand(LoginMicrosoftAsync);
            AddOfflineCommand = new AsyncRelayCommand(AddOfflineAsync);
            RemoveCommand = new RelayCommand(Remove, () => HasSelection);
            SetActiveCommand = new RelayCommand(() =>
            {
                if (Selected is not null)
                {
                    AppServices.Auth.SetActive(Selected);
                    StatusText = $"已切换到 {Selected.PlayerName}";
                    App.MainWindowInstance?.RefreshUserCenter();
                    RefreshActiveSummary();
                }
            }, () => HasSelection);
            RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => HasSelection);

            // 账户增删后同步「空列表」提示与左侧摘要
            Accounts.CollectionChanged += (_, _) =>
            {
                Raise(nameof(HasAccounts), nameof(HasNoAccounts));
                RefreshActiveSummary();
            };
        }

        public async Task InitializeAsync()
        {
            // 自动刷新已过期的微软账户
            foreach (var account in Accounts.Where(a => a.Type == AccountType.Microsoft && a.IsExpired).ToList())
            {
                try
                {
                    await AppServices.Auth.RefreshAsync(account);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"自动刷新账户失败：{ex.Message}");
                }
            }
            if (Selected is null)
            {
                var active = AppServices.Auth.Active ?? Accounts.FirstOrDefault();
                if (active is not null) await UiDispatcher.RunAsync(() => Selected = active);
            }
        }

        private async Task LoginMicrosoftAsync()
        {
            if (ShowMicrosoftLogin is null) return;
            IsBusy = true;
            StatusText = "正在发起登录…";
            try
            {
                var account = await ShowMicrosoftLogin();
                if (account is not null)
                {
                    AppServices.Auth.SetActive(account);
                    await UiDispatcher.RunAsync(() => Selected = account);
                    StatusText = $"登录成功：{account.PlayerName}";
                    App.MainWindowInstance?.RefreshUserCenter();
                    RefreshActiveSummary();
                    AppServices.NotifyInfo("微软账户登录成功：" + account.PlayerName);
                }
                else
                {
                    StatusText = "登录已取消";
                }
            }
            catch (Exception ex)
            {
                StatusText = "登录失败：" + ex.Message;
                AppServices.NotifyError("登录失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task AddOfflineAsync()
        {
            if (ShowOfflineDialog is null) return;
            var name = await ShowOfflineDialog();
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var account = AppServices.Auth.CreateOffline(name!);
                AppServices.Auth.SetActive(account);
                await UiDispatcher.RunAsync(() => Selected = account);
                StatusText = "已添加离线账户：" + account.PlayerName;
                App.MainWindowInstance?.RefreshUserCenter();
                AppServices.NotifyInfo("离线账户已添加");
            }
            catch (Exception ex)
            {
                AppServices.NotifyError("添加失败", ex.Message);
            }
        }

        private void Remove()
        {
            if (Selected is null) return;
            var target = Selected;
            UiDispatcher.Run(() => Selected = null);
            AppServices.Auth.Remove(target);
            StatusText = "账户已移除";
            App.MainWindowInstance?.RefreshUserCenter();
        }

        private async Task RefreshAsync()
        {
            if (Selected is null) return;
            if (Selected.Type != AccountType.Microsoft)
            {
                StatusText = "离线账户无需刷新";
                return;
            }
            IsBusy = true;
            StatusText = "正在刷新令牌…";
            try
            {
                var ok = await AppServices.Auth.RefreshAsync(Selected);
                StatusText = ok ? "令牌已刷新" : "刷新失败，请重新登录";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
