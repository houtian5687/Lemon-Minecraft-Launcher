using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// 游戏本体的安装：客户端 jar、依赖库、native 解压与资源文件。
    /// </summary>
    public sealed class GameInstallService
    {
        private readonly VersionCatalogService _catalog = AppServices.Catalog;

        /// <summary>
        /// 确保指定版本完整可运行，返回解析后的规格。
        /// groupId / groupName 用于把整个安装过程的下载聚合为同一个任务组。
        /// </summary>
        public async Task<GameVersionSpec> EnsureVersionAsync(string versionId,
            IProgress<InstallProgress>? progress = null, CancellationToken ct = default,
            string? groupId = null, string? groupName = null)
        {
            groupId ??= "install:" + versionId;
            groupName ??= versionId;

            progress?.Report(new InstallProgress { Phase = "解析版本元数据", Percent = 2 });
            var spec = await _catalog.ResolveAsync(versionId, ct);

            var steps = new List<(string Phase, Func<Task> Action)>();

            steps.Add(("下载游戏主程序", async () =>
            {
                if (string.IsNullOrEmpty(spec.ClientJar.Url)) return;
                var dest = Path.Combine(AppPaths.VersionDir(versionId), versionId + ".jar");
                spec.ClientJar.Path = dest;
                await DownloadFileAsync(spec.ClientJar.Url, dest, spec.ClientJar.Sha1, spec.ClientJar.Size,
                    versionId + ".jar", progress, ct, groupId, groupName, spec.ClientJar.FallbackUrls);
            }));

            steps.Add(("下载依赖库", async () =>
            {
                var requests = new List<DownloadRequest>();
                foreach (var lib in spec.Libraries)
                {
                    var dest = Path.Combine(AppPaths.LibrariesRoot, lib.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(dest) && lib.Size is > 0 && new FileInfo(dest).Length == lib.Size.Value) continue;
                    requests.Add(new DownloadRequest
                    {
                        Url = lib.Url,
                        Destination = dest,
                        Sha1 = lib.Sha1,
                        Size = lib.Size,
                        FileName = Path.GetFileName(lib.Path),
                        Optional = lib.IsOptional,
                        FallbackUrls = lib.FallbackUrls
                    });
                }
                if (requests.Count == 0)
                {
                    progress?.Report(new InstallProgress { Phase = "依赖库已完整", Percent = 45 });
                    return;
                }
                var result = await AppServices.Downloads.DownloadAllAsync(requests, groupName,
                    new Progress<DownloadProgress>(p =>
                        progress?.Report(new InstallProgress
                        {
                            Phase = "下载依赖库",
                            Detail = p.CurrentFile,
                            Current = p.Completed,
                            Total = p.Total,
                            Percent = 10 + p.Percent * 0.35
                        })), ct, groupId);
                if (!result.Success)
                    throw new IOException("部分依赖库下载失败：" + string.Join("；", result.Errors.Take(3)));
            }));

            steps.Add(("解压本地库", async () =>
            {
                var nativesDir = Path.Combine(AppPaths.VersionDir(versionId), "natives");
                Directory.CreateDirectory(nativesDir);
                var natives = spec.Libraries.Where(l => l.IsNative).ToList();
                for (int i = 0; i < natives.Count; i++)
                {
                    var lib = natives[i];
                    var jar = Path.Combine(AppPaths.LibrariesRoot, lib.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(jar)) continue;
                    ExtractNatives(jar, nativesDir, lib.ExtractExclude);
                    progress?.Report(new InstallProgress
                    {
                        Phase = "解压本地库",
                        Current = i + 1,
                        Total = natives.Count,
                        Percent = 45 + (double)(i + 1) / natives.Count * 5
                    });
                }
            }));

            steps.Add(("下载游戏资源", async () =>
            {
                await EnsureAssetsAsync(spec, progress, ct, groupId, groupName);
            }));

            foreach (var (phase, action) in steps)
            {
                ct.ThrowIfCancellationRequested();
                Logger.Info($"[安装] {phase}");
                await action();
            }

            progress?.Report(new InstallProgress { Phase = "安装完成", Percent = 100 });
            Logger.Info($"版本 {versionId} 安装完成");
            return spec;
        }

        private async Task DownloadFileAsync(string url, string dest, string? sha1, long? size, string? name,
            IProgress<InstallProgress>? progress, CancellationToken ct, string? groupId = null, string? groupName = null,
            IEnumerable<string>? fallbackUrls = null)
        {
            var item = AppServices.Downloads.Enqueue(url, dest, sha1, size,
                groupId: groupId, groupName: groupName, fileName: name, fallbackUrls: fallbackUrls);
            if (progress is not null)
            {
                _ = Task.Run(async () =>
                {
                    while (item.IsRunning)
                    {
                        progress.Report(new InstallProgress
                        {
                            Phase = "下载游戏主程序",
                            Detail = name,
                            Percent = 2 + item.Progress * 0.08
                        });
                        await Task.Delay(300, CancellationToken.None);
                    }
                }, CancellationToken.None);
            }
            var ok = await AppServices.Downloads.WaitAsync(item);
            if (!ok) throw new IOException($"下载失败：{name} - {item.ErrorMessage}");
        }

        private async Task EnsureAssetsAsync(GameVersionSpec spec, IProgress<InstallProgress>? progress,
            CancellationToken ct, string? groupId = null, string? groupName = null)
        {
            if (spec.AssetIndex is null || string.IsNullOrEmpty(spec.AssetIndex.Url)) return;

            var indexId = spec.AssetIndex.Id;
            var indexPath = Path.Combine(AppPaths.AssetsIndexesRoot, indexId + ".json");
            if (!File.Exists(indexPath))
            {
                var item = AppServices.Downloads.Enqueue(spec.AssetIndex.Url, indexPath, spec.AssetIndex.Sha1,
                    spec.AssetIndex.Size, groupId: groupId, groupName: groupName,
                    fileName: "资源索引 " + indexId,
                    fallbackUrls: VersionCatalogService.BuildFallbacks(spec.AssetIndex.Url));
                if (!await AppServices.Downloads.WaitAsync(item))
                    throw new IOException("资源索引下载失败：" + item.ErrorMessage);
            }

            var index = Json.DeserializeFromFile<AssetIndexFile>(indexPath);
            if (index is null || index.Objects.Count == 0) return;

            var requests = new List<DownloadRequest>();
            foreach (var (key, obj) in index.Objects)
            {
                var relative = obj.Hash[..2] + "/" + obj.Hash;
                var dest = Path.Combine(AppPaths.AssetsObjectsRoot, obj.Hash[..2], obj.Hash);
                if (File.Exists(dest) && new FileInfo(dest).Length == obj.Size) continue;
                var url = _catalog.AssetsBase + relative;
                requests.Add(new DownloadRequest
                {
                    Url = url,
                    Destination = dest,
                    Sha1 = obj.Hash,
                    Size = obj.Size,
                    FileName = key,
                    FallbackUrls = VersionCatalogService.BuildFallbacks(url)
                });
            }

            if (requests.Count == 0)
            {
                progress?.Report(new InstallProgress { Phase = "游戏资源已完整", Percent = 100 });
            }
            else
            {
                var result = await AppServices.Downloads.DownloadAllAsync(requests, groupName ?? indexId,
                    new Progress<DownloadProgress>(p =>
                        progress?.Report(new InstallProgress
                        {
                            Phase = "下载游戏资源",
                            Detail = p.CurrentFile,
                            Current = p.Completed,
                            Total = p.Total,
                            Percent = 50 + p.Percent * 0.45
                        })), ct, groupId);
                if (!result.Success)
                    Logger.Warn("部分资源文件下载失败：" + string.Join("；", result.Errors.Take(3)));
            }

            // legacy 资源映射（1.7.2 及更早 / 虚拟资源目录）
            if (spec.MapToResources || spec.UseVirtualAssets || index.MapToResources == true || index.Virtual == true)
            {
                await Task.Run(() => MapLegacyAssets(spec, index), ct);
            }
        }

        private static void MapLegacyAssets(GameVersionSpec spec, AssetIndexFile index)
        {
            var targetRoot = spec.MapToResources
                ? AppPaths.ResourcesRoot
                : Path.Combine(AppPaths.AssetsRoot, "virtual", string.IsNullOrEmpty(spec.LegacyAssetsId) ? "legacy" : spec.LegacyAssetsId!);
            Directory.CreateDirectory(targetRoot);
            int done = 0;
            foreach (var (key, obj) in index.Objects)
            {
                var source = Path.Combine(AppPaths.AssetsObjectsRoot, obj.Hash[..2], obj.Hash);
                if (!File.Exists(source)) continue;
                var target = Path.Combine(targetRoot, key.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, true);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"映射资源失败 {key}：{ex.Message}");
                }
                done++;
            }
            Logger.Info($"已映射 {done} 个 legacy 资源文件到 {targetRoot}");
        }

        /// <summary>
        /// 解压 native 库，跳过 exclude 中的条目（通常为 META-INF）。
        /// </summary>
        public static void ExtractNatives(string jarPath, string targetDir, List<string>? exclude)
        {
            try
            {
                using var archive = ZipFile.OpenRead(jarPath);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (exclude is not null && exclude.Any(e =>
                            entry.FullName.StartsWith(e.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var target = Path.Combine(targetDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    try
                    {
                        entry.ExtractToFile(target, true);
                    }
                    catch (IOException)
                    {
                        // 文件被占用时忽略，避免安装中断
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"解压 native 失败 {jarPath}：{ex.Message}");
            }
        }

        /// <summary>
        /// 校验版本完整性（缺失文件数量）。
        /// </summary>
        public static VersionIntegrity CheckIntegrity(GameVersionSpec spec)
        {
            var missing = new List<string>();
            var clientJar = Path.Combine(AppPaths.VersionDir(spec.Id), spec.Id + ".jar");
            if (!File.Exists(clientJar)) missing.Add("客户端 jar");

            foreach (var lib in spec.Libraries)
            {
                var path = Path.Combine(AppPaths.LibrariesRoot, lib.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) missing.Add(lib.Name);
            }
            return new VersionIntegrity(spec.Libraries.Count + 1, missing);
        }
    }

    public sealed class VersionIntegrity(int total, List<string> missing)
    {
        public int Total { get; } = total;
        public List<string> Missing { get; } = missing;
        public bool IsComplete => Missing.Count == 0;
        public string Text => IsComplete ? "完整" : $"缺失 {Missing.Count} 个文件";
    }
}
