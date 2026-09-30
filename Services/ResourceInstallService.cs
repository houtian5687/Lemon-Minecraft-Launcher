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
    /// 资源（模组 / 光影 / 材质包 / 整合包）的安装与整合包导入。
    /// </summary>
    public sealed class ResourceInstallService
    {
        public static string FolderOf(ResourceType type) => type switch
        {
            ResourceType.Mod => "mods",
            ResourceType.Shader => "shaderpacks",
            ResourceType.ResourcePack => "resourcepacks",
            _ => "mods"
        };

        /// <summary>
        /// 安装单个资源文件到指定实例。
        /// </summary>
        public async Task<bool> InstallAsync(GameInstance instance, ResourceSearchResult project,
            ResourceVersion version, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        {
            if (project.Type == ResourceType.Modpack)
                throw new InvalidOperationException("整合包请使用导入功能");

            var url = version.DownloadUrl;
            if (string.IsNullOrEmpty(url))
            {
                if (project.Platform == ResourcePlatform.CurseForge)
                {
                    url = await AppServices.CurseForge.ResolveDownloadUrlAsync(project.Id, version.Id, version.FileName);
                }
                if (string.IsNullOrEmpty(url)) throw new IOException("无法获取下载地址");
            }

            var folder = Path.Combine(instance.Directory, FolderOf(project.Type));
            Directory.CreateDirectory(folder);
            var fileName = version.FileName ?? $"{project.Slug}-{version.Id}.jar";
            var dest = Path.Combine(folder, Sanitize(fileName));

            progress?.Report(new InstallProgress { Phase = "下载资源：" + project.Name, Percent = 5 });
            var item = AppServices.Downloads.Enqueue(url!, dest, version.Sha1,
                version.FileSize > 0 ? version.FileSize : null,
                groupId: $"res:{instance.Id}:{project.Id}", groupName: project.Name,
                fileName: $"{project.Name} · {version.Name}", iconUrl: project.IconUrl);

            if (progress is not null)
            {
                _ = Task.Run(async () =>
                {
                    while (item.IsRunning)
                    {
                        progress.Report(new InstallProgress
                        {
                            Phase = "下载资源：" + project.Name,
                            Detail = item.DetailText,
                            Percent = 5 + item.Progress * 0.9
                        });
                        await Task.Delay(300, CancellationToken.None);
                    }
                }, CancellationToken.None);
            }

            var ok = await AppServices.Downloads.WaitAsync(item);
            if (!ok) throw new IOException($"下载失败：{item.ErrorMessage}");

            instance.InstalledResources.RemoveAll(r =>
                string.Equals(r.FileName, Path.GetFileName(dest), StringComparison.OrdinalIgnoreCase));
            instance.InstalledResources.Add(new InstalledResource
            {
                Id = project.Id,
                Name = project.Name,
                FileName = Path.GetFileName(dest),
                Type = project.Type,
                Platform = project.Platform,
                VersionId = version.Id,
                VersionName = version.Name,
                Url = url,
                InstalledAt = DateTime.Now
            });
            AppServices.Instances.Save();
            progress?.Report(new InstallProgress { Phase = "安装完成", Percent = 100 });
            Logger.Info($"已安装资源 {project.Name} → {instance.Name}");
            return true;
        }

        /// <summary>
        /// 导入 Modrinth 整合包（.mrpack），自动创建实例并安装依赖。
        /// </summary>
        public async Task<GameInstance> ImportModpackAsync(string mrpackPath, string? instanceName = null,
            IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        {
            if (!File.Exists(mrpackPath)) throw new FileNotFoundException("整合包文件不存在", mrpackPath);

            ModpackIndex? index = null;
            using (var archive = ZipFile.OpenRead(mrpackPath))
            {
                var entry = archive.GetEntry("modrinth.index.json")
                            ?? throw new InvalidDataException("不是有效的 Modrinth 整合包");
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                index = Json.Deserialize<ModpackIndex>(await reader.ReadToEndAsync());
            }
            if (index is null) throw new InvalidDataException("整合包索引解析失败");

            var gameVersion = index.Dependencies.Minecraft ?? throw new InvalidDataException("整合包缺少游戏版本信息");
            var (loader, loaderVersion) = index.Dependencies switch
            {
                { FabricLoader: not null } => (LoaderType.Fabric, index.Dependencies.FabricLoader),
                { QuiltLoader: not null } => (LoaderType.Quilt, index.Dependencies.QuiltLoader),
                { NeoForge: not null } => (LoaderType.NeoForge, index.Dependencies.NeoForge),
                { Forge: not null } => (LoaderType.Forge, index.Dependencies.Forge),
                _ => (LoaderType.Vanilla, null)
            };

            var name = instanceName ?? index.Name;
            progress?.Report(new InstallProgress { Phase = "创建游戏版本并安装加载器", Percent = 2 });
            var instance = await AppServices.Instances.CreateAsync(name, gameVersion, loader, loaderVersion,
                AppServices.Settings.Settings.DefaultMaxMemoryMb, progress: progress, ct: ct);

            // 解压 overrides
            progress?.Report(new InstallProgress { Phase = "解压整合包覆盖文件", Percent = 60 });
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(mrpackPath);
                foreach (var entry in archive.Entries)
                {
                    var full = entry.FullName;
                    if (!full.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) &&
                        !full.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = full.Contains("client-overrides/")
                        ? full[(full.IndexOf("client-overrides/", StringComparison.OrdinalIgnoreCase) + "client-overrides/".Length)..]
                        : full["overrides/".Length..];
                    if (string.IsNullOrEmpty(relative)) continue;
                    var target = Path.Combine(instance.Directory, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    try { entry.ExtractToFile(target, true); }
                    catch (Exception ex) { Logger.Warn($"解压覆盖文件失败 {full}：{ex.Message}"); }
                }
            }, ct);

            // 下载整合包内声明的文件
            var requests = new List<DownloadRequest>();
            foreach (var file in index.Files)
            {
                var url = file.Downloads.FirstOrDefault();
                if (string.IsNullOrEmpty(url)) continue;
                var target = Path.Combine(instance.Directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (target.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    !target.Contains("mods", StringComparison.OrdinalIgnoreCase))
                {
                    // 部分整合包把 mod 放在自定义目录，统一放入 mods
                    target = Path.Combine(instance.Directory, "mods", Path.GetFileName(file.Path));
                }
                requests.Add(new DownloadRequest
                {
                    Url = url,
                    Destination = target,
                    Sha1 = file.Hashes.TryGetValue("sha1", out var sha1) ? sha1 : null,
                    Size = file.FileSize > 0 ? file.FileSize : null,
                    FileName = Path.GetFileName(file.Path)
                });
            }

            progress?.Report(new InstallProgress { Phase = "下载整合包文件", Percent = 65 });
            if (requests.Count > 0)
            {
                var result = await AppServices.Downloads.DownloadAllAsync(requests, index.Name,
                    new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress
                    {
                        Phase = "下载整合包文件",
                        Detail = p.CurrentFile,
                        Current = p.Completed,
                        Total = p.Total,
                        Percent = 65 + p.Percent * 0.34
                    })), ct, $"modpack:{instance.Id}");
                if (!result.Success)
                    Logger.Warn("整合包部分文件下载失败：" + string.Join("；", result.Errors.Take(3)));
            }

            instance.Notes = index.Summary;
            AppServices.Instances.Save();
            progress?.Report(new InstallProgress { Phase = "整合包导入完成", Percent = 100 });
            Logger.Info($"整合包导入完成：{index.Name}（{requests.Count} 个文件）");
            return instance;
        }

        private static string Sanitize(string fileName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
