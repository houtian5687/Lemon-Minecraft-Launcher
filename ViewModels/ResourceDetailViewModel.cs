using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;

namespace App1.ViewModels
{
    /// <summary>
    /// 资源详情页：展示单个模组 / 光影 / 材质包的可安装版本并安装到指定游戏版本。
    /// </summary>
    public sealed class ResourceDetailViewModel : ObservableObject
    {
        private ResourceSearchResult? _resource;
        private ResourceVersion? _selectedVersion;
        private GameInstance? _targetInstance;
        private bool _isBusy;
        private bool _isLoading;
        private string _statusText = "";
        private string _descriptionText = "";

        public ObservableCollection<ResourceVersion> Versions { get; } = new();
        public ObservableCollection<GameInstance> Instances => AppServices.Instances.Instances;

        /// <summary>项目截图（由 Modrinth 项目详情提供）。</summary>
        public ObservableCollection<string> Gallery { get; } = new();

        public bool HasGallery => Gallery.Count > 0;

        /// <summary>项目正文描述：搜索结果通常为空，需拉取项目详情补全。</summary>
        public string DescriptionText
        {
            get => _descriptionText;
            set => Set(ref _descriptionText, value);
        }

        public ResourceSearchResult? Resource
        {
            get => _resource;
            private set
            {
                if (Set(ref _resource, value))
                    Raise(nameof(HasResource), nameof(TitleText), nameof(OpenProjectCommand));
            }
        }

        public bool HasResource => _resource is not null;

        public string TitleText => _resource?.Name ?? "资源详情";

        public ResourceVersion? SelectedVersion
        {
            get => _selectedVersion;
            set
            {
                if (Set(ref _selectedVersion, value))
                {
                    Raise(nameof(SelectedVersionDetail), nameof(SelectedChangelog));
                    InstallCommand.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>选中版本的附加信息（发布时间 / 文件大小 / 加载器）。</summary>
        public string SelectedVersionDetail
        {
            get
            {
                if (SelectedVersion is null) return "请选择一个版本";
                var parts = new System.Collections.Generic.List<string>();
                if (SelectedVersion.PublishedAt is { } time) parts.Add(time.ToString("yyyy-MM-dd"));
                if (SelectedVersion.FileSize > 0) parts.Add(SelectedVersion.FileSize.FormatSize());
                if (SelectedVersion.GameVersions.Count > 0)
                    parts.Add(string.Join(" / ", SelectedVersion.GameVersions.Take(4)));
                if (SelectedVersion.Loaders.Count > 0) parts.Add(string.Join(" / ", SelectedVersion.Loaders));
                return string.Join(" · ", parts);
            }
        }

        /// <summary>选中版本的更新日志。</summary>
        public string SelectedChangelog
        {
            get
            {
                if (SelectedVersion is null) return "请先在列表中选择一个版本";
                var text = SelectedVersion.Changelog?.Trim();
                return string.IsNullOrEmpty(text) ? "该版本未提供更新日志" : text;
            }
        }

        public GameInstance? TargetInstance
        {
            get => _targetInstance;
            set
            {
                if (Set(ref _targetInstance, value)) InstallCommand.NotifyCanExecuteChanged();
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => Set(ref _isBusy, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => Set(ref _isLoading, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public AsyncRelayCommand InstallCommand { get; }
        public RelayCommand OpenProjectCommand { get; }

        public ResourceDetailViewModel()
        {
            InstallCommand = new AsyncRelayCommand(InstallAsync,
                () => Resource is not null && SelectedVersion is not null && TargetInstance is not null);
            OpenProjectCommand = new RelayCommand(OpenProject, () => Resource?.ProjectUrl is not null);
        }

        public async Task LoadAsync(ResourceSearchResult resource)
        {
            Resource = resource;
            var settings = AppServices.Settings.Settings;
            TargetInstance = AppServices.Instances.Get(settings.LastInstanceId) ?? Instances.FirstOrDefault();

            DescriptionText = resource.Description ?? resource.Summary ?? "";

            IsLoading = true;
            StatusText = "正在获取可用版本…";
            try
            {
                // 搜索结果不含项目正文与截图，Modrinth 需再拉一次项目详情补全
                if (resource.Platform == ResourcePlatform.Modrinth)
                {
                    var full = await AppServices.Modrinth.GetProjectAsync(resource.Id);
                    if (full is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(full.Description))
                            await UiDispatcher.RunAsync(() => DescriptionText = full.Description!);

                        await UiDispatcher.RunAsync(() =>
                        {
                            Gallery.Clear();
                            foreach (var image in full.Gallery ?? new System.Collections.Generic.List<string>())
                                Gallery.Add(image);
                            Raise(nameof(HasGallery));
                        });
                    }
                }

                var versions = resource.Platform == ResourcePlatform.Modrinth
                    ? await AppServices.Modrinth.GetVersionsAsync(resource.Id)
                    : await AppServices.CurseForge.GetFilesAsync(resource.Id);

                await UiDispatcher.RunAsync(() =>
                {
                    Versions.Clear();
                    foreach (var version in versions) Versions.Add(version);
                    SelectedVersion = Versions.FirstOrDefault(v => v.IsPrimary) ?? Versions.FirstOrDefault();
                });

                StatusText = versions.Count == 0
                    ? "该资源暂无可安装版本"
                    : $"共 {versions.Count} 个可用版本";
            }
            catch (Exception ex)
            {
                Logger.Warn("获取资源版本失败：" + ex.Message);
                StatusText = "获取版本失败：" + ex.Message;
                AppServices.NotifyError("获取版本失败", ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task InstallAsync()
        {
            if (Resource is null || SelectedVersion is null || TargetInstance is null) return;

            IsBusy = true;
            try
            {
                await AppServices.Resources.InstallAsync(TargetInstance, Resource, SelectedVersion,
                    new Progress<InstallProgress>(p => UiDispatcher.Run(() => StatusText = p.Text)));
                StatusText = $"已安装到「{TargetInstance.Name}」";
                AppServices.NotifyInfo($"已安装 {Resource.Name} 到 {TargetInstance.Name}");
            }
            catch (Exception ex)
            {
                Logger.Error("安装资源失败", ex);
                StatusText = "安装失败：" + ex.Message;
                AppServices.NotifyError("安装失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OpenProject()
        {
            var url = Resource?.ProjectUrl;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppServices.NotifyError("打开链接失败", ex.Message);
            }
        }
    }
}
