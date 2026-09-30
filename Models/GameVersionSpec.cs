using System.Collections.Generic;
using System.Linq;

namespace App1.Models
{
    /// <summary>
    /// 解析后的版本规格：已完成 inheritsFrom 合并、规则过滤与平台 natives 选择，
    /// 启动器直接依据此模型构建启动命令。
    /// </summary>
    public sealed class GameVersionSpec
    {
        public string Id { get; set; } = "";

        /// <summary>继承链上的原版版本号。</summary>
        public string InheritedId { get; set; } = "";

        public string MainClass { get; set; } = "";

        /// <summary>1.12.2 及更早版本使用的整串参数。</summary>
        public string? MinecraftArguments { get; set; }

        /// <summary>JVM 参数模板（含规则，启动时求值）。</summary>
        public List<ResolvedArgument> JvmArguments { get; set; } = new();

        /// <summary>游戏参数模板（含规则，启动时求值）。</summary>
        public List<ResolvedArgument> GameArguments { get; set; } = new();

        public List<ResolvedLibrary> Libraries { get; set; } = new();

        public AssetIndexInfo? AssetIndex { get; set; }

        /// <summary>legacy 资源目录名（如 legacy / 1.7.10）。</summary>
        public string? LegacyAssetsId { get; set; }

        public bool MapToResources { get; set; }

        public bool UseVirtualAssets { get; set; }

        public ResolvedArtifact ClientJar { get; set; } = new();

        public int JavaMajorVersion { get; set; } = 8;

        public LoaderType Loader { get; set; } = LoaderType.Vanilla;

        public string? LoaderVersion { get; set; }

        public string? ReleaseTime { get; set; }

        public string DisplayName => string.IsNullOrEmpty(LoaderVersion)
            ? Id
            : $"{InheritedId} · {Loader.ToText()} {LoaderVersion}";
    }

    /// <summary>
    /// 解析后的启动参数项（保留规则，启动时依据功能开关求值）。
    /// </summary>
    public sealed class ResolvedArgument
    {
        public List<string> Values { get; set; } = new();

        public List<LaunchRule>? Rules { get; set; }

        public static ResolvedArgument Plain(string value) => new() { Values = { value } };

        public static ResolvedArgument Plain(IEnumerable<string> values) => new() { Values = values.ToList() };
    }

    public sealed class ResolvedLibrary
    {
        public string Name { get; set; } = "";

        /// <summary>相对于 libraries 目录的相对路径（含 .jar）。</summary>
        public string Path { get; set; } = "";

        public string Url { get; set; } = "";

        public string? Sha1 { get; set; }

        public long? Size { get; set; }

        /// <summary>是否为平台相关的 native 库。</summary>
        public bool IsNative { get; set; }

        public List<string>? ExtractExclude { get; set; }

        /// <summary>native 分类器名称（如 natives-windows-64）。</summary>
        public string? NativeClassifier { get; set; }

        /// <summary>
        /// 可选文件：旧格式依赖库（未声明 downloads）中，部分文件在仓库中并不存在，
        /// 下载失败时不应中断整个安装流程。
        /// </summary>
        public bool IsOptional { get; set; }

        /// <summary>备用下载地址（主地址失败后依次尝试）。</summary>
        public List<string>? FallbackUrls { get; set; }
    }

    public sealed class ResolvedArtifact
    {
        public string Url { get; set; } = "";
        public string? Sha1 { get; set; }
        public long? Size { get; set; }
        public string Path { get; set; } = "";

        /// <summary>备用下载地址（主地址失败后依次尝试，如官方源 ↔ 镜像互备）。</summary>
        public List<string>? FallbackUrls { get; set; }
    }

    public sealed class AssetIndexInfo
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Sha1 { get; set; }
        public long? Size { get; set; }
        public long? TotalSize { get; set; }
    }

    /// <summary>
    /// 资源索引文件内容（1.6+ 使用 objects 哈希存储）。
    /// </summary>
    public sealed class AssetIndexFile
    {
        public Dictionary<string, AssetObject> Objects { get; set; } = new();
        public bool? Virtual { get; set; }
        public bool? MapToResources { get; set; }
    }

    public sealed class AssetObject
    {
        public string Hash { get; set; } = "";
        public long Size { get; set; }
    }
}
