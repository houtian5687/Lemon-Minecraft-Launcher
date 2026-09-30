using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;

namespace App1.ViewModels
{
    /// <summary>
    /// 版本下载页：左侧选择版本类型、中间浏览版本列表、右侧设置并开始下载。
    /// </summary>
    public sealed class DownloadViewModel : ObservableObject
    {
        private readonly List<VersionManifestEntry> _allVersions = new();

        private VersionManifestEntry? _selectedVersion;
        private int _filterIndex;
        private string _searchText = "";
        private string _name = "";
        private string _selectedLoader = "Vanilla";
        private string? _selectedLoaderVersion;
        private JavaInfo? _selectedJava;
        private int _maxMemoryMb = AppServices.Settings.Settings.DefaultMaxMemoryMb;
        private bool _isBusy;
        private bool _isLoading;
        private bool _isIndeterminate;
        private double _progressValue;
        private string _statusText = "正在获取版本列表…";

        public ObservableCollection<VersionManifestEntry> FilteredVersions { get; } = new();
        public ObservableCollection<string> LoaderVersions { get; } = new();
        public ObservableCollection<JavaInfo> JavaList { get; } = new();
        public ObservableCollection<GameInstance> Instances => AppServices.Instances.Instances;

        public List<string> Loaders { get; } = new() { "Vanilla", "Fabric", "Quilt", "Forge", "NeoForge" };

        /// <summary>版本类型筛选：0 全部 / 1 正式版 / 2 快照 / 3 远古版。</summary>
        public int FilterIndex
        {
            get => _filterIndex;
            set
            {
                if (!Set(ref _filterIndex, value)) return;
                ApplyFilter();
                Raise(nameof(CanStartDownload));
                StartDownloadCommand.NotifyCanExecuteChanged();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!Set(ref _searchText, value)) return;
                ApplyFilter();
                Raise(nameof(CanStartDownload));
                StartDownloadCommand.NotifyCanExecuteChanged();
            }
        }

        public VersionManifestEntry? SelectedVersion
        {
            get => _selectedVersion;
            set
            {
                if (!Set(ref _selectedVersion, value)) return;
                if (value is not null && (string.IsNullOrWhiteSpace(Name) || Name == _lastAutoName))
                {
                    _lastAutoName = value.Id;
                    Name = value.Id;
                }
                Raise(nameof(CanStartDownload));
                StartDownloadCommand.NotifyCanExecuteChanged();
                _ = LoadLoaderVersionsAsync();
            }
        }

        private string? _lastAutoName;

        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        public string SelectedLoader
        {
            get => _selectedLoader;
            set
            {
                if (Set(ref _selectedLoader, value))
                {
                    Raise(nameof(HasLoaderVersion));
                    _ = LoadLoaderVersionsAsync();
                }
            }
        }

        public bool HasLoaderVersion => SelectedLoader != "Vanilla";

        public string? SelectedLoaderVersion
        {
            get => _selectedLoaderVersion;
            set => Set(ref _selectedLoaderVersion, value);
        }

        public JavaInfo? SelectedJava
        {
            get => _selectedJava;
            set => Set(ref _selectedJava, value);
        }

        public int MaxMemoryMb
        {
            get => _maxMemoryMb;
            set => Set(ref _maxMemoryMb, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (Set(ref _isBusy, value))
                {
                    Raise(nameof(CanStartDownload));
                    StartDownloadCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => Set(ref _isLoading, value);
        }

        public bool CanStartDownload => !IsBusy && SelectedVersion is not null;

        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => Set(ref _isIndeterminate, value);
        }

        public double ProgressValue
        {
            get => _progressValue;
            set => Set(ref _progressValue, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public AsyncRelayCommand RefreshCommand { get; }
        public AsyncRelayCommand StartDownloadCommand { get; }

        public DownloadViewModel()
        {
            RefreshCommand = new AsyncRelayCommand(() => LoadVersionsAsync(true));
            StartDownloadCommand = new AsyncRelayCommand(StartDownloadAsync, () => CanStartDownload);
        }

        /// <summary>当前使用的下载源说明。</summary>
        public string SourceText => AppServices.Settings.Settings.UseBmclApiMirror
            ? "BMCLAPI 镜像 · 源端有请求配额，被限流时会自动退避并重试"
            : "Mojang 官方源";

        public async Task InitializeAsync()
        {
            Raise(nameof(SourceText));
            await LoadVersionsAsync(false);
            await LoadJavaListAsync();
        }

        /// <summary>
        /// 拉取版本清单（首次可读缓存，刷新强制走网络）。
        /// </summary>
        public async Task LoadVersionsAsync(bool forceRefresh)
        {
            IsLoading = true;
            StatusText = "正在获取版本列表…";
            try
            {
                var versions = await AppServices.Catalog.GetVersionListAsync(true, true, forceRefresh);
                _allVersions.Clear();
                _allVersions.AddRange(versions);
                await UiDispatcher.RunAsync(ApplyFilter);
                StatusText = $"共 {_allVersions.Count} 个可下载版本";
            }
            catch (Exception ex)
            {
                Logger.Warn("获取版本列表失败：" + ex.Message);
                StatusText = "获取版本列表失败：" + ex.Message;
                AppServices.NotifyError("获取版本列表失败", ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void ApplyFilter()
        {
            IEnumerable<VersionManifestEntry> query = _allVersions;
            query = FilterIndex switch
            {
                1 => query.Where(v => v.Type == "release"),
                2 => query.Where(v => v.Type == "snapshot"),
                3 => query.Where(v => v.Type is "old_beta" or "old_alpha"),
                _ => query
            };

            var keyword = SearchText?.Trim();
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(v => v.Id.Contains(keyword!, StringComparison.OrdinalIgnoreCase));
            }

            var list = query.Take(500).ToList();
            FilteredVersions.Clear();
            foreach (var version in list) FilteredVersions.Add(version);

            if (SelectedVersion is null || !list.Contains(SelectedVersion))
            {
                SelectedVersion = list.FirstOrDefault();
            }

            if (list.Count == 0)
            {
                StatusText = "没有匹配的版本，请调整版本类型或搜索关键字";
            }
        }

        public async Task LoadJavaListAsync()
        {
            var javas = await AppServices.Java.ScanAsync();
            await UiDispatcher.RunAsync(() =>
            {
                JavaList.Clear();
                foreach (var java in javas) JavaList.Add(java);
                SelectedJava = JavaList.FirstOrDefault(j => j.IsRecommended) ?? JavaList.FirstOrDefault();
            });
        }

        private async Task LoadLoaderVersionsAsync()
        {
            if (SelectedVersion is null) return;
            if (SelectedLoader == "Vanilla")
            {
                await UiDispatcher.RunAsync(() => LoaderVersions.Clear());
                return;
            }

            try
            {
                var gameVersion = SelectedVersion.Id;
                var versions = SelectedLoader switch
                {
                    "Fabric" => await AppServices.Loaders.GetFabricVersionsAsync(gameVersion),
                    "Quilt" => await AppServices.Loaders.GetQuiltVersionsAsync(gameVersion),
                    "Forge" => await AppServices.Loaders.GetForgeVersionsAsync(gameVersion),
                    "NeoForge" => await AppServices.Loaders.GetNeoForgeVersionsAsync(gameVersion),
                    _ => new List<string>()
                };

                await UiDispatcher.RunAsync(() =>
                {
                    LoaderVersions.Clear();
                    foreach (var version in versions.Take(60)) LoaderVersions.Add(version);
                    SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("获取加载器版本失败：" + ex.Message);
                StatusText = "获取加载器版本失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 开始下载：入队后立即跳转到下载队列页，让用户在队列中查看整体进度。
        /// </summary>
        private async Task StartDownloadAsync()
        {
            if (SelectedVersion is null)
            {
                StatusText = "请先选择要下载的游戏版本";
                return;
            }

            var gameVersion = SelectedVersion.Id;
            var loader = Enum.TryParse<LoaderType>(SelectedLoader, out var parsed) ? parsed : LoaderType.Vanilla;
            var loaderVersion = loader == LoaderType.Vanilla ? null : SelectedLoaderVersion;
            var name = string.IsNullOrWhiteSpace(Name) ? gameVersion : Name.Trim();
            var memory = MaxMemoryMb;
            var javaPath = SelectedJava?.Path;

            IsBusy = true;
            IsIndeterminate = true;
            ProgressValue = 0;
            StatusText = "已加入下载队列…";
            StartDownloadCommand.NotifyCanExecuteChanged();

            // 立即跳转到下载队列页面
            App.MainWindowInstance?.NavigateTo("downloads");

            try
            {
                var progress = new Progress<InstallProgress>(p => UiDispatcher.Run(() =>
                {
                    IsIndeterminate = p.Total == 0;
                    ProgressValue = p.Percent;
                    StatusText = p.Text;
                }));

                var instance = await AppServices.Instances.CreateAsync(name, gameVersion, loader, loaderVersion,
                    memory, javaPath, progress);

                StatusText = $"「{instance.Name}」下载完成，可直接启动游戏";
                AppServices.NotifyInfo($"游戏版本 {instance.Name} 下载完成");
            }
            catch (Exception ex)
            {
                Logger.Error("下载游戏版本失败", ex);
                StatusText = "下载失败：" + ex.Message;
                AppServices.NotifyError("下载失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
                IsIndeterminate = false;
                StartDownloadCommand.NotifyCanExecuteChanged();
            }
        }
    }
}
