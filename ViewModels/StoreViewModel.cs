using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;

namespace App1.ViewModels
{
    /// <summary>分类筛选项：Key 用于请求接口，Name 为中文显示名。</summary>
    public sealed class CategoryOption(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public override string ToString() => Name;
    }

    /// <summary>
    /// 跳转到搜索页时携带的检索条件。
    /// </summary>
    public sealed class SearchRequest
    {
        public string? Query { get; set; }

        /// <summary>排序维度：0 相关度 / 1 下载量 / 2 关注数 / 3 最近更新。</summary>
        public int SortIndex { get; set; }
    }

    /// <summary>
    /// 打开集合主题页时携带的筛选信息。分类标签与加载器标签共用这个页面。
    /// </summary>
    public sealed class CategoryRequest
    {
        /// <summary>集合键（接口使用的英文标识）：分类键或加载器键。</summary>
        public string Key { get; set; } = "";

        /// <summary>集合的中文显示名，作为页面标题。</summary>
        public string Name { get; set; } = "";

        /// <summary>集合维度说明：类别 / 加载器，用于页面上的来源标识。</summary>
        public string KindText { get; set; } = "类别";
    }

    /// <summary>
    /// 资源中心：从 Modrinth / CurseForge 检索模组、整合包、光影与材质包并安装到实例。
    /// </summary>
    public sealed class StoreViewModel : ObservableObject
    {
        private string _query = "";
        private int _platformIndex;
        private int _typeIndex;
        private string? _selectedGameVersion;
        private string _selectedLoader = "Any";
        private GameInstance? _targetInstance;
        private bool _isBusy;
        private string _statusText = "";
        private int _currentPage = 1;
        private int _totalPages = 1;
        private int _totalCount;
        private int _sortIndex;
        private int _categoryIndex;
        private bool _isDiscoverMode = true;
        private bool _isLoadingDiscover;
        /// <summary>分类主题页指定的分类键；非空时优先于下拉框索引。</summary>
        private string? _categoryKeyOverride;

        public ObservableCollection<ResourceSearchResult> Results { get; } = new();

        /// <summary>发现页：下载量最高的资源。</summary>
        public ObservableCollection<ResourceSearchResult> Popular { get; } = new();

        private ResourceSearchResult? _heroMain;
        private ResourceSearchResult? _heroSide;
        private ResourceSearchResult? _heroSmall1;
        private ResourceSearchResult? _heroSmall2;

        /// <summary>首页主推大卡：热门榜第一。</summary>
        public ResourceSearchResult? HeroMain
        {
            get => _heroMain;
            private set => Set(ref _heroMain, value);
        }

        /// <summary>首页 Hero 右侧大卡：热门榜第二。</summary>
        public ResourceSearchResult? HeroSide
        {
            get => _heroSide;
            private set => Set(ref _heroSide, value);
        }

        /// <summary>首页 Hero 右下小卡。</summary>
        public ResourceSearchResult? HeroSmall1
        {
            get => _heroSmall1;
            private set => Set(ref _heroSmall1, value);
        }

        /// <summary>首页 Hero 右下小卡。</summary>
        public ResourceSearchResult? HeroSmall2
        {
            get => _heroSmall2;
            private set => Set(ref _heroSmall2, value);
        }

        /// <summary>发现页：最近更新的资源。</summary>
        public ObservableCollection<ResourceSearchResult> Latest { get; } = new();

        /// <summary>分类筛选列表（下标 0 对应「全部」）。</summary>
        public ObservableCollection<CategoryOption> Categories { get; } = new();

        public ObservableCollection<string> GameVersions { get; } = new();
        public ObservableCollection<GameInstance> Instances => AppServices.Instances.Instances;
        public ObservableCollection<InstalledResource> Installed { get; } = new();

        public string Query
        {
            get => _query;
            set => Set(ref _query, value);
        }

        public int PlatformIndex
        {
            get => _platformIndex;
            set => Set(ref _platformIndex, value);
        }

        public int TypeIndex
        {
            get => _typeIndex;
            set
            {
                if (!Set(ref _typeIndex, value)) return;
                // 资源类型变化后，分类列表与推荐榜单都需要重新拉取
                _ = LoadCategoriesAsync();
                if (IsDiscoverMode) _ = LoadDiscoverAsync();
            }
        }

        public string? SelectedGameVersion
        {
            get => _selectedGameVersion;
            set => Set(ref _selectedGameVersion, value);
        }

        public string SelectedLoader
        {
            get => _selectedLoader;
            set => Set(ref _selectedLoader, value);
        }

        public GameInstance? TargetInstance
        {
            get => _targetInstance;
            set
            {
                if (Set(ref _targetInstance, value))
                {
                    _ = LoadInstalledAsync();
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => Set(ref _isBusy, value);
        }

        /// <summary>每页条目数（Modrinth 上限 100，CurseForge 上限 50）。</summary>
        public int PageSize { get; } = 20;

        public int CurrentPage
        {
            get => _currentPage;
            private set => Set(ref _currentPage, value);
        }

        public int TotalPages
        {
            get => _totalPages;
            private set => Set(ref _totalPages, value);
        }

        public int TotalCount
        {
            get => _totalCount;
            private set => Set(ref _totalCount, value);
        }

        public string PageText => $"第 {CurrentPage} / {TotalPages} 页 · 共 {TotalCount} 个结果";

        public bool CanGoPrev => CurrentPage > 1;

        public bool CanGoNext => CurrentPage < TotalPages;

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        /// <summary>当前资源类型（由类型下拉换算）。</summary>
        private ResourceType CurrentType => TypeIndex switch
        {
            1 => ResourceType.Modpack,
            2 => ResourceType.Shader,
            3 => ResourceType.ResourcePack,
            _ => ResourceType.Mod
        };

        /// <summary>排序维度：0 相关度 / 1 下载量 / 2 关注数 / 3 最近更新。</summary>
        public int SortIndex
        {
            get => _sortIndex;
            set => Set(ref _sortIndex, value);
        }

        private string SortKey => SortIndex switch
        {
            1 => "downloads",
            2 => "follows",
            3 => "updated",
            _ => "relevance"
        };

        /// <summary>分类筛选下标，0 表示「全部」。切换后重新拉取发现页榜单。</summary>
        public int CategoryIndex
        {
            get => _categoryIndex;
            set
            {
                if (!Set(ref _categoryIndex, value)) return;
                // 手动选择分类后，解除分类主题页固定下来的筛选条件
                _categoryKeyOverride = null;
                if (IsDiscoverMode) _ = LoadDiscoverAsync();
            }
        }

        private string? SelectedCategory
        {
            get
            {
                // 分类主题页会直接给定分类键，此时不再跟随下拉框索引
                if (_categoryKeyOverride is not null) return _categoryKeyOverride;
                if (CategoryIndex <= 0 || CategoryIndex >= Categories.Count) return null;
                var key = Categories[CategoryIndex].Key;
                return string.IsNullOrEmpty(key) ? null : key;
            }
        }

        /// <summary>发现模式：未检索时展示推荐与榜单。</summary>
        public bool IsDiscoverMode
        {
            get => _isDiscoverMode;
            set
            {
                if (Set(ref _isDiscoverMode, value)) Raise(nameof(IsResultMode));
            }
        }

        public bool IsResultMode => !IsDiscoverMode;

        public bool IsLoadingDiscover
        {
            get => _isLoadingDiscover;
            set => Set(ref _isLoadingDiscover, value);
        }

        public bool HasResults => Results.Count > 0;

        public AsyncRelayCommand SearchCommand { get; }
        public AsyncRelayCommand ImportModpackCommand { get; }
        public AsyncRelayCommand InstallLocalFileCommand { get; }

        public AsyncRelayCommand FirstPageCommand { get; }
        public AsyncRelayCommand PrevPageCommand { get; }
        public AsyncRelayCommand NextPageCommand { get; }
        public AsyncRelayCommand LastPageCommand { get; }

        /// <summary>从检索结果返回发现页。</summary>
        public RelayCommand ShowDiscoverCommand { get; }

        /// <summary>由页面注入的本地资源文件选择回调。</summary>
        public Func<Task<string?>>? PickLocalResourceFile { get; set; }

        public RelayCommand RefreshInstalledCommand { get; }
        public RelayCommand RemoveInstalledCommand { get; }
        public RelayCommand ToggleInstalledCommand { get; }

        /// <summary>由页面注入的文件选择回调（导入整合包）。</summary>
        public Func<Task<string?>>? PickModpackFile { get; set; }

        public StoreViewModel()
        {
            SearchCommand = new AsyncRelayCommand(SearchAsync);
            ImportModpackCommand = new AsyncRelayCommand(ImportModpackAsync);
            InstallLocalFileCommand = new AsyncRelayCommand(InstallLocalFileAsync);

            FirstPageCommand = new AsyncRelayCommand(() => GoToPageAsync(1), () => CanGoPrev);
            PrevPageCommand = new AsyncRelayCommand(() => GoToPageAsync(CurrentPage - 1), () => CanGoPrev);
            NextPageCommand = new AsyncRelayCommand(() => GoToPageAsync(CurrentPage + 1), () => CanGoNext);
            LastPageCommand = new AsyncRelayCommand(() => GoToPageAsync(TotalPages), () => CanGoNext);
            RefreshInstalledCommand = new RelayCommand(() => _ = LoadInstalledAsync());
            RemoveInstalledCommand = new RelayCommand(p =>
            {
                if (p is InstalledResource r && TargetInstance is not null) RemoveInstalled(r);
            });
            ToggleInstalledCommand = new RelayCommand(p =>
            {
                if (p is InstalledResource r && TargetInstance is not null)
                {
                    if (InstanceService.ToggleResource(TargetInstance, r)) _ = LoadInstalledAsync();
                }
            });
            ShowDiscoverCommand = new RelayCommand(() => IsDiscoverMode = true);

            // 结果集变化时同步空状态提示
            Results.CollectionChanged += (_, _) => Raise(nameof(HasResults));
        }

        public async Task InitializeAsync()
        {
            try
            {
                var versions = await AppServices.Catalog.GetVersionListAsync(false);
                await UiDispatcher.RunAsync(() =>
                {
                    GameVersions.Clear();
                    GameVersions.Add("");
                    foreach (var v in versions) GameVersions.Add(v.Id);
                });

                var last = AppServices.Settings.Settings.LastInstanceId;
                var instance = AppServices.Instances.Get(last) ?? Instances.FirstOrDefault();
                if (instance is not null)
                {
                    await UiDispatcher.RunAsync(() =>
                    {
                        TargetInstance = instance;
                        SelectedGameVersion = instance.GameVersion;
                    });
                }

                await LoadCategoriesAsync();
                await LoadDiscoverAsync();
            }
            catch (Exception ex)
            {
                StatusText = "初始化失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 搜索页专用初始化：只准备筛选条件，不加载发现页榜单（避免多余请求）。
        /// </summary>
        public async Task InitializeForSearchAsync()
        {
            try
            {
                var versions = await AppServices.Catalog.GetVersionListAsync(false);
                await UiDispatcher.RunAsync(() =>
                {
                    GameVersions.Clear();
                    GameVersions.Add("");
                    foreach (var v in versions) GameVersions.Add(v.Id);
                });

                var last = AppServices.Settings.Settings.LastInstanceId;
                var instance = AppServices.Instances.Get(last) ?? Instances.FirstOrDefault();
                if (instance is not null)
                    await UiDispatcher.RunAsync(() => TargetInstance = instance);

                await LoadCategoriesAsync();
            }
            catch (Exception ex)
            {
                StatusText = "初始化失败：" + ex.Message;
            }
        }

        /// <summary>用指定关键词执行一次检索（供详情页跳转时调用）。</summary>
        public async Task SearchWithAsync(string? query)
        {
            if (IsBusy) return;
            Query = query ?? "";
            CurrentPage = 1;
            TotalPages = 1;
            TotalCount = 0;
            IsDiscoverMode = false;
            await LoadPageAsync();
        }

        /// <summary>
        /// 按分类键检索（分类主题页专用）：直接以该分类作为唯一筛选条件，
        /// 不依赖下拉框索引，避免分类列表尚未加载完成时定位不到分类。
        /// </summary>
        public async Task SearchByCategoryAsync(string? categoryKey, int sortIndex)
        {
            _categoryKeyOverride = string.IsNullOrEmpty(categoryKey) ? null : categoryKey;
            SortIndex = sortIndex;
            await SearchWithAsync(null);
        }

        /// <summary>拉取当前资源类型的分类标签。</summary>
        private async Task LoadCategoriesAsync()
        {
            try
            {
                var list = await AppServices.Modrinth.GetCategoriesAsync(CurrentType);
                await UiDispatcher.RunAsync(() =>
                {
                    Categories.Clear();
                    Categories.Add(new CategoryOption("", "全部"));
                    foreach (var item in list)
                        Categories.Add(new CategoryOption(item, Core.ResourceTags.ToChinese(item)));
                    if (CategoryIndex >= Categories.Count) CategoryIndex = 0;
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("加载分类失败：" + ex.Message);
            }
        }

        /// <summary>加载发现页的「热门下载」与「最近更新」两个榜单。</summary>
        private async Task LoadDiscoverAsync()
        {
            if (IsLoadingDiscover) return;
            IsLoadingDiscover = true;
            try
            {
                var type = CurrentType;
                var category = SelectedCategory;
                var popularTask = AppServices.Modrinth.SearchAsync("", type, null, null, 8, 0, category, "downloads");
                var latestTask = AppServices.Modrinth.SearchAsync("", type, null, null, 8, 0, category, "updated");
                var popular = await popularTask;
                var latest = await latestTask;
                var items = popular.Items;

                await UiDispatcher.RunAsync(() =>
                {
                    Popular.Clear();
                    foreach (var item in items) Popular.Add(item);
                    Latest.Clear();
                    foreach (var item in latest.Items) Latest.Add(item);

                    // 首页 Hero：按顺序取热门榜前四个（一大三小）
                    HeroMain = items.Count > 0 ? items[0] : null;
                    HeroSide = items.Count > 1 ? items[1] : null;
                    HeroSmall1 = items.Count > 2 ? items[2] : null;
                    HeroSmall2 = items.Count > 3 ? items[3] : null;
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("加载推荐榜单失败：" + ex.Message);
            }
            finally
            {
                IsLoadingDiscover = false;
            }
        }

        /// <summary>开始新的检索：回到第一页。</summary>
        private async Task SearchAsync()
        {
            if (IsBusy) return;
            CurrentPage = 1;
            TotalPages = 1;
            TotalCount = 0;
            // 检索后从发现视图切换到结果视图
            IsDiscoverMode = false;
            await LoadPageAsync();
        }

        /// <summary>加载当前页的数据。</summary>
        private async Task LoadPageAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = $"正在加载第 {CurrentPage} 页…";
            try
            {
                var platform = PlatformIndex == 0 ? ResourcePlatform.Modrinth : ResourcePlatform.CurseForge;
                var type = TypeIndex switch
                {
                    1 => ResourceType.Modpack,
                    2 => ResourceType.Shader,
                    3 => ResourceType.ResourcePack,
                    _ => ResourceType.Mod
                };
                var loader = SelectedLoader == "Any" ? null : SelectedLoader;
                var offset = (CurrentPage - 1) * PageSize;

                var page = platform == ResourcePlatform.Modrinth
                    ? await AppServices.Modrinth.SearchAsync(Query, type, SelectedGameVersion, loader,
                        PageSize, offset, SelectedCategory, SortKey)
                    : await AppServices.CurseForge.SearchAsync(Query, type, SelectedGameVersion, loader,
                        PageSize, offset, SortKey);

                TotalCount = page.TotalCount;
                // 服务端对翻页偏移有上限（Modrinth 约 10000 条），页码做一次收敛
                var pageCount = (int)Math.Ceiling(TotalCount / (double)PageSize);
                TotalPages = Math.Clamp(pageCount, 1, 500);

                // 结果数异常时回退页码（例如筛选条件变化后超出范围）
                if (TotalCount > 0 && CurrentPage > TotalPages)
                {
                    CurrentPage = TotalPages;
                    IsBusy = false;
                    await LoadPageAsync();
                    return;
                }

                await UiDispatcher.RunAsync(() =>
                {
                    Results.Clear();
                    foreach (var item in page.Items) Results.Add(item);
                });

                StatusText = Results.Count == 0
                    ? "没有匹配的结果，请调整关键词或筛选条件"
                    : PageText;
            }
            catch (Exception ex)
            {
                StatusText = "检索失败：" + ex.Message;
                AppServices.NotifyError("检索失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
                NotifyPageCommands();
            }
        }

        private async Task GoToPageAsync(int page)
        {
            if (IsBusy) return;
            var target = Math.Clamp(page, 1, Math.Max(1, TotalPages));
            if (target == CurrentPage) return;
            CurrentPage = target;
            await LoadPageAsync();
        }

        private void NotifyPageCommands()
        {
            Raise(nameof(PageText), nameof(CanGoPrev), nameof(CanGoNext),
                nameof(CurrentPage), nameof(TotalPages), nameof(TotalCount));
            FirstPageCommand.NotifyCanExecuteChanged();
            PrevPageCommand.NotifyCanExecuteChanged();
            NextPageCommand.NotifyCanExecuteChanged();
            LastPageCommand.NotifyCanExecuteChanged();
        }

        private async Task ImportModpackAsync()
        {
            if (PickModpackFile is null) return;
            var file = await PickModpackFile();
            if (string.IsNullOrEmpty(file)) return;

            IsBusy = true;
            try
            {
                var instance = await AppServices.Resources.ImportModpackAsync(file!,
                    progress: new Progress<InstallProgress>(p => UiDispatcher.Run(() => StatusText = p.Text)));
                StatusText = $"整合包已导入为游戏版本「{instance.Name}」";
                AppServices.NotifyInfo("整合包导入完成：" + instance.Name);
            }
            catch (Exception ex)
            {
                StatusText = "导入失败：" + ex.Message;
                AppServices.NotifyError("导入失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 手动安装本地文件（.jar / .zip / .mrpack）到目标游戏版本。
        /// </summary>
        private async Task InstallLocalFileAsync()
        {
            if (TargetInstance is null)
            {
                StatusText = "请先选择目标游戏版本";
                AppServices.NotifyError("未选择游戏版本", "请在上方选择要安装到的游戏版本");
                return;
            }
            if (PickLocalResourceFile is null) return;

            var file = await PickLocalResourceFile();
            if (string.IsNullOrEmpty(file)) return;

            var type = TypeIndex switch
            {
                1 => ResourceType.Modpack,
                2 => ResourceType.Shader,
                3 => ResourceType.ResourcePack,
                _ => ResourceType.Mod
            };

            IsBusy = true;
            try
            {
                if (type == ResourceType.Modpack)
                {
                    var instance = await AppServices.Resources.ImportModpackAsync(file!,
                        progress: new Progress<InstallProgress>(p => UiDispatcher.Run(() => StatusText = p.Text)));
                    StatusText = $"整合包已导入为游戏版本「{instance.Name}」";
                    AppServices.NotifyInfo("整合包导入完成：" + instance.Name);
                    return;
                }

                var target = TargetInstance;
                var folder = ResourceInstallService.FolderOf(type);
                var directory = System.IO.Path.Combine(target.Directory, folder);
                System.IO.Directory.CreateDirectory(directory);
                var destination = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(file!));
                StatusText = "正在复制文件…";
                await Task.Run(() => System.IO.File.Copy(file!, destination, true));

                await LoadInstalledAsync();
                StatusText = $"已安装本地文件：{System.IO.Path.GetFileName(destination)}";
                AppServices.NotifyInfo("本地资源已安装到 " + target.Name);
            }
            catch (Exception ex)
            {
                Logger.Error("安装本地资源失败", ex);
                StatusText = "安装失败：" + ex.Message;
                AppServices.NotifyError("安装失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task LoadInstalledAsync()
        {
            if (TargetInstance is null)
            {
                await UiDispatcher.RunAsync(() => Installed.Clear());
                return;
            }
            var instance = TargetInstance;
            var list = await Task.Run(() => InstanceService.ScanResources(instance));
            await UiDispatcher.RunAsync(() =>
            {
                Installed.Clear();
                foreach (var item in list) Installed.Add(item);
            });
        }

        private void RemoveInstalled(InstalledResource resource)
        {
            if (TargetInstance is null) return;
            var folder = ResourceInstallService.FolderOf(resource.Type);
            var path = System.IO.Path.Combine(TargetInstance.Directory, folder, resource.FileName);
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                TargetInstance.InstalledResources.RemoveAll(r => r.FileName == resource.FileName);
                AppServices.Instances.Save();
                _ = LoadInstalledAsync();
            }
            catch (Exception ex)
            {
                AppServices.NotifyError("删除失败", ex.Message);
            }
        }
    }
}
