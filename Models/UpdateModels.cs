using System.Collections.Generic;
using App1.Core;

namespace App1.Models
{
    /// <summary>
    /// 远端更新清单（增量更新：仅下载哈希发生变化的文件）。
    /// </summary>
    public sealed class UpdateManifest
    {
        public string Version { get; set; } = "";
        public string? Notes { get; set; }
        public bool Mandatory { get; set; }
        public string? DownloadPageUrl { get; set; }
        public List<UpdateFileEntry> Files { get; set; } = new();
    }

    public sealed class UpdateFileEntry
    {
        /// <summary>相对于应用安装目录的相对路径。</summary>
        public string Path { get; set; } = "";

        public string Url { get; set; } = "";

        /// <summary>目标文件的 SHA256，用于判定是否需要更新。</summary>
        public string Sha256 { get; set; } = "";

        public long Size { get; set; }

        /// <summary>可选：增量补丁包地址（基于本地旧版本文件的二进制补丁）。</summary>
        public string? PatchUrl { get; set; }

        public long? PatchSize { get; set; }

        /// <summary>补丁算法：目前支持 full（整文件替换）与 delta（差异补丁包）。</summary>
        public string? PatchAlgorithm { get; set; }
    }

    /// <summary>
    /// 更新检查结果。
    /// </summary>
    public sealed class UpdateCheckResult
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string? Notes { get; set; }
        public bool Mandatory { get; set; }
        public string? DownloadPageUrl { get; set; }
        public long TotalBytes { get; set; }
        public int FileCount { get; set; }
        public List<UpdateFileEntry> FilesToUpdate { get; set; } = new();
        public string? Error { get; set; }

        public string TotalSizeText => TotalBytes.FormatSize();
    }
}
