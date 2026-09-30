using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;

namespace App1.ViewModels
{
    /// <summary>
    /// 启动页：选择实例与账户、安装缺失文件并启动游戏。
    /// </summary>
    public sealed class HomeViewModel : ObservableObject
    {
        private GameInstance? _selectedInstance;
        private Account? _selectedAccount;
        private bool _isBusy;
        private bool _isGameRunning;
        private bool _isStarting;
        private double _progressValue;
        private bool _isIndeterminate;
        private string _statusText = "准备就绪";
        private string _detailText = "";

        public ObservableCollection<GameInstance> Instances => AppServices.Instances.Instances;
        public ObservableCollection<Account> Accounts => AppServices.Auth.Accounts;
        public ObservableCollection<string> LogLines { get; } = new();

        public GameInstance? SelectedInstance
        {
            get => _selectedInstance;
            set
            {
                if (Set(ref _selectedInstance, value))
                {
                    if (value is not null) AppServices.Settings.Settings.LastInstanceId = value.Id;
                    Raise(nameof(HasInstance), nameof(InstanceSummary), nameof(InstanceDetailText),
                        nameof(SelectionSummary), nameof(CanSwitchVersion),
                        nameof(MemoryText), nameof(BuildInfoText), nameof(PlayTimeText),
                        nameof(LoaderText), nameof(GameVersionText), nameof(LaunchButtonText),
                        nameof(ShowHint), nameof(HintTitle), nameof(HintText),
                        nameof(MissingInstance), nameof(MissingAccount));
                    NotifyCommands();
                }
            }
        }

        public Account? SelectedAccount
        {
            get => _selectedAccount;
            set
            {
                if (Set(ref _selectedAccount, value))
                {
                    if (value is not null)
                    {
                        AppServices.Auth.SetActive(value);
                        AppServices.Settings.Settings.LastAccountId = value.Id;
                    }
                    Raise(nameof(HasAccount), nameof(AccountSummary), nameof(AccountDetailText),
                        nameof(SelectionSummary),
                        nameof(LaunchButtonText), nameof(ShowHint), nameof(HintTitle), nameof(HintText),
                        nameof(MissingInstance), nameof(MissingAccount));
                    NotifyCommands();
                }
            }
        }

        public bool HasInstance => SelectedInstance is not null;
        public bool HasAccount => SelectedAccount is not null;

        public string InstanceSummary => SelectedInstance is null
            ? "尚未选择游戏版本"
            : $"{SelectedInstance.Name} · {SelectedInstance.VersionText}";

        public string AccountSummary => SelectedAccount is null
            ? "尚未选择账户"
            : $"{SelectedAccount.PlayerName} · {SelectedAccount.TypeText}";

        public string InstanceDetailText => SelectedInstance is null
            ? "选择一个游戏版本后即可开始游戏"
            : $"内存 {SelectedInstance.MinMemoryMb}–{SelectedInstance.MaxMemoryMb} MB · " +
              $"上次游玩 {SelectedInstance.LastPlayed:yyyy-MM-dd}";

        /// <summary>
        /// 当前版本与账户的合并摘要，显示在启动按钮上方。
        /// </summary>
        public string SelectionSummary
        {
            get
            {
                if (SelectedInstance is null) return "尚未选择游戏版本";
                var text = $"{SelectedInstance.Name} · {SelectedInstance.VersionText}";
                if (SelectedAccount is null) return text + " · 未选择账户";
                return $"{text} · {SelectedAccount.PlayerName}（{SelectedAccount.TypeText}）";
            }
        }

        public string AccountDetailText => SelectedAccount is null
            ? "添加账户以启用正版联机与皮肤显示"
            : SelectedAccount.StatusText;

        public string MemoryText => SelectedInstance is null ? "—" : $"{SelectedInstance.MaxMemoryMb} MB";

        public string BuildInfoText => SelectedInstance is null ? "—" : SelectedInstance.VersionText;

        public string PlayTimeText => SelectedInstance is null ? "—" : SelectedInstance.PlayTimeText;

        public string LoaderText => SelectedInstance is null
            ? "—"
            : $"{SelectedInstance.Loader.ToText()}{(string.IsNullOrEmpty(SelectedInstance.LoaderVersion) ? "" : " " + SelectedInstance.LoaderVersion)}";

        public string GameVersionText => SelectedInstance?.GameVersion ?? "—";

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (Set(ref _isBusy, value))
                    Raise(nameof(CanLaunch), nameof(LaunchButtonText), nameof(CanSwitchVersion));
            }
        }

        /// <summary>是否缺少启动条件（用于主页显示引导）。</summary>
        public bool ShowHint => !HasInstance || !HasAccount;

        public bool MissingInstance => !HasInstance;

        public bool MissingAccount => HasInstance && !HasAccount;

        public string HintTitle => !HasInstance
            ? "还没有游戏版本"
            : !HasAccount ? "还没有游戏账户" : "";

        public string HintText => !HasInstance
            ? "前往「游戏下载」选择一个版本开始下载"
            : !HasAccount ? "前往「账户」添加微软正版或离线账户" : "";

        public bool IsGameRunning
        {
            get => _isGameRunning;
            set
            {
                if (Set(ref _isGameRunning, value))
                    Raise(nameof(CanLaunch), nameof(LaunchButtonText), nameof(CanKill),
                        nameof(CanSwitchVersion), nameof(KillButtonText));
            }
        }

        /// <summary>
        /// 进程已创建但游戏主窗口尚未出现（正在加载资源）。
        /// 此期间启动按钮保持禁用并显示「启动中」。
        /// </summary>
        public bool IsStarting
        {
            get => _isStarting;
            set
            {
                if (Set(ref _isStarting, value))
                    Raise(nameof(CanLaunch), nameof(LaunchButtonText), nameof(CanKill),
                        nameof(CanSwitchVersion), nameof(KillButtonText));
            }
        }

        public bool CanLaunch => !IsBusy && !IsStarting && !IsGameRunning && HasInstance && HasAccount;

        public bool CanKill => IsGameRunning || IsStarting;

        /// <summary>
        /// 版本切换（下拉）是否可用。
        /// 没有任何游戏版本时一并禁用，使整组按钮统一呈灰色。
        /// </summary>
        public bool CanSwitchVersion => HasInstance && !IsBusy && !IsStarting && !IsGameRunning;

        public string LaunchButtonText => !HasInstance
            ? "请先下载版本"
            : !HasAccount ? "请先添加账户"
            : IsStarting ? "启动中…"
            : IsGameRunning ? "游戏运行中"
            : IsBusy ? "处理中…"
            : "启动游戏";

        public string KillButtonText => IsStarting ? "取消启动" : "结束游戏";

        public double ProgressValue
        {
            get => _progressValue;
            set => Set(ref _progressValue, value);
        }

        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => Set(ref _isIndeterminate, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public string DetailText
        {
            get => _detailText;
            set => Set(ref _detailText, value);
        }

        public AsyncRelayCommand LaunchCommand { get; }
        public RelayCommand KillCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public AsyncRelayCommand RepairCommand { get; }
        public RelayCommand ClearLogCommand { get; }

        public HomeViewModel()
        {
            // 版本或账户在其它页面发生变化时，自动同步主页的选择状态
            AppServices.Instances.Instances.CollectionChanged += OnCollectionsChanged;
            AppServices.Auth.Accounts.CollectionChanged += OnCollectionsChanged;

            LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => HasInstance && HasAccount && !IsGameRunning);
            KillCommand = new RelayCommand(() =>
            {
                AppServices.Launcher.Kill();
                StatusText = "已请求结束游戏进程";
            }, () => CanKill);
            OpenFolderCommand = new RelayCommand(() =>
            {
                if (SelectedInstance is not null) InstanceService.OpenFolder(SelectedInstance);
            }, () => HasInstance);
            RepairCommand = new AsyncRelayCommand(RepairAsync, () => HasInstance && !IsBusy);
            ClearLogCommand = new RelayCommand(() => LogLines.Clear());

            AppServices.Launcher.OutputReceived += OnOutput;
            AppServices.Launcher.GameExited += OnGameExited;
            AppServices.Launcher.GameStarted += OnGameStarted;
        }

        public async Task InitializeAsync()
        {
            EnsureSelection();
            await Task.CompletedTask;
        }

        private void OnCollectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => UiDispatcher.Run(EnsureSelection);

        /// <summary>
        /// 保证主页始终有可用的版本 / 账户选中项（下载或登录后立即生效）。
        /// </summary>
        private void EnsureSelection()
        {
            var settings = AppServices.Settings.Settings;
            var instances = AppServices.Instances.Instances;
            var accounts = AppServices.Auth.Accounts;

            if (SelectedInstance is null || !instances.Contains(SelectedInstance))
            {
                SelectedInstance = AppServices.Instances.Get(settings.LastInstanceId)
                                   ?? instances.FirstOrDefault();
            }

            if (SelectedAccount is null || !accounts.Contains(SelectedAccount))
            {
                SelectedAccount = accounts.FirstOrDefault(a => a.Id == settings.LastAccountId)
                                  ?? AppServices.Auth.Active
                                  ?? accounts.FirstOrDefault();
            }

            Raise(nameof(HasInstance), nameof(HasAccount), nameof(LaunchButtonText),
                nameof(SelectionSummary), nameof(InstanceSummary), nameof(AccountSummary),
                nameof(CanSwitchVersion),
                nameof(ShowHint), nameof(HintTitle), nameof(HintText),
                nameof(MissingInstance), nameof(MissingAccount));
            NotifyCommands();
        }

        private void OnOutput(string line)
        {
            UiDispatcher.Run(() =>
            {
                LogLines.Add(line);
                while (LogLines.Count > 500) LogLines.RemoveAt(0);
            });
        }

        /// <summary>
        /// 游戏主窗口出现：此时才把「启动中」切换为「运行中」。
        /// </summary>
        private void OnGameStarted()
        {
            UiDispatcher.Run(() =>
            {
                IsStarting = false;
                IsGameRunning = true;
                StatusText = "游戏运行中";
                NotifyCommands();
            });
        }

        private void OnGameExited(int exitCode)
        {
            UiDispatcher.Run(() =>
            {
                IsGameRunning = false;
                IsStarting = false;
                IsBusy = false;
                IsIndeterminate = false;
                if (exitCode == 0)
                {
                    StatusText = "游戏已正常退出";
                    DetailText = "";
                }
                else
                {
                    StatusText = $"游戏异常退出（代码 {exitCode}）";
                    DetailText = "可前往【崩溃分析】查看详细原因与修复建议";
                    AppServices.NotifyError(StatusText, DetailText);
                }
                NotifyCommands();
            });
        }

        private async Task LaunchAsync()
        {
            if (SelectedInstance is null)
            {
                StatusText = "请先下载一个游戏版本";
                AppServices.NotifyError("无法启动", "请先在「游戏下载」中下载一个游戏版本");
                return;
            }
            if (SelectedAccount is null)
            {
                StatusText = "请先添加游戏账户";
                AppServices.NotifyError("无法启动", "请先在「账户」中添加微软正版或离线账户");
                return;
            }

            IsBusy = true;
            IsIndeterminate = true;
            StatusText = "正在检查游戏文件…";
            NotifyCommands();

            try
            {
                var instance = SelectedInstance;
                var versionId = instance.ResolvedVersionId ?? instance.GameVersion;

                var progress = new Progress<InstallProgress>(p => UiDispatcher.Run(() =>
                {
                    IsIndeterminate = false;
                    ProgressValue = p.Percent;
                    StatusText = p.Phase;
                    DetailText = p.Detail ?? (p.Total > 0 ? $"{p.Current}/{p.Total}" : "");
                }));

                StatusText = "校验版本完整性…";
                var spec = await AppServices.Catalog.ResolveAsync(versionId);
                var integrity = GameInstallService.CheckIntegrity(spec);
                if (!integrity.IsComplete)
                {
                    StatusText = "正在下载缺失的游戏文件…";
                    await AppServices.Installer.EnsureVersionAsync(versionId, progress);
                }

                if (!await AppServices.Auth.EnsureValidAsync(SelectedAccount))
                {
                    StatusText = "账户令牌已失效，请重新登录";
                    AppServices.NotifyError("账户验证失败", "请在【账户】页面重新登录该微软账户");
                    return;
                }

                StatusText = "正在启动游戏…";
                IsIndeterminate = true;
                var result = await AppServices.Launcher.LaunchAsync(instance, SelectedAccount);
                if (result.Success)
                {
                    // 进程已创建，但游戏窗口尚未出现，先进入「启动中」状态
                    IsStarting = true;
                    StatusText = "启动中，正在等待游戏窗口…";
                    DetailText = "日志输出：" + result.LogPath;
                    if (AppServices.Settings.Settings.CloseOnLaunch)
                    {
                        Logger.Info("已设置启动后关闭启动器");
                    }
                }
                else
                {
                    StatusText = "启动失败：" + result.Error;
                    DetailText = "";
                    AppServices.NotifyError("启动失败", result.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("启动流程异常", ex);
                StatusText = "启动失败：" + ex.Message;
                AppServices.NotifyError("启动失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
                if (!IsGameRunning) IsIndeterminate = false;
                NotifyCommands();
            }
        }

        private async Task RepairAsync()
        {
            if (SelectedInstance is null) return;
            var versionId = SelectedInstance.ResolvedVersionId ?? SelectedInstance.GameVersion;
            IsBusy = true;
            StatusText = "正在修复安装…";
            try
            {
                var progress = new Progress<InstallProgress>(p => UiDispatcher.Run(() =>
                {
                    IsIndeterminate = false;
                    ProgressValue = p.Percent;
                    StatusText = p.Phase;
                    DetailText = p.Detail ?? "";
                }));
                await AppServices.Installer.EnsureVersionAsync(versionId, progress);
                StatusText = "修复完成";
                AppServices.NotifyInfo("游戏文件校验完成");
            }
            catch (Exception ex)
            {
                StatusText = "修复失败：" + ex.Message;
                AppServices.NotifyError("修复失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
                IsIndeterminate = false;
                NotifyCommands();
            }
        }

        private void NotifyCommands()
        {
            UiDispatcher.Run(() =>
            {
                LaunchCommand.NotifyCanExecuteChanged();
                KillCommand.NotifyCanExecuteChanged();
                OpenFolderCommand.NotifyCanExecuteChanged();
                RepairCommand.NotifyCanExecuteChanged();
            });
        }
    }
}
