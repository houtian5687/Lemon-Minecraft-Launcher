using System;
using System.Collections.Generic;
using System.Linq;
using App1.Core;

namespace App1.Models
{
    /// <summary>
    /// 资源检索结果的统一视图模型（抹平 Modrinth / CurseForge 差异）。
    /// </summary>
    public sealed class ResourceSearchResult : ObservableObject
    {
        /// <summary>
        /// Modrinth 的 categories 字段会混入加载器标识（fabric / forge 等），
        /// 判断「分类」时必须先剔除，否则卡片上的分类标签会显示成加载器名，
        /// 真正的分类（科技、装饰…）反而被挤掉。
        /// </summary>
        private static readonly HashSet<string> LoaderTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "fabric", "forge", "quilt", "neoforge", "vanilla", "bukkit", "spigot", "paper", "purpur"
        };

        private static bool IsLoaderToken(string value) => LoaderTokens.Contains(value);

        /// <summary>加载器名称的规范写法（NeoForge 这类写法对大小写敏感）。</summary>
        private static string FormatLoader(string raw) => raw.ToLowerInvariant() switch
        {
            "fabric" => "Fabric",
            "forge" => "Forge",
            "quilt" => "Quilt",
            "neoforge" => "NeoForge",
            "vanilla" => "原版",
            _ => raw
        };

        private bool _isInstalled;

        public string Id { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? Description { get; set; }
        public string? IconUrl { get; set; }
        public string? Author { get; set; }
        public long Downloads { get; set; }
        public long Follows { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public ResourceType Type { get; set; }
        public ResourcePlatform Platform { get; set; }
        public List<string> Categories { get; set; } = new();
        public List<string> Loaders { get; set; } = new();
        public List<string> GameVersions { get; set; } = new();
        public string? ProjectUrl { get; set; }
        public string? SourceUrl { get; set; }
        public List<string>? Gallery { get; set; }

        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (Set(ref _isInstalled, value)) Raise(nameof(InstallText));
            }
        }

        public string InstallText => IsInstalled ? "已安装" : "安装";

        public string DownloadsText => Downloads >= 10_000 ? $"{Downloads / 10_000.0:0.#} 万" : Downloads.ToString();

        /// <summary>关注数：Modrinth 未提供评分字段，以关注数作为人气指标。</summary>
        public string FollowsText => Follows >= 10_000 ? $"{Follows / 10_000.0:0.#} 万" : Follows.ToString();

        /// <summary>
        /// 封面图：优先使用第一张截图（用于商店风格的顶部背景与缩略图），
        /// 没有截图时回退到项目图标。
        /// </summary>
        public string? CoverUrl => Gallery is { Count: > 0 } && !string.IsNullOrWhiteSpace(Gallery[0])
            ? Gallery[0]
            : IconUrl;

        /// <summary>分类标签的中文显示文本（已剔除混入的加载器）。</summary>
        public string CategoriesText
        {
            get
            {
                var names = Categories.Where(c => !IsLoaderToken(c))
                    .Take(4)
                    .Select(Core.ResourceTags.ToChinese)
                    .ToList();
                return names.Count == 0 ? "未分类" : string.Join(" · ", names);
            }
        }

        /// <summary>首个真实分类的原始键（已剔除加载器），点击标签时用于定位分类主题页。</summary>
        public string PrimaryCategoryKey => Categories.FirstOrDefault(c => !IsLoaderToken(c)) ?? "";

        /// <summary>首个真实分类的中文名，用于商店卡片上的分类标签。</summary>
        public string PrimaryCategoryText
        {
            get
            {
                var key = PrimaryCategoryKey;
                return string.IsNullOrEmpty(key) ? "未分类" : Core.ResourceTags.ToChinese(key);
            }
        }

        /// <summary>
        /// 首个加载器的显示名（Fabric / Forge / NeoForge 等）。
        /// 卡片上必须给出加载器类型，因此缺失时回退为「通用」而不是留空。
        /// </summary>
        public string PrimaryLoaderText => Loaders.Count > 0 ? FormatLoader(Loaders[0]) : "通用";

        /// <summary>首个加载器的原始键（小写），点击加载器标签时作为筛选条件。</summary>
        public string PrimaryLoaderKey => Loaders.Count > 0 ? Loaders[0].ToLowerInvariant() : "";

        /// <summary>
        /// 规范化后的加载器标识（如 Forge / Fabric / NeoForge），
        /// 用于详情页标题旁的醒目徽标，多个加载器会全部列出。
        /// </summary>
        public IReadOnlyList<string> LoaderBadges => Loaders
            .Select(FormatLoader)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>
        /// 数据源说明：标明资源来自哪个平台，以及推荐的下载方式。
        /// </summary>
        public string SourceTipText => Platform == ResourcePlatform.Modrinth
            ? "数据来源：Modrinth · 推荐由启动器内置下载器安装（自动校验、失败可切镜像）"
            : "数据来源：CurseForge · 推荐由启动器内置下载器安装（无需手动下载再导入）";

        public string UpdatedText => UpdatedAt?.ToString("yyyy-MM-dd") ?? "-";

        public string PlatformText => Platform == ResourcePlatform.Modrinth ? "Modrinth" : "CurseForge";

        public string TypeText => Type.ToText();

        public string LoadersText => Loaders.Count == 0 ? "-" : string.Join(" / ", Loaders);
    }

    /// <summary>
    /// 资源检索结果的一页（用于翻页）。
    /// </summary>
    public sealed class ResourceSearchPage
    {
        public List<ResourceSearchResult> Items { get; set; } = new();

        /// <summary>结果总数（用于计算总页数）。</summary>
        public int TotalCount { get; set; }

        public int Offset { get; set; }

        public int Limit { get; set; } = 20;
    }

    /// <summary>
    /// 资源的可安装版本。
    /// </summary>
    public sealed class ResourceVersion
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? VersionNumber { get; set; }
        public string? Changelog { get; set; }
        public DateTime? PublishedAt { get; set; }
        public List<string> GameVersions { get; set; } = new();
        public List<string> Loaders { get; set; } = new();
        public string? DownloadUrl { get; set; }
        public string? FileName { get; set; }
        public long FileSize { get; set; }
        public string? Sha1 { get; set; }
        public string? Sha512 { get; set; }
        public bool IsPrimary { get; set; }
        public string? ReleaseChannel { get; set; }

        public string SizeText => FileSize.FormatSize();

        public string PublishedText => PublishedAt?.ToString("yyyy-MM-dd") ?? "-";

        /// <summary>适配的游戏版本（用于列表展示）。</summary>
        public string GameVersionsText => GameVersions.Count == 0
            ? "未标注适用版本"
            : string.Join(" / ", GameVersions.Take(6));

        /// <summary>适配的加载器。</summary>
        public string LoadersText => Loaders.Count == 0 ? "-" : string.Join(" / ", Loaders);
    }

    /// <summary>
    /// Modrinth 整合包索引（modrinth.index.json）。
    /// </summary>
    public sealed class ModpackIndex
    {
        public int FormatVersion { get; set; }
        public string Name { get; set; } = "";
        public string? Summary { get; set; }
        public string? VersionId { get; set; }
        public ModpackDependencies Dependencies { get; set; } = new();
        public List<ModpackFile> Files { get; set; } = new();
    }

    public sealed class ModpackDependencies
    {
        public string? Minecraft { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("fabric-loader")]
        public string? FabricLoader { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("quilt-loader")]
        public string? QuiltLoader { get; set; }

        public string? Forge { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("neoforge")]
        public string? NeoForge { get; set; }
    }

    public sealed class ModpackFile
    {
        public string Path { get; set; } = "";
        public Dictionary<string, string> Hashes { get; set; } = new();
        public Dictionary<string, string>? Env { get; set; }
        public List<string> Downloads { get; set; } = new();
        public long FileSize { get; set; }
    }

    /// <summary>
    /// 整合包解析出的加载器需求。
    /// </summary>
    public sealed class ModpackLoaderInfo
    {
        public LoaderType Loader { get; set; } = LoaderType.Vanilla;
        public string? LoaderVersion { get; set; }
        public string GameVersion { get; set; } = "";
    }
}
