using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    public sealed class LaunchResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string LogPath { get; set; } = "";
        public string CommandPreview { get; set; } = "";
    }

    /// <summary>
    /// 游戏启动：参数构建、进程管理与日志采集。
    /// </summary>
    public sealed class LaunchService
    {
        private Process? _process;
        private DateTime _startTime;
        private GameInstance? _currentInstance;
        private StreamWriter? _logWriter;

        public bool IsRunning => _process is { HasExited: false };

        public event Action<string>? OutputReceived;

        /// <summary>游戏退出事件，参数为退出码。</summary>
        public event Action<int>? GameExited;

        /// <summary>
        /// 游戏主窗口出现（真正进入游戏）时触发。
        /// 进程创建到窗口出现之间游戏仍在加载资源，界面应显示「启动中」而非「运行中」。
        /// </summary>
        public event Action? GameStarted;

        /// <summary>等待游戏窗口出现的最长时间（秒），超时后按已启动处理。</summary>
        private const int WindowWaitTimeoutSeconds = 90;

        /// <summary>
        /// 构建完整的启动命令行（供预览与调试）。
        /// </summary>
        public LaunchPlan BuildPlan(GameVersionSpec spec, GameInstance instance, Account account, JavaInfo java)
        {
            var versionId = instance.ResolvedVersionId ?? spec.Id;
            var versionDir = AppPaths.VersionDir(versionId);
            var nativesDir = Path.Combine(versionDir, "natives");
            Directory.CreateDirectory(nativesDir);

            var clientJar = File.Exists(Path.Combine(versionDir, versionId + ".jar"))
                ? Path.Combine(versionDir, versionId + ".jar")
                : spec.ClientJar.Path;

            var classpathEntries = spec.Libraries
                .Where(l => !l.IsNative)
                .Select(l => Path.Combine(AppPaths.LibrariesRoot, l.Path.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();
            classpathEntries.Add(clientJar);
            var classpath = string.Join(Path.PathSeparator, classpathEntries);

            var assetsRoot = spec.UseVirtualAssets && !string.IsNullOrEmpty(spec.LegacyAssetsId)
                ? Path.Combine(AppPaths.AssetsRoot, "virtual", spec.LegacyAssetsId!)
                : AppPaths.AssetsRoot;
            if (spec.MapToResources) assetsRoot = AppPaths.ResourcesRoot;

            var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["auth_player_name"] = account.PlayerName,
                ["auth_uuid"] = account.Uuid,
                ["auth_access_token"] = account.AccessToken,
                ["user_type"] = account.Type == AccountType.Microsoft ? "msa" : "legacy",
                ["user_properties"] = "{}",
                ["auth_xuid"] = "0",
                ["auth_session"] = account.AccessToken,
                ["clientid"] = "",
                ["version_name"] = spec.Id,
                ["version_type"] = "release",
                ["game_directory"] = instance.Directory,
                ["assets_root"] = assetsRoot,
                ["assets_index_name"] = spec.AssetIndex?.Id ?? spec.LegacyAssetsId ?? "",
                ["resolution_width"] = instance.WindowWidth.ToString(),
                ["resolution_height"] = instance.WindowHeight.ToString(),
                ["natives_directory"] = nativesDir,
                ["launcher_name"] = AppConstants.LauncherShortName,
                ["launcher_version"] = "1.0.0",
                ["classpath"] = classpath,
                ["classpath_separator"] = Path.PathSeparator.ToString(),
                ["library_directory"] = AppPaths.LibrariesRoot,
                ["primary_jar"] = clientJar
            };

            var context = new LaunchContext(instance.WindowMode != WindowMode.Default, false);

            var args = new List<string>();

            // 内存（做一次保护性收敛，避免出现非法的 -Xms/-Xmx 组合导致 JVM 无法创建）
            var minMemory = Math.Clamp(instance.MinMemoryMb, 256, 32768);
            var maxMemory = Math.Clamp(instance.MaxMemoryMb, 512, 32768);
            if (maxMemory < minMemory) maxMemory = minMemory;
            args.Add($"-Xms{minMemory}M");
            args.Add($"-Xmx{maxMemory}M");

            // 稳定性与安全加固
            args.AddRange(new[]
            {
                "-XX:+UseG1GC",
                "-XX:-UseAdaptiveSizePolicy",
                "-XX:-OmitStackTraceInFastThrow",
                "-Dfile.encoding=UTF-8",
                "-Dlog4j2.formatMsgNoLookups=true",
                "-Dminecraft.launcher.brand=" + AppConstants.LauncherShortName,
                "-Dminecraft.launcher.version=1.0.0",
                "-Dfml.ignoreInvalidMinecraftCertificates=true",
                "-Dfml.ignorePatchDiscrepancies=true",
                $"-Djava.library.path={nativesDir}",
                $"-Djna.tmpdir={nativesDir}",
                $"-Dorg.lwjgl.system.SharedLibraryExtractPath={Path.Combine(nativesDir, "lwjgl")}",
                $"-Dio.netty.native.workdir={nativesDir}"
            });

            // 版本自带的 JVM 参数（1.13+ 含有模块开放等必要参数）
            var hasClasspathArg = false;
            foreach (var arg in spec.JvmArguments)
            {
                if (!MatchesRules(arg, context)) continue;
                foreach (var value in arg.Values)
                {
                    if (value.Contains("${classpath}", StringComparison.OrdinalIgnoreCase)) hasClasspathArg = true;
                    var expanded = Expand(value, tokens);
                    if (!string.IsNullOrWhiteSpace(expanded)) args.Add(expanded);
                }
            }

            // 用户自定义 JVM 参数
            if (!string.IsNullOrWhiteSpace(instance.JvmArguments))
            {
                foreach (var part in SplitCommand(instance.JvmArguments!))
                {
                    args.Add(Expand(part, tokens));
                }
            }

            if (!hasClasspathArg)
            {
                args.Add("-cp");
                args.Add(classpath);
            }

            args.Add(spec.MainClass);

            // 游戏参数
            if (spec.GameArguments.Count > 0)
            {
                foreach (var arg in spec.GameArguments)
                {
                    if (!MatchesRules(arg, context)) continue;
                    foreach (var value in arg.Values)
                    {
                        var expanded = Expand(value, tokens);
                        if (!string.IsNullOrWhiteSpace(expanded)) args.Add(expanded);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(spec.MinecraftArguments))
            {
                foreach (var part in spec.MinecraftArguments!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    args.Add(Expand(part, tokens));
                }
            }

            // 窗口设置
            if (instance.WindowMode == WindowMode.Custom)
            {
                args.Add("--width");
                args.Add(instance.WindowWidth.ToString());
                args.Add("--height");
                args.Add(instance.WindowHeight.ToString());
            }

            // ---- 兜底保护：绝不携带试玩模式与快速游玩参数 ----
            // 这些参数由版本 JSON 中的 features 规则控制，一旦规则解析异常就会把 --demo 带进来，
            // 导致离线账户进入试玩版。这里做最终过滤，保证启动参数始终正确。
            var forbidden = new[]
            {
                "--demo", "--quickPlayPath", "--quickPlaySingleplayer",
                "--quickPlayMultiplayer", "--quickPlayRealms"
            };
            var removed = args.RemoveAll(a =>
                forbidden.Contains(a, StringComparer.OrdinalIgnoreCase) ||
                a.Contains("${quickPlay", StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Logger.Warn($"已过滤 {removed} 个不适用于启动器的参数（试玩 / 快速游玩）");
            }

            return new LaunchPlan(java, args, instance.Directory, classpath);
        }

        public sealed class LaunchPlan(JavaInfo java, List<string> arguments, string workingDirectory, string classpath)
        {
            public JavaInfo Java { get; } = java;
            public List<string> Arguments { get; } = arguments;
            public string WorkingDirectory { get; } = workingDirectory;
            public string Classpath { get; } = classpath;

            /// <summary>优先使用 javaw.exe 避免额外控制台窗口。</summary>
            public string Executable => File.Exists(Java.WPath) ? Java.WPath : Java.Path;

            public string Preview =>
                $"\"{Executable}\" {string.Join(" ", Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
        }

        private readonly record struct LaunchContext(bool HasCustomResolution, bool IsDemoUser);

        private static bool MatchesRules(ResolvedArgument arg, LaunchContext context)
        {
            if (arg.Rules is null || arg.Rules.Count == 0) return true;
            var allowed = false;
            foreach (var rule in arg.Rules)
            {
                var osMatch = MatchesOs(rule.Os);
                var featureMatch = MatchesFeatures(rule.Features, context);
                var match = osMatch && featureMatch;
                var action = rule.Action ?? "allow";
                if (action.Equals("allow", StringComparison.OrdinalIgnoreCase))
                {
                    if (match) allowed = true;
                }
                else if (action.Equals("disallow", StringComparison.OrdinalIgnoreCase))
                {
                    if (match) return false;
                }
            }
            return allowed;
        }

        private static bool MatchesOs(RuleOs? os)
        {
            if (os is null) return true;
            if (!string.IsNullOrEmpty(os.Name) && !os.Name.Equals("windows", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(os.Arch) && !MatchesArch(os.Arch)) return false;
            return true;
        }

        /// <summary>
        /// 判断 os.arch 规则是否匹配当前进程架构。
        /// Mojang 用 "x86" 表示 32 位 Intel，64 位系统不应匹配该规则。
        /// </summary>
        internal static bool MatchesArch(string arch)
        {
            var is64 = Environment.Is64BitOperatingSystem;
            var isArm64 = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64;

            return arch.ToLowerInvariant() switch
            {
                "x86" or "i386" => !is64,
                "x86_64" or "amd64" => is64 && !isArm64,
                "arm64" or "aarch64" => isArm64,
                _ => true
            };
        }

        private static bool MatchesFeatures(RuleFeatures? features, LaunchContext context)
        {
            if (features is null) return true;
            if (features.HasCustomResolution is { } custom && custom != context.HasCustomResolution) return false;
            if (features.IsDemoUser is { } demo && demo != context.IsDemoUser) return false;
            if (features.HasQuickPlaysSupport is true) return false;
            if (features.IsQuickPlaySingleplayer is true) return false;
            if (features.IsQuickPlayMultiplayer is true) return false;
            if (features.IsQuickPlayRealms is true) return false;
            return true;
        }

        private static string Expand(string value, Dictionary<string, string> tokens)
        {
            if (!value.Contains('$')) return value;
            var result = value;
            foreach (var (key, replacement) in tokens)
            {
                result = result.Replace("${" + key + "}", replacement, StringComparison.OrdinalIgnoreCase);
            }
            return result;
        }

        private static IEnumerable<string> SplitCommand(string command)
        {
            var list = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;
            foreach (var c in command)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0) { list.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0) list.Add(current.ToString());
            return list;
        }

        /// <summary>
        /// 启动游戏。
        /// </summary>
        public async Task<LaunchResult> LaunchAsync(GameInstance instance, Account account,
            CancellationToken ct = default)
        {
            if (IsRunning) return new LaunchResult { Error = "已有游戏实例在运行" };

            try
            {
                var versionId = instance.ResolvedVersionId ?? instance.GameVersion;
                var spec = await AppServices.Catalog.ResolveAsync(versionId, ct);

                var integrity = GameInstallService.CheckIntegrity(spec);
                if (!integrity.IsComplete)
                {
                    Logger.Warn($"版本 {versionId} 文件缺失 {integrity.Missing.Count} 项，尝试修复…");
                    await AppServices.Installer.EnsureVersionAsync(versionId, null, ct);
                }

                var java = await ResolveJavaAsync(instance, spec, ct)
                           ?? throw new IOException("未找到可用的 Java 运行时。请打开「设置 → Java 运行环境」，" +
                                                    "点击「重新扫描」或直接安装所需的 Java 版本。");

                // 启动前的明确校验，避免出现难以理解的 JVM 弹框
                var requiredJava = spec.JavaMajorVersion > 0 ? spec.JavaMajorVersion : 8;
                if (java.MajorVersion < requiredJava)
                {
                    throw new IOException(
                        $"该游戏版本需要 Java {requiredJava} 或更高，当前仅有 Java {java.MajorVersion}。" +
                        $"请打开「设置 → Java 运行环境」，点击「安装 Java {requiredJava}」后重试。");
                }
                if (!java.Is64Bit && instance.MaxMemoryMb > 1024)
                {
                    throw new IOException(
                        $"32 位 Java 无法分配 {instance.MaxMemoryMb} MB 内存，请在「设置 → Java 运行环境」中安装 64 位 Java。");
                }

                if (!await AppServices.Auth.EnsureValidAsync(account, ct))
                    Logger.Warn("账户令牌刷新失败，仍尝试启动（离线会话可能失效）");

                var plan = BuildPlan(spec, instance, account, java);

                Directory.CreateDirectory(AppPaths.GameLogsRoot);
                var logPath = Path.Combine(AppPaths.GameLogsRoot, $"{instance.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _logWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
                _logWriter.WriteLine($"# {AppConstants.LauncherName} 启动日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _logWriter.WriteLine($"# 游戏版本：{instance.Name}（{instance.VersionText}）");
                _logWriter.WriteLine($"# Java：{java.Path}（{java.Version}）");
                _logWriter.WriteLine($"# 命令：{plan.Preview}");
                _logWriter.WriteLine();

                var psi = new ProcessStartInfo
                {
                    FileName = plan.Executable,
                    WorkingDirectory = plan.WorkingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = !instance.ShowConsole
                };
                foreach (var arg in plan.Arguments) psi.ArgumentList.Add(arg);

                _process = Process.Start(psi) ?? throw new IOException("无法启动 Java 进程");
                _startTime = DateTime.Now;
                _currentInstance = instance;
                instance.LastAccountId = account.Id;

                _process.OutputDataReceived += (_, e) => AppendLog(e.Data);
                _process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;

                Logger.Info($"游戏已启动：{instance.Name}（PID {_process.Id}）");

                // 后台等待主窗口创建，用于把「启动中」切换为「运行中」
                var started = _process;
                _ = Task.Run(() => WaitForGameWindowAsync(started));

                return new LaunchResult { Success = true, LogPath = logPath, CommandPreview = plan.Preview };
            }
            catch (Exception ex)
            {
                Logger.Error("启动失败", ex);
                return new LaunchResult { Error = ex.Message };
            }
        }

        /// <summary>
        /// 轮询检测游戏主窗口是否已创建（Java 进程启动后需要一段时间加载才会显示窗口）。
        /// 检测到窗口、进程退出或超时后结束等待，保证界面不会一直停留在「启动中」。
        /// </summary>
        private async Task WaitForGameWindowAsync(Process process)
        {
            var deadline = DateTime.UtcNow.AddSeconds(WindowWaitTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(300).ConfigureAwait(false);
                try
                {
                    if (process.HasExited) return; // 已退出，由退出事件负责收尾
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        Logger.Info("游戏窗口已出现");
                        UiDispatcher.Run(() => GameStarted?.Invoke());
                        return;
                    }
                }
                catch
                {
                    return; // 进程对象可能已被释放
                }
            }

            Logger.Warn($"等待游戏窗口超时（{WindowWaitTimeoutSeconds} 秒），按已启动处理");
            UiDispatcher.Run(() => GameStarted?.Invoke());
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            if (_process is null) return;
            var exitCode = _process.ExitCode;
            var elapsed = DateTime.Now - _startTime;
            _process.Dispose();
            _process = null;

            // 实例的绑定属性需在 UI 线程更新后再持久化
            UiDispatcher.Run(() =>
            {
                if (_currentInstance is { } instance)
                {
                    instance.LastPlayed = DateTime.Now;
                    instance.TotalPlaySeconds += (long)elapsed.TotalSeconds;
                    AppServices.Settings.Settings.LastInstanceId = instance.Id;
                }
                AppServices.Instances.Save();
                AppServices.Settings.Save();
            });

            _logWriter?.WriteLine($"\n# 进程退出，代码 {exitCode}，运行时长 {elapsed:hh\\:mm\\:ss}");
            _logWriter?.Dispose();
            _logWriter = null;

            Logger.Info($"游戏退出，代码 {exitCode}");
            if (exitCode != 0)
            {
                Logger.Warn($"游戏异常退出（代码 {exitCode}），可在【崩溃分析】页面查看详情");
            }
            GameExited?.Invoke(exitCode);
        }

        private void AppendLog(string? line)
        {
            if (line is null) return;
            try
            {
                _logWriter?.WriteLine(line);
                OutputReceived?.Invoke(line);
            }
            catch { /* 忽略写日志异常 */ }
        }

        /// <summary>
        /// 选择启动用的 Java：实例指定 → 全局设置 → 自动匹配，且始终保证满足游戏的最低版本要求。
        /// </summary>
        private async Task<JavaInfo?> ResolveJavaAsync(GameInstance instance, GameVersionSpec spec,
            CancellationToken ct)
        {
            var required = spec.JavaMajorVersion > 0 ? spec.JavaMajorVersion : 8;
            var javas = await AppServices.Java.ScanAsync(false, ct);
            var settings = AppServices.Settings.Settings;

            // 1) 实例指定的 Java（仅在其满足版本要求时采用）
            if (!string.IsNullOrEmpty(instance.JavaPath))
            {
                var custom = AppServices.Java.FindByPath(instance.JavaPath);
                if (custom is not null)
                {
                    if (custom.MajorVersion >= required) return custom;
                    Logger.Warn($"该版本指定的 Java {custom.MajorVersion} 低于游戏要求的 Java {required}，" +
                                "已自动改用更高版本（可在版本设置中调整）");
                }
            }

            // 2) 全局设置（仅在关闭"自动选择"时生效）
            if (!settings.AutoSelectJava && !string.IsNullOrEmpty(settings.JavaPath))
            {
                var preferred = AppServices.Java.FindByPath(settings.JavaPath);
                if (preferred is not null)
                {
                    if (preferred.MajorVersion >= required) return preferred;
                    Logger.Warn($"设置中的 Java {preferred.MajorVersion} 低于游戏要求的 Java {required}，已自动改用更高版本");
                }
            }

            // 3) 自动匹配满足要求的最低可用版本
            var matched = javas.Where(j => j.Is64Bit && j.MajorVersion >= required)
                .OrderBy(j => j.MajorVersion).FirstOrDefault();
            if (matched is not null) return matched;

            // 4) 兜底（调用方会因版本不足给出明确的安装提示）
            return AppServices.Java.SelectBest(required, settings.JavaPath) ?? javas.FirstOrDefault();
        }

        public void Kill()
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(true);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("结束游戏进程失败：" + ex.Message);
            }
        }
    }
}
