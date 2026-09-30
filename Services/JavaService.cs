using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// Java 运行时自动检测、版本识别与自动部署。
    /// </summary>
    public sealed class JavaService
    {
        private readonly List<JavaInfo> _cache = new();
        private bool _scanned;

        private static readonly string[] RelativeJavaPaths =
        {
            "bin\\javaw.exe", "bin\\java.exe", "javaw.exe", "java.exe"
        };

        public IReadOnlyList<JavaInfo> Detected => _cache;

        /// <summary>
        /// 扫描系统中已安装的 Java：
        /// 手动指定路径 + 注册表 + JAVA_HOME + 常见安装目录 + PATH + 启动器自带运行时。
        /// </summary>
        public async Task<List<JavaInfo>> ScanAsync(bool force = false, CancellationToken ct = default)
        {
            if (_scanned && !force) return _cache;
            _cache.Clear();

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 用户手动指定的路径优先级最高（自动扫描失效时的兜底）
            try
            {
                foreach (var manual in AppServices.Settings.Settings.ManualJavaPaths)
                {
                    if (!string.IsNullOrWhiteSpace(manual)) candidates.Add(manual.Trim());
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取手动 Java 路径失败：" + ex.Message);
            }

            // 2) 注册表
            foreach (var path in EnumerateRegistryJavas()) candidates.Add(path);

            // 3) JAVA_HOME
            var envJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(envJavaHome)) candidates.Add(envJavaHome);

            // 4) 常见安装目录（递归 4 层，覆盖官方启动器 runtime 的深层结构）
            var scannedDirs = 0;
            foreach (var dir in JavaRequirement.SearchDirectories())
            {
                if (!Directory.Exists(dir)) continue;
                scannedDirs++;
                foreach (var found in EnumerateJavaInDirectory(dir, 4)) candidates.Add(found);
            }

            // 5) PATH 中的 java
            try
            {
                var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var p in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var entry = p.Trim('"').Trim();
                    if (entry.Length == 0) continue;
                    var javaExe = Path.Combine(entry, "java.exe");
                    if (File.Exists(javaExe)) candidates.Add(javaExe);
                }
            }
            catch { /* 忽略 */ }

            // 6) 用户目录下的 Java
            foreach (var userJava in EnumerateUserJavas()) candidates.Add(userJava);

            Logger.Info($"Java 扫描开始：候选 {candidates.Count} 个，有效目录 {scannedDirs} 个");

            // 并行探测（受限并发），避免逐个启动进程导致界面长时间无响应
            var results = new List<JavaInfo>();
            var gate = new SemaphoreSlim(4);
            var tasks = candidates.Select(async candidate =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var info = await ProbeAsync(candidate, ct);
                    if (info is null)
                    {
                        Logger.Debug($"Java 候选不可用：{candidate}");
                        return null;
                    }
                    Logger.Info($"发现 Java：{info.Version}（{info.Architecture}）→ {info.Path}");
                    return info;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"探测 Java 失败 {candidate}：{ex.Message}");
                    return null;
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            foreach (var info in await Task.WhenAll(tasks))
            {
                if (info is null) continue;
                if (results.Any(r => string.Equals(r.Path, info.Path, StringComparison.OrdinalIgnoreCase))) continue;
                results.Add(info);
            }

            foreach (var info in results) info.IsRecommended = false;
            var best = results.Where(j => j.Is64Bit).OrderByDescending(j => j.MajorVersion).FirstOrDefault()
                       ?? results.OrderByDescending(j => j.MajorVersion).FirstOrDefault();
            if (best is not null) best.IsRecommended = true;

            _cache.Clear();
            _cache.AddRange(results.OrderByDescending(j => j.MajorVersion).ThenBy(j => j.Path));
            _scanned = true;
            Logger.Info($"Java 扫描完成，共发现 {_cache.Count} 个可用运行时");
            return _cache;
        }

        /// <summary>
        /// 手动添加一个 Java（java.exe 文件路径或 Java 安装目录）。
        /// </summary>
        public async Task<JavaInfo?> AddManualJavaAsync(string path, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var normalized = path.Trim().Trim('"');
            // 用户可能选择了 javaw.exe
            if (normalized.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase))
            {
                var sibling = Path.Combine(Path.GetDirectoryName(normalized)!, "java.exe");
                if (File.Exists(sibling)) normalized = sibling;
            }

            var info = await ProbeAsync(normalized, ct);
            if (info is null)
            {
                // 若选择的是目录但结构特殊，再尝试其 bin 子目录
                if (Directory.Exists(normalized))
                {
                    info = await ProbeAsync(Path.Combine(normalized, "bin", "java.exe"), ct);
                }
                if (info is null) return null;
            }

            var settings = AppServices.Settings.Settings;
            if (!settings.ManualJavaPaths.Any(p => string.Equals(p, info.Path, StringComparison.OrdinalIgnoreCase)))
            {
                settings.ManualJavaPaths.Add(info.Path);
                AppServices.Settings.Save();
            }

            info.Source = "手动添加";
            _cache.RemoveAll(j => string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase));
            _cache.Add(info);
            Logger.Info($"已手动添加 Java：{info.Path}");
            return info;
        }

        /// <summary>
        /// 移除手动添加的 Java。
        /// </summary>
        public void RemoveManualJava(string path)
        {
            var settings = AppServices.Settings.Settings;
            settings.ManualJavaPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            AppServices.Settings.Save();
            _cache.RemoveAll(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
            Logger.Info($"已移除手动 Java：{path}");
        }

        private static IEnumerable<string> EnumerateUserJavas()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dirs = new List<string>();
            try
            {
                dirs.Add(Path.Combine(local, "Programs"));
                dirs.Add(Path.Combine(local, "Java"));
                dirs.Add(AppPaths.JavaRoot);
                dirs.Add(AppPaths.RuntimeRoot);
            }
            catch { }
            foreach (var d in dirs.Where(Directory.Exists))
            {
                foreach (var f in EnumerateJavaInDirectory(d, 4)) yield return f;
            }
        }

        private static IEnumerable<string> EnumerateJavaInDirectory(string root, int maxDepth)
        {
            var found = new List<string>();
            try
            {
                // 直接命中：root/bin/java.exe 或 root/java.exe
                foreach (var rel in RelativeJavaPaths)
                {
                    var full = Path.Combine(root, rel);
                    if (File.Exists(full)) found.Add(full);
                }

                // 向下搜索一层到 maxDepth 层，寻找含 bin\java.exe 的目录
                var stack = new Stack<(string Dir, int Depth)>();
                stack.Push((root, 0));
                while (stack.Count > 0)
                {
                    var (dir, depth) = stack.Pop();
                    if (depth >= maxDepth) continue;
                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            var name = Path.GetFileName(sub);
                            if (name is "bin" or "jre" or "temp" or "logs") continue;
                            var javaExe = Path.Combine(sub, "bin", "java.exe");
                            if (File.Exists(javaExe)) found.Add(javaExe);
                            stack.Push((sub, depth + 1));
                        }
                    }
                    catch { /* 无权限目录跳过 */ }
                }
            }
            catch { }
            return found;
        }

        private static IEnumerable<string> EnumerateRegistryJavas()
        {
            var list = new List<string>();
            try
            {
                using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft");
                if (baseKey is not null)
                {
                    foreach (var subName in new[] { "JDK", "JRE", "Java Runtime Environment", "Java Development Kit" })
                    {
                        using var sub = baseKey.OpenSubKey(subName);
                        if (sub is null) continue;
                        foreach (var version in sub.GetSubKeyNames())
                        {
                            using var vKey = sub.OpenSubKey(version);
                            var home = vKey?.GetValue("JavaHome") as string;
                            if (!string.IsNullOrEmpty(home)) list.Add(home);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("注册表 Java 检测失败：" + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// 探测单个 Java 可执行文件的信息。
        /// </summary>
        public async Task<JavaInfo?> ProbeAsync(string candidate, CancellationToken ct = default)
        {
            try
            {
                string javaExe;
                if (Directory.Exists(candidate))
                {
                    var direct = Path.Combine(candidate, "bin", "java.exe");
                    javaExe = File.Exists(direct) ? direct : Path.Combine(candidate, "java.exe");
                }
                else
                {
                    javaExe = candidate;
                    if (!javaExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
                    if (!javaExe.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase)) return null;
                }
                if (!File.Exists(javaExe)) return null;

                var dir = Path.GetDirectoryName(javaExe)!;
                var javaw = Path.Combine(dir, "javaw.exe");
                var info = new JavaInfo
                {
                    Path = javaExe,
                    WPath = File.Exists(javaw) ? javaw : javaExe,
                    Exists = true,
                    Source = InferSource(javaExe)
                };

                var output = await RunAsync(javaExe, "-version", ct);
                ParseVersion(output, info);
                if (info.MajorVersion == 0)
                {
                    // 兜底：读取 release 文件
                    var release = Path.Combine(Path.GetDirectoryName(dir)!, "release");
                    if (File.Exists(release))
                    {
                        foreach (var line in File.ReadAllLines(release))
                        {
                            if (line.StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase))
                                ParseVersion(line, info);
                            if (line.StartsWith("IMPLEMENTOR=", StringComparison.OrdinalIgnoreCase))
                                info.Vendor = line.Split('=')[1].Trim('"');
                        }
                    }
                }
                return info.MajorVersion == 0 ? null : info;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Java 探测失败 {candidate}：{ex.Message}");
                return null;
            }
        }

        private static string InferSource(string path)
        {
            try
            {
                if (AppServices.Settings.Settings.ManualJavaPaths.Any(p =>
                        string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                    return "手动添加";
            }
            catch { }
            if (path.Contains("Minecraft Launcher", StringComparison.OrdinalIgnoreCase)) return "MC 官方启动器";
            if (path.StartsWith(AppPaths.JavaRoot, StringComparison.OrdinalIgnoreCase)) return "启动器自带";
            if (path.Contains("Eclipse Adoptium", StringComparison.OrdinalIgnoreCase)) return "Adoptium";
            if (path.Contains("jdk", StringComparison.OrdinalIgnoreCase)) return "JDK";
            return "";
        }

        private static void ParseVersion(string output, JavaInfo info)
        {
            var match = Regex.Match(output, "\"?(?<v>1\\.\\d+\\.\\d+[\\w_\\-\\.]*|\\d+\\.\\d+\\.\\d+[\\w_\\-\\.]*|\\d+)\\s?");
            if (match.Success) info.Version = match.Groups["v"].Value;
            info.Is64Bit = output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("amd64", StringComparison.OrdinalIgnoreCase);

            var major = 0;
            var v = info.Version;
            if (!string.IsNullOrEmpty(v))
            {
                if (v.StartsWith("1."))
                {
                    var parts = v.Split('.');
                    int.TryParse(parts[1], out major);
                }
                else
                {
                    var parts = v.Split('.');
                    int.TryParse(parts[0], out major);
                }
            }
            info.MajorVersion = major;

            if (string.IsNullOrEmpty(info.Vendor))
            {
                if (output.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase)) info.Vendor = "OpenJDK";
                else if (output.Contains("HotSpot", StringComparison.OrdinalIgnoreCase)) info.Vendor = "HotSpot";
                else if (output.Contains("Oracle", StringComparison.OrdinalIgnoreCase)) info.Vendor = "Oracle";
            }
        }

        private static async Task<string> RunAsync(string exe, string args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return "";
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct);
            return (await stdout) + "\n" + (await stderr);
        }

        /// <summary>
        /// 依据版本需求选择最合适的 Java。
        /// 注意：用户指定的 Java 也必须满足游戏的最低版本要求，否则会自动改用更高版本，
        /// 否则会因参数不兼容（如 Java 8 不支持 --add-opens）导致 JVM 无法创建。
        /// </summary>
        public JavaInfo? SelectBest(int requiredMajor, string? preferredPath = null)
        {
            if (!string.IsNullOrEmpty(preferredPath))
            {
                var preferred = FindByPath(preferredPath);
                if (preferred is not null)
                {
                    if (preferred.MajorVersion >= requiredMajor) return preferred;
                    Logger.Warn($"指定的 Java {preferred.MajorVersion} 低于游戏要求的 Java {requiredMajor}，" +
                                "已自动改用更高版本的 Java");
                }
            }

            var candidates = _cache.Where(j => j.Is64Bit && j.MajorVersion >= requiredMajor)
                .OrderBy(j => j.MajorVersion).ToList();
            if (candidates.Count > 0) return candidates[0];

            // 没有满足要求的运行时：返回现有最高版本，由调用方给出明确的安装提示
            return _cache.Where(j => j.Is64Bit).OrderByDescending(j => j.MajorVersion).FirstOrDefault()
                   ?? _cache.OrderByDescending(j => j.MajorVersion).FirstOrDefault();
        }

        /// <summary>按路径查找已缓存的 Java。</summary>
        public JavaInfo? FindByPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return _cache.FirstOrDefault(j =>
                string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.WPath, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 依据游戏版本需求自动挑选（必要时先扫描）可用的 Java。
        /// </summary>
        public async Task<JavaInfo?> SelectJavaForVersionAsync(string gameVersion, CancellationToken ct = default)
        {
            await ScanAsync(false, ct);
            var required = JavaRequirement.RecommendFor(gameVersion);
            return SelectBest(required, AppServices.Settings.Settings.JavaPath);
        }

        /// <summary>
        /// 自动下载并部署指定主版本的 JRE（Eclipse Temurin，失败时回退国内镜像）。
        /// </summary>
        public async Task<JavaInfo?> InstallJavaAsync(int majorVersion, IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            var targetDir = Path.Combine(AppPaths.JavaRoot, $"jre-{majorVersion}");
            var javaExe = Path.Combine(targetDir, "bin", "java.exe");
            if (File.Exists(javaExe))
            {
                var existing = await ProbeAsync(javaExe, ct);
                if (existing is not null)
                {
                    Logger.Info($"Java {majorVersion} 已存在，跳过安装：{javaExe}");
                    progress?.Report(1);
                    _cache.RemoveAll(j => j.Path == existing.Path);
                    _cache.Add(existing);
                    return existing;
                }
            }

            Directory.CreateDirectory(AppPaths.TempRoot);
            var zipPath = Path.Combine(AppPaths.TempRoot, $"jre-{majorVersion}.zip");
            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { /* 残留文件忽略 */ }
            }

            progress?.Report(0.02);
            var sources = await BuildJavaSourcesAsync(majorVersion, ct);
            var errors = new List<string>();
            DownloadItem? downloaded = null;

            foreach (var (name, url) in sources)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Logger.Info($"开始下载 Java {majorVersion}（{name}）：{url}");
                    var item = AppServices.Downloads.Enqueue(url, zipPath,
                        groupId: $"java-{majorVersion}", groupName: $"Java {majorVersion} 运行时",
                        fileName: $"Java {majorVersion} 运行时（{name}）");
                    // 镜像对 Range 支持参差不齐，大文件统一走单流 + 断点续传
                    item.AllowChunking = false;

                    if (progress is not null)
                    {
                        _ = Task.Run(async () =>
                        {
                            while (item.IsRunning)
                            {
                                progress.Report(item.TotalBytes > 0
                                    ? 0.05 + item.Progress / 100d * 0.65
                                    : 0.15);
                                await Task.Delay(400, CancellationToken.None);
                            }
                        }, CancellationToken.None);
                    }

                    if (await AppServices.Downloads.WaitAsync(item))
                    {
                        downloaded = item;
                        break;
                    }
                    errors.Add($"{name}：{item.ErrorMessage ?? "下载中断"}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{name}：{ex.Message}");
                    Logger.Warn($"Java 运行时下载源 {name} 失败：{ex.Message}");
                }
            }

            if (downloaded is null)
            {
                var detail = errors.Count == 0 ? "没有可用的下载源" : string.Join("；", errors);
                throw new IOException($"Java {majorVersion} 运行时下载失败（{detail}）");
            }

            progress?.Report(0.72);
            var extractDir = Path.Combine(AppPaths.TempRoot, "jre-extract-" + Guid.NewGuid().ToString("N")[..8]);
            extractDir.DeleteDirectorySafe();
            Directory.CreateDirectory(extractDir);

            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir, true), ct);
            }
            catch (InvalidDataException ex)
            {
                throw new IOException("Java 压缩包已损坏，请重试（可能是下载中断导致）", ex);
            }

            var root = Directory.GetDirectories(extractDir).FirstOrDefault();
            if (root is null) throw new IOException("Java 压缩包结构异常，未找到解压根目录");

            if (Directory.Exists(targetDir)) targetDir.DeleteDirectorySafe();
            Directory.CreateDirectory(AppPaths.JavaRoot);
            Directory.Move(root, targetDir);
            extractDir.DeleteDirectorySafe();
            try { File.Delete(zipPath); } catch { /* 清理失败忽略 */ }

            progress?.Report(0.9);
            var info = await ProbeAsync(javaExe, ct);
            if (info is null)
            {
                // 探测失败不视为安装失败：文件已就位，仍登记以便用户手动使用
                var javaw = Path.Combine(targetDir, "bin", "javaw.exe");
                info = new JavaInfo
                {
                    Path = javaExe,
                    WPath = File.Exists(javaw) ? javaw : javaExe,
                    Version = majorVersion.ToString(),
                    MajorVersion = majorVersion,
                    Is64Bit = true,
                    Exists = true,
                    Source = "启动器自带"
                };
                Logger.Warn($"Java {majorVersion} 已部署但未能识别版本信息：{javaExe}");
            }

            _cache.RemoveAll(j => j.Path == info.Path);
            _cache.Add(info);
            progress?.Report(1);
            Logger.Info($"Java {majorVersion} 部署完成：{info.Path}（{info.Version}）");
            return info;
        }

        /// <summary>
        /// 构建 Java 运行时下载源列表：官方 Adoptium 优先，失败时回退国内镜像。
        /// </summary>
        private static async Task<List<(string Name, string Url)>> BuildJavaSourcesAsync(int majorVersion,
            CancellationToken ct)
        {
            var sources = new List<(string, string)>
            {
                ("Adoptium 官方",
                    $"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/windows/x64/jre/hotspot/normal/eclipse")
            };

            try
            {
                var index = $"https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{majorVersion}/jre/x64/windows/";
                var html = await Http.GetStringAsync(index, ct);
                var matches = Regex.Matches(html,
                    "href=\"(?<file>OpenJDK[^\"]*jre_x64_windows_hotspot_[^\"]+\\.zip)\"",
                    RegexOptions.IgnoreCase);
                if (matches.Count > 0)
                {
                    var file = matches[matches.Count - 1].Groups["file"].Value;
                    sources.Add(("清华镜像", index + file));
                    Logger.Info($"已获取 Java 镜像文件：{file}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("获取 Java 镜像列表失败：" + ex.Message);
            }

            return sources;
        }
    }
}
