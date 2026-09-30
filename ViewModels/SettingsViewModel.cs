using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;

namespace App1.ViewModels
{
    public sealed class ThemeOption(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public override string ToString() => Name;
    }

    /// <summary>
    /// 设置页：外观、目录、Java、启动参数、下载、账户与更新。
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private JavaInfo? _selectedJava;
        private bool _isBusy;
        private string _javaStatus = "";
        private UpdateCheckResult? _updateResult;
        private double _updateProgress;
        private string _updateStatus = "";
        private int _themeIndex;

        public AppSettings Settings => AppServices.Settings.Settings;

        public ObservableCollection<JavaInfo> JavaList { get; } = new();
        public ObservableCollection<ThemeOption> Themes { get; } = new()
        {
            new ThemeOption("Default", "跟随系统"),
            new ThemeOption("Dark", "深色"),
            new ThemeOption("Light", "浅色")
        };

        public int ThemeIndex
        {
            get => _themeIndex;
            set
            {
                if (!Set(ref _themeIndex, value)) return;
                Settings.Theme = Themes[value].Key;
                AppServices.Settings.Save();
                App.MainWindowInstance?.ApplyTheme(Settings.Theme);
            }
        }

        public JavaInfo? SelectedJava
        {
            get => _selectedJava;
            set
            {
                if (!Set(ref _selectedJava, value)) return;
                Settings.JavaPath = value?.Path;
                AppServices.Settings.Save();
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => Set(ref _isBusy, value);
        }

        public string JavaStatus
        {
            get => _javaStatus;
            set => Set(ref _javaStatus, value);
        }

        public UpdateCheckResult? UpdateResult
        {
            get => _updateResult;
            set
            {
                if (Set(ref _updateResult, value)) Raise(nameof(HasUpdate), nameof(UpdateText));
            }
        }

        public bool HasUpdate => UpdateResult?.HasUpdate == true;

        public string UpdateText => UpdateResult is null
            ? "尚未检查"
            : UpdateResult.HasUpdate
                ? $"发现 {UpdateResult.LatestVersion}（{UpdateResult.FileCount} 个文件，{UpdateResult.TotalSizeText}）"
                : $"当前已是最新版本（{UpdateResult.CurrentVersion}）";

        public double UpdateProgress
        {
            get => _updateProgress;
            set => Set(ref _updateProgress, value);
        }

        public string UpdateStatus
        {
            get => _updateStatus;
            set => Set(ref _updateStatus, value);
        }

        public string CurrentVersion => AppServices.Updater.CurrentVersion;

        public string GameDirectory => AppPaths.Root;

        public string CurseForgeStatusText => AppConstants.HasCurseForgeKey
            ? "CurseForge：可用"
            : "CurseForge：暂不可用，检索将自动使用 Modrinth";

        public string StorageUsageText
        {
            get
            {
                try
                {
                    if (!System.IO.Directory.Exists(AppPaths.Root)) return "尚未创建游戏目录";
                    var instances = AppServices.Instances.Instances.Count;
                    var versions = System.IO.Directory.Exists(AppPaths.VersionsRoot)
                        ? System.IO.Directory.GetDirectories(AppPaths.VersionsRoot).Length
                        : 0;
                    return $"{instances} 个游戏版本 · {versions} 个已安装版本";
                }
                catch
                {
                    return "";
                }
            }
        }

        public AsyncRelayCommand BrowseGameDirCommand { get; }
        public AsyncRelayCommand ScanJavaCommand { get; }
        public AsyncRelayCommand InstallJavaCommand { get; }
        public AsyncRelayCommand AddJavaFileCommand { get; }
        public AsyncRelayCommand AddJavaFolderCommand { get; }
        public RelayCommand RemoveJavaCommand { get; }
        public AsyncRelayCommand CheckUpdateCommand { get; }
        public AsyncRelayCommand ApplyUpdateCommand { get; }
        public RelayCommand OpenGameDirCommand { get; }
        public RelayCommand OpenLogDirCommand { get; }

        public SettingsViewModel()
        {
            BrowseGameDirCommand = new AsyncRelayCommand(BrowseGameDirAsync);
            ScanJavaCommand = new AsyncRelayCommand(ScanJavaAsync);
            InstallJavaCommand = new AsyncRelayCommand(p =>
            {
                var major = p?.ToString() switch { "8" => 8, "17" => 17, "21" => 21, _ => 21 };
                return InstallJavaAsync(major);
            });
            AddJavaFileCommand = new AsyncRelayCommand(async () =>
            {
                var file = await WindowHelper.PickFileAsync(".exe");
                if (string.IsNullOrEmpty(file)) return;
                await AddManualJavaAsync(file!);
            });
            AddJavaFolderCommand = new AsyncRelayCommand(async () =>
            {
                var folder = await WindowHelper.PickFolderAsync();
                if (string.IsNullOrEmpty(folder)) return;
                await AddManualJavaAsync(folder!);
            });
            RemoveJavaCommand = new RelayCommand(p =>
            {
                if (p is not JavaInfo info) return;
                AppServices.Java.RemoveManualJava(info.Path);
                JavaList.Remove(info);
                if (ReferenceEquals(SelectedJava, info))
                {
                    SelectedJava = JavaList.FirstOrDefault(j => j.IsRecommended) ?? JavaList.FirstOrDefault();
                }
                JavaStatus = $"已移除：{info.Path}";
            });
            CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync);
            ApplyUpdateCommand = new AsyncRelayCommand(ApplyUpdateAsync, () => HasUpdate);
            OpenGameDirCommand = new RelayCommand(() => OpenFolder(AppPaths.Root));
            OpenLogDirCommand = new RelayCommand(() => OpenFolder(Logger.LogDirectory));

            // 设置项改动后立即落盘，取代原来的「保存设置」按钮
            Settings.PropertyChanged += OnSettingsChanged;
        }

        /// <summary>
        /// 自动保存：全部设置项都是双向绑定，改动后立即写入配置；
        /// 下载相关参数还需同步给下载器，否则要等下次启动才生效。
        /// </summary>
        private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            AppServices.Settings.Save();

            if (e.PropertyName is nameof(AppSettings.MaxConcurrentDownloads)
                or nameof(AppSettings.ChunkCount)
                or nameof(AppSettings.ChunkThresholdMb)
                or nameof(AppSettings.SpeedLimitKb))
            {
                AppServices.Downloads.Configure(
                    Settings.MaxConcurrentDownloads,
                    Settings.ChunkCount,
                    Settings.ChunkThresholdMb * 1024 * 1024,
                    Settings.SpeedLimitKb * 1024);
            }
        }

        public async Task InitializeAsync()
        {
            _themeIndex = Themes.ToList().FindIndex(t => t.Key == Settings.Theme);
            if (_themeIndex < 0) _themeIndex = 0;
            Raise(nameof(ThemeIndex));
            await ScanJavaAsync();
        }

        private void Save()
        {
            AppServices.Settings.Save();
            AppServices.Downloads.Configure(
                Settings.MaxConcurrentDownloads,
                Settings.ChunkCount,
                Settings.ChunkThresholdMb * 1024 * 1024,
                Settings.SpeedLimitKb * 1024);
            AppServices.NotifyInfo("设置已保存");
        }

        private async Task BrowseGameDirAsync()
        {
            var folder = await WindowHelper.PickFolderAsync(AppPaths.Root);
            if (string.IsNullOrEmpty(folder)) return;
            Settings.GameDirectory = folder!;
            AppPaths.Root = folder!;
            AppPaths.EnsureAll();
            AppServices.Instances.Load();
            AppServices.Auth.Load();
            Save();
            Raise(nameof(GameDirectory));
            AppServices.NotifyInfo("游戏目录已更新，部分设置将在重启后完全生效");
        }

        private async Task ScanJavaAsync()
        {
            IsBusy = true;
            JavaStatus = "正在扫描系统中的 Java…";
            try
            {
                var javas = await AppServices.Java.ScanAsync(true);
                await UiDispatcher.RunAsync(() =>
                {
                    JavaList.Clear();
                    foreach (var j in javas) JavaList.Add(j);
                    SelectedJava = JavaList.FirstOrDefault(j => j.Path == Settings.JavaPath)
                                   ?? JavaList.FirstOrDefault(j => j.IsRecommended)
                                   ?? JavaList.FirstOrDefault();
                });
                JavaStatus = $"共发现 {javas.Count} 个 Java 运行时";
            }
            catch (Exception ex)
            {
                JavaStatus = "扫描失败：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 手动添加 Java（java.exe 文件或安装目录），自动扫描失效时的兜底方案。
        /// </summary>
        private async Task AddManualJavaAsync(string path)
        {
            IsBusy = true;
            JavaStatus = "正在验证所选 Java…";
            try
            {
                var info = await AppServices.Java.AddManualJavaAsync(path);
                if (info is null)
                {
                    JavaStatus = "所选路径不是有效的 Java 运行时";
                    AppServices.NotifyError("添加失败", "请选择 java.exe / javaw.exe，或包含 bin\\java.exe 的 Java 安装目录");
                    return;
                }

                await UiDispatcher.RunAsync(() =>
                {
                    var existed = JavaList.FirstOrDefault(j =>
                        string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase));
                    if (existed is not null) JavaList.Remove(existed);
                    JavaList.Add(info);
                    SelectedJava = info;
                });
                JavaStatus = $"已添加：{info.Version} · {info.Architecture}";
                AppServices.NotifyInfo("已添加 Java 运行时并设为默认");
            }
            catch (Exception ex)
            {
                JavaStatus = "添加失败：" + ex.Message;
                AppServices.NotifyError("添加失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task InstallJavaAsync(int major)
        {
            IsBusy = true;
            JavaStatus = $"正在下载并部署 Java {major}…";
            try
            {
                var info = await AppServices.Java.InstallJavaAsync(major,
                    new Progress<double>(p => UiDispatcher.Run(() =>
                        JavaStatus = $"正在部署 Java {major} … {Math.Clamp(p * 100, 0, 100):0}%")));
                if (info is null) throw new System.IO.IOException("安装完成但未能识别该运行时");
                await UiDispatcher.RunAsync(() =>
                {
                    var existed = JavaList.FirstOrDefault(j =>
                        string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase));
                    if (existed is not null) JavaList.Remove(existed);
                    JavaList.Add(info);
                    SelectedJava = info;
                });
                JavaStatus = $"Java {major} 已就绪：{info.Version}（已设为默认）";
                AppServices.Settings.Save();
                AppServices.NotifyInfo($"Java {major} 已安装并设为默认运行时");
            }
            catch (Exception ex)
            {
                JavaStatus = "安装失败：" + ex.Message;
                AppServices.NotifyError("Java 安装失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task CheckUpdateAsync()
        {
            IsBusy = true;
            UpdateStatus = "正在检查更新…";
            try
            {
                UpdateResult = await AppServices.Updater.CheckAsync();
                UpdateStatus = UpdateResult.Error ?? UpdateText;
                if (HasUpdate) AppServices.NotifyInfo("发现新版本：" + UpdateResult!.LatestVersion);
            }
            catch (Exception ex)
            {
                UpdateStatus = "检查失败：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
                ApplyUpdateCommand.NotifyCanExecuteChanged();
            }
        }

        private async Task ApplyUpdateAsync()
        {
            if (UpdateResult is null || !UpdateResult.HasUpdate) return;
            IsBusy = true;
            UpdateStatus = "正在下载更新…";
            try
            {
                var ok = await AppServices.Updater.ApplyAsync(UpdateResult,
                    new Progress<double>(p => UiDispatcher.Run(() => UpdateProgress = p)));
                UpdateStatus = ok ? "更新完成，重启后生效" : "更新失败，请查看日志";
                if (ok)
                {
                    AppServices.NotifyInfo("更新完成，可点击重启应用");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus = "更新失败：" + ex.Message;
                AppServices.NotifyError("更新失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppServices.NotifyError("打开目录失败", ex.Message);
            }
        }
    }
}
