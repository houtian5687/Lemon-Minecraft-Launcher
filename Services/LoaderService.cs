using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// 模组加载器（Fabric / Quilt / Forge / NeoForge）的版本获取与安装。
    /// </summary>
    public sealed class LoaderService
    {
        private readonly VersionCatalogService _catalog = AppServices.Catalog;

        private static bool UseMirror => AppServices.Settings.Settings.UseBmclApiMirror;
        private static string ForgeMaven => UseMirror
            ? "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge"
            : "https://maven.minecraftforge.net/net/minecraftforge/forge";
        private static string NeoForgeMaven => "https://maven.neoforged.net/releases/net/neoforged/neoforge";

        // ---------------- Fabric ----------------

        public async Task<List<string>> GetFabricVersionsAsync(string gameVersion, CancellationToken ct = default)
        {
            var url = $"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}";
            var list = await Http.GetJsonAsync<List<FabricLoaderEntry>>(url, ct) ?? new();
            return list.Select(x => x.Loader?.Version).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
        }

        internal sealed class FabricLoaderEntry
        {
            public FabricVersion? Loader { get; set; }
            public FabricVersion? Intermediary { get; set; }
        }

        internal sealed class FabricVersion
        {
            public string? Version { get; set; }
            public bool? Stable { get; set; }
        }

        internal sealed class FabricMeta
        {
            public FabricLauncherMeta? LauncherMeta { get; set; }
        }

        internal sealed class FabricLauncherMeta
        {
            public FabricLibraries? Libraries { get; set; }
            public Dictionary<string, string>? MainClass { get; set; }
        }

        internal sealed class FabricLibraries
        {
            public List<RawLibrary>? Common { get; set; }
            public List<RawLibrary>? Client { get; set; }
            public List<RawLibrary>? Server { get; set; }
        }

        // ---------------- Quilt ----------------

        public async Task<List<string>> GetQuiltVersionsAsync(string gameVersion, CancellationToken ct = default)
        {
            var url = $"https://meta.quiltmc.org/v3/versions/loader/{gameVersion}";
            var list = await Http.GetJsonAsync<List<QuiltLoaderEntry>>(url, ct) ?? new();
            return list.Select(x => x.Loader?.Version ?? x.Version).Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!).Distinct().ToList();
        }

        internal sealed class QuiltLoaderEntry
        {
            public string? Version { get; set; }
            public FabricVersion? Loader { get; set; }
            public FabricLauncherMeta? LauncherMeta { get; set; }
        }

        // ---------------- Forge / NeoForge ----------------

        public async Task<List<string>> GetForgeVersionsAsync(string? gameVersion = null, CancellationToken ct = default)
            => await GetMavenVersionsAsync(ForgeMaven + "/maven-metadata.xml", gameVersion, ct);

        public async Task<List<string>> GetNeoForgeVersionsAsync(string? gameVersion = null, CancellationToken ct = default)
            => await GetMavenVersionsAsync(NeoForgeMaven + "/maven-metadata.xml", gameVersion, ct);

        private async Task<List<string>> GetMavenVersionsAsync(string metadataUrl, string? gameVersion, CancellationToken ct)
        {
            try
            {
                var xml = await Http.GetStringAsync(metadataUrl, ct);
                var doc = XDocument.Parse(xml);
                var versions = doc.Descendants("version")
                    .Select(v => v.Value.Trim())
                    .Where(v => string.IsNullOrEmpty(gameVersion) || v.StartsWith(gameVersion + "-", StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToList();
                versions.Reverse();
                return versions;
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取加载器版本列表失败 {metadataUrl}：{ex.Message}");
                return new List<string>();
            }
        }

        // ---------------- 安装 ----------------

        /// <summary>
        /// 安装加载器，返回可启动的版本 ID。
        /// </summary>
        public async Task<string> InstallAsync(string gameVersion, LoaderType loader, string? loaderVersion,
            IProgress<InstallProgress>? progress = null, CancellationToken ct = default,
            string? groupId = null, string? groupName = null)
        {
            // 确保原版元数据存在
            await _catalog.LoadRawAsync(gameVersion, true, ct);

            return loader switch
            {
                LoaderType.Fabric => await InstallFabricAsync(gameVersion, loaderVersion, progress, ct),
                LoaderType.Quilt => await InstallQuiltAsync(gameVersion, loaderVersion, progress, ct),
                LoaderType.Forge => await InstallForgeAsync(gameVersion, loaderVersion, false, progress, ct, groupId, groupName),
                LoaderType.NeoForge => await InstallForgeAsync(gameVersion, loaderVersion, true, progress, ct, groupId, groupName),
                _ => gameVersion
            };
        }

        private async Task<string> InstallFabricAsync(string gameVersion, string? loaderVersion,
            IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new InstallProgress { Phase = "获取 Fabric 元数据", Percent = 5 });
            if (string.IsNullOrEmpty(loaderVersion))
            {
                var versions = await GetFabricVersionsAsync(gameVersion, ct);
                loaderVersion = versions.FirstOrDefault();
            }
            if (string.IsNullOrEmpty(loaderVersion)) throw new IOException("未找到可用的 Fabric Loader 版本");

            var versionId = $"fabric-loader-{loaderVersion}-{gameVersion}";
            var metaUrl = $"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}/{loaderVersion}";
            var meta = await Http.GetJsonAsync<FabricMeta>(metaUrl, ct)
                       ?? throw new IOException("Fabric 元数据解析失败");

            var libs = new List<RawLibrary>();
            if (meta.LauncherMeta?.Libraries?.Common is { } common) libs.AddRange(common);
            if (meta.LauncherMeta?.Libraries?.Client is { } clientLibs) libs.AddRange(clientLibs);

            var mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient";
            if (meta.LauncherMeta?.MainClass?.TryGetValue("client", out var mc) == true && !string.IsNullOrEmpty(mc))
                mainClass = mc;

            var raw = new RawVersionJson
            {
                Id = versionId,
                InheritsFrom = gameVersion,
                MainClass = mainClass,
                Libraries = libs,
                Type = "release"
            };
            _catalog.SaveRaw(raw, versionId);
            Logger.Info($"Fabric 版本已生成：{versionId}");
            progress?.Report(new InstallProgress { Phase = "Fabric 元数据就绪", Percent = 20 });
            return versionId;
        }

        private async Task<string> InstallQuiltAsync(string gameVersion, string? loaderVersion,
            IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new InstallProgress { Phase = "获取 Quilt 元数据", Percent = 5 });
            if (string.IsNullOrEmpty(loaderVersion))
            {
                var versions = await GetQuiltVersionsAsync(gameVersion, ct);
                loaderVersion = versions.FirstOrDefault();
            }
            if (string.IsNullOrEmpty(loaderVersion)) throw new IOException("未找到可用的 Quilt Loader 版本");

            var versionId = $"quilt-loader-{loaderVersion}-{gameVersion}";

            // Quilt Loader 以 Maven 产物形式提供，直接构造继承原版的版本描述
            var libs = new List<RawLibrary>
            {
                new()
                {
                    Name = $"org.quiltmc:quilt-loader:{loaderVersion}",
                    Url = "https://maven.quiltmc.org/repository/release/"
                }
            };

            var raw = new RawVersionJson
            {
                Id = versionId,
                InheritsFrom = gameVersion,
                MainClass = "org.quiltmc.loader.impl.launch.knot.KnotClient",
                Libraries = libs,
                Type = "release"
            };
            _catalog.SaveRaw(raw, versionId);
            Logger.Info($"Quilt 版本已生成：{versionId}");
            progress?.Report(new InstallProgress { Phase = "Quilt 元数据就绪", Percent = 20 });
            return versionId;
        }

        private async Task<string> InstallForgeAsync(string gameVersion, string? loaderVersion, bool neoForge,
            IProgress<InstallProgress>? progress, CancellationToken ct, string? groupId = null, string? groupName = null)
        {
            progress?.Report(new InstallProgress { Phase = "解析安装器版本", Percent = 5 });
            if (string.IsNullOrEmpty(loaderVersion))
            {
                var versions = neoForge
                    ? await GetNeoForgeVersionsAsync(gameVersion, ct)
                    : await GetForgeVersionsAsync(gameVersion, ct);
                loaderVersion = versions.FirstOrDefault();
            }
            if (string.IsNullOrEmpty(loaderVersion)) throw new IOException("未找到可用的安装器版本");

            var maven = neoForge ? NeoForgeMaven : ForgeMaven;
            var prefix = neoForge ? "neoforge" : "forge";
            var installerUrl = $"{maven}/{loaderVersion}/{prefix}-{loaderVersion}-installer.jar";
            var installerPath = Path.Combine(AppPaths.TempRoot, $"{prefix}-{loaderVersion}-installer.jar");
            Directory.CreateDirectory(AppPaths.TempRoot);

            progress?.Report(new InstallProgress { Phase = "下载安装器", Percent = 10 });
            var item = AppServices.Downloads.Enqueue(installerUrl, installerPath,
                groupId: groupId, groupName: groupName,
                fileName: $"{prefix}-{loaderVersion}-installer.jar");
            if (progress is not null)
            {
                _ = Task.Run(async () =>
                {
                    while (item.IsRunning)
                    {
                        progress.Report(new InstallProgress { Phase = "下载安装器", Percent = 10 + item.Progress * 0.4 });
                        await Task.Delay(300, CancellationToken.None);
                    }
                }, CancellationToken.None);
            }
            if (!await AppServices.Downloads.WaitAsync(item))
                throw new IOException("安装器下载失败：" + item.ErrorMessage);

            var java = await AppServices.Java.SelectJavaForVersionAsync(gameVersion, ct)
                       ?? throw new IOException("未找到可用的 Java 运行时，无法执行安装器");

            progress?.Report(new InstallProgress { Phase = "执行安装器（可能需要数十秒）", Percent = 55 });
            var result = await RunInstallerAsync(java, installerPath, ct);
            if (!result.Success) throw new IOException("安装器执行失败：" + result.Output);

            var versionId = FindInstalledVersionId(gameVersion, prefix, loaderVersion) ?? loaderVersion;
            Logger.Info($"{prefix} 安装完成：{versionId}");
            progress?.Report(new InstallProgress { Phase = "安装器执行完成", Percent = 70 });
            return versionId;
        }

        private static async Task<(bool Success, string Output)> RunInstallerAsync(JavaInfo java, string installerPath,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo(java.Path, $"-jar \"{installerPath}\" --installClient \"{AppPaths.Root}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppPaths.Root
            };
            using var process = Process.Start(psi) ?? throw new IOException("无法启动 Java 进程");
            var output = await process.StandardOutput.ReadToEndAsync(ct) + await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            Logger.Info("安装器输出：" + output.Ellipsis(2000));
            return (process.ExitCode == 0, output);
        }

        private static string? FindInstalledVersionId(string gameVersion, string prefix, string mavenVersion)
        {
            if (!Directory.Exists(AppPaths.VersionsRoot)) return null;
            var candidates = Directory.GetDirectories(AppPaths.VersionsRoot)
                .Select(Path.GetFileName)
                .Where(name => name is not null &&
                               name!.Contains(gameVersion, StringComparison.OrdinalIgnoreCase) &&
                               name!.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0) return null;
            return candidates.FirstOrDefault(c => c!.Contains(mavenVersion.Split('-').Last(), StringComparison.OrdinalIgnoreCase))
                   ?? candidates.First();
        }
    }
}
