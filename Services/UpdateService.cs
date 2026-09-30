using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// 启动器自动更新：基于文件哈希的增量更新（仅下载发生变化的文件）。
    /// </summary>
    public sealed class UpdateService
    {
        public string CurrentVersion { get; } = GetCurrentVersion();

        private static string GetCurrentVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(info)) return info.Split('+')[0];
                return assembly.GetName().Version?.ToString() ?? "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }

        /// <summary>
        /// 检查更新：与本地文件 SHA256 对比，得到需要更新的文件清单。
        /// </summary>
        public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        {
            // 地址内置在程序中，不再读取用户配置
            var url = AppConstants.UpdateManifestUrl;
            var result = new UpdateCheckResult { CurrentVersion = CurrentVersion };
            if (string.IsNullOrWhiteSpace(url))
            {
                result.Error = "更新清单地址不可用";
                return result;
            }

            try
            {
                var manifest = await Http.GetJsonAsync<UpdateManifest>(url!, ct)
                               ?? throw new IOException("更新清单解析失败");
                result.LatestVersion = manifest.Version;
                result.Notes = manifest.Notes;
                result.Mandatory = manifest.Mandatory;
                result.DownloadPageUrl = manifest.DownloadPageUrl;

                var root = AppContext.BaseDirectory;
                foreach (var file in manifest.Files)
                {
                    var local = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(local))
                    {
                        result.FilesToUpdate.Add(file);
                        result.TotalBytes += file.Size;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(file.Sha256))
                    {
                        result.FilesToUpdate.Add(file);
                        result.TotalBytes += file.Size;
                        continue;
                    }
                    var hash = await local.Sha256Async(ct);
                    if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        result.FilesToUpdate.Add(file);
                        result.TotalBytes += file.Size;
                    }
                }

                result.FileCount = result.FilesToUpdate.Count;
                result.HasUpdate = result.FilesToUpdate.Count > 0 &&
                                   !string.Equals(manifest.Version, CurrentVersion, StringComparison.OrdinalIgnoreCase);
                AppServices.Settings.Settings.LastUpdateCheck = DateTime.Now;
                AppServices.Settings.Save();
                Logger.Info($"更新检查完成：最新 {manifest.Version}，需更新 {result.FileCount} 个文件");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn("检查更新失败：" + ex.Message);
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 应用增量更新（下载到临时目录 → 校验 → 备份 → 替换）。
        /// </summary>
        public async Task<bool> ApplyAsync(UpdateCheckResult update, IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            if (update.FilesToUpdate.Count == 0) return true;

            var root = AppContext.BaseDirectory;
            var staging = Path.Combine(AppPaths.PatchRoot, "staging-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(staging);

            var requests = new List<DownloadRequest>();
            foreach (var file in update.FilesToUpdate)
            {
                var url = !string.IsNullOrEmpty(file.PatchUrl) ? file.PatchUrl! : file.Url;
                requests.Add(new DownloadRequest
                {
                    Url = url,
                    Destination = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)),
                    Size = file.PatchSize ?? file.Size,
                    FileName = Path.GetFileName(file.Path)
                });
            }

            var batch = await AppServices.Downloads.DownloadAllAsync(requests, "启动器更新",
                new Progress<DownloadProgress>(p => progress?.Report(p.Percent)), ct);

            if (!batch.Success)
            {
                Logger.Error("更新文件下载失败：" + string.Join("；", batch.Errors.Take(3)));
                return false;
            }

            // 校验哈希
            foreach (var file in update.FilesToUpdate)
            {
                if (string.IsNullOrWhiteSpace(file.Sha256)) continue;
                var staged = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(staged)) continue;
                var hash = await staged.Sha256Async(ct);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error($"更新文件校验失败：{file.Path}");
                    return false;
                }
            }

            Directory.CreateDirectory(Path.Combine(AppPaths.PatchRoot, "backup"));

            // 备份并替换
            int index = 0;
            foreach (var file in update.FilesToUpdate)
            {
                var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
                var target = Path.Combine(root, relative);
                var source = Path.Combine(staging, relative);
                if (!File.Exists(source)) continue;
                try
                {
                    if (File.Exists(target))
                    {
                        var backup = Path.Combine(AppPaths.PatchRoot, "backup", relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(target, backup, true);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, true);
                }
                catch (Exception ex)
                {
                    Logger.Error($"替换文件失败 {relative}：{ex.Message}");
                    return false;
                }
                progress?.Report(++index / (double)update.FilesToUpdate.Count * 100);
            }

            staging.DeleteDirectorySafe();
            Logger.Info($"更新完成，共替换 {update.FilesToUpdate.Count} 个文件");
            return true;
        }

        /// <summary>
        /// 重启应用以完成更新。
        /// </summary>
        public static void Restart()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true
                });
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Logger.Error("重启失败", ex);
            }
        }
    }
}
