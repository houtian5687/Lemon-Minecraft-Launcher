using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// 崩溃日志分析：识别常见故障模式并给出可执行的修复建议。
    /// </summary>
    public sealed class CrashAnalysisService
    {
        private sealed record Pattern(string Name, string Regex, CrashSeverity Severity, string Title,
            string Description, string[] Suggestions);

        private static readonly List<Pattern> Patterns = new()
        {
            new Pattern("OutOfMemory", @"OutOfMemoryError|Java heap space|GC overhead limit exceeded|Metaspace",
                CrashSeverity.Fatal, "内存不足（OutOfMemoryError）",
                "Java 虚拟机在运行时耗尽了分配的内存，通常由内存设置过小、模组过多或高分辨率材质包导致。",
                new[]
                {
                    "在实例设置中把最大内存提高到 4096 MB 以上（不要超过物理内存的 70%）",
                    "减少同时加载的高清材质包与光影",
                    "若安装了大量模组，尝试分批禁用排查",
                    "添加 JVM 参数：-XX:+UseG1GC -XX:MaxGCPauseMillis=200"
                }),

            new Pattern("MemoryTooSmall", @"Could not reserve enough space for object heap|Invalid maximum heap size",
                CrashSeverity.Error, "内存设置非法",
                "分配给 JVM 的内存超过了可用物理内存或系统限制。",
                new[] { "降低最大内存设置", "确认系统是 64 位并使用 64 位 Java", "关闭其他占用内存的程序" }),

            new Pattern("JavaVersion", @"Unsupported major\.minor version|class file has wrong version|UnsupportedClassVersionError",
                CrashSeverity.Fatal, "Java 版本不匹配",
                "当前使用的 Java 版本与该 Minecraft 版本或模组所需的 Java 版本不一致。",
                new[]
                {
                    "1.20.5 及以上请使用 Java 21",
                    "1.18 ~ 1.20.4 请使用 Java 17",
                    "1.17 请使用 Java 16，1.16 及更早请使用 Java 8",
                    "可在【设置 → Java】中点击自动安装对应版本"
                }),

            new Pattern("Graphics", @"Pixel format not accelerated|No OpenGL context found|GLFW error 65542|Could not create GL context|WGL: The driver does not appear to support OpenGL|opengl not supported",
                CrashSeverity.Error, "显卡驱动 / OpenGL 问题",
                "游戏无法创建 OpenGL 上下文，通常是显卡驱动过旧或使用了集成显卡。",
                new[]
                {
                    "更新显卡驱动到最新版本（NVIDIA / AMD / Intel 官网下载）",
                    "若使用笔记本电脑，在显卡控制面板中强制使用独立显卡运行 javaw.exe",
                    "删除实例的 options.txt 后重试",
                    "尝试关闭光影或降低渲染设置"
                }),

            new Pattern("Natives", @"UnsatisfiedLinkError|Failed to locate library|no lwjgl.* in java\.library\.path|LWJGLException|Failed to load the native library",
                CrashSeverity.Error, "本地库（native）缺失或损坏",
                "游戏无法加载 LWJGL 等平台本地库，通常是 natives 目录文件缺失。",
                new[] { "在实例上点击【修复安装】重新下载依赖", "确认杀毒软件未隔离 natives 目录中的 dll", "更换游戏目录路径（避免中文或特殊字符）" }),

            new Pattern("CorruptJar", @"ZipException|invalid or corrupt jarfile|Error: Invalid or corrupt jarfile|Invalid signature file digest",
                CrashSeverity.Error, "文件损坏",
                "下载的游戏或库文件不完整/损坏。",
                new[] { "在实例上点击【修复安装】重新校验并下载", "清理下载缓存目录后重试", "检查磁盘空间是否充足" }),

            new Pattern("MainClass", @"Could not find or load main class|ClassNotFoundException: net\.minecraft|NoClassDefFoundError: net/minecraft",
                CrashSeverity.Fatal, "主类无法加载",
                "版本文件缺失或版本目录被破坏，导致找不到游戏主类。",
                new[] { "在实例上点击【修复安装】", "检查游戏目录中 versions 文件夹是否完整", "重新创建该实例" }),

            new Pattern("MixinFailure", @"Mixin apply failed|mixin.*failed to apply|MixinTransformation|Critical injection failure|mixin\.Mixins",
                CrashSeverity.Fatal, "Mixin 注入失败（模组冲突）",
                "某个模组的 Mixin 注入失败，通常是模组之间不兼容或模组与游戏版本不匹配。",
                new[]
                {
                    "查看下方相关日志确定出错的模组并移除或更新它",
                    "确认所有模组的版本与目标游戏版本一致",
                    "逐个禁用模组进行二分法排查",
                    "尝试删除 config 目录重新生成配置"
                }),

            new Pattern("ModResolution", @"ModResolutionException|Mods are missing|Missing or unsupported mandatory dependencies|requires \[|Found duplicate mods|DuplicateModsFoundException|Errors were found during loading",
                CrashSeverity.Fatal, "模组依赖缺失或重复",
                "模组加载器检测到缺失的依赖、版本不匹配或重复的模组。",
                new[]
                {
                    "按日志提示安装缺失的依赖模组（如 JEI、CraftTweaker 等）",
                    "删除重复的模组文件（mods 目录中同名不同版本）",
                    "确认模组支持当前加载器（Fabric / Forge / Quilt 不可混用）"
                }),

            new Pattern("ModLoad", @"Error occurred during initialization|Failed to load mod|Cannot load mod|mod loading error|Uncaught exception in thread ""main""",
                CrashSeverity.Error, "模组加载阶段异常",
                "在模组初始化阶段发生异常，通常由某个模组引起。",
                new[]
                {
                    "查看日志中 Caused by 部分定位具体模组",
                    "移除最近安装的模组后重试",
                    "删除 config 目录让模组重新生成配置"
                }),

            new Pattern("ForgeMissing", @"Attempted to load class.*net/minecraft|fml.*coremod|Forge Mod Loader has found a problem with your minecraft installation",
                CrashSeverity.Error, "Forge 安装异常",
                "Forge 安装不完整或与游戏版本不匹配。",
                new[] { "重新选择 Forge 版本并安装", "确认 Forge 版本与游戏版本一致", "删除 versions 下对应的 forge 目录后重新安装" }),

            new Pattern("Permission", @"Access is denied|Permission denied|java\.io\.FileNotFoundException.*\(拒绝访问|Failed to create profile directory",
                CrashSeverity.Error, "文件权限不足",
                "启动器或游戏无法写入游戏目录，通常是权限不足或文件被占用。",
                new[] { "以管理员身份运行启动器", "将游戏目录移动到非系统盘（如 D:\\Games\\Minecraft）", "关闭占用该目录的程序（如杀毒软件、资源管理器）" }),

            new Pattern("DiskSpace", @"No space left on device|There is not enough space on the disk",
                CrashSeverity.Error, "磁盘空间不足",
                "目标磁盘剩余空间不足以写入游戏文件。",
                new[] { "清理磁盘空间", "更换游戏目录到空间充足的磁盘" }),

            new Pattern("Log4j", @"log4j|JndiLookup",
                CrashSeverity.Warning, "检测到 Log4j 相关输出",
                "日志中包含 Log4j 相关信息，启动器已默认加入 -Dlog4j2.formatMsgNoLookups=true 缓解。",
                new[] { "无需处理，如仍担心可使用 1.18.2+ 的游戏版本" }),

            new Pattern("NetworkAuth", @"Invalid session|Authentication failed|Failed to verify username|io\.netty\.channel",
                CrashSeverity.Warning, "认证或网络异常",
                "正版验证失败或联机时网络异常。",
                new[] { "重新登录微软账户", "检查系统时间与网络代理设置", "离线模式无法进入需要正版验证的服务器" }),

            new Pattern("ExitAccessViolation", @"EXCEPTION_ACCESS_VIOLATION|0xc0000005|-1073741819",
                CrashSeverity.Fatal, "内存访问冲突（0xC0000005）",
                "JVM 或本地库发生了非法内存访问，常由驱动、超频或内存硬件问题引起。",
                new[]
                {
                    "更新显卡驱动",
                    "关闭内存超频（XMP / DOCP）后重试",
                    "运行 Windows 内存诊断检查内存稳定性",
                    "尝试更换 Java 版本"
                })
        };

        private static readonly Regex ModLineRegex =
            new(@"(?i)(?:mods\\|mods/|Loading mod|from mod|Mod ID: '?)([A-Za-z0-9_\-\.\[\]]{3,60})", RegexOptions.Compiled);

        /// <summary>
        /// 分析日志文本。
        /// </summary>
        public CrashReport Analyze(string logText, int? exitCode = null, string? instanceName = null)
        {
            var report = new CrashReport
            {
                Issues = new List<CrashIssue>(),
                Session = new GameSessionLog
                {
                    InstanceName = instanceName ?? "",
                    ExitCode = exitCode
                }
            };

            var lines = logText.SplitLines().ToList();
            report.LogLineCount = lines.Count;

            foreach (var pattern in Patterns)
            {
                var regex = new Regex(pattern.Regex, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                var matched = new List<string>();
                foreach (var line in lines)
                {
                    if (line.Length > 2000) continue;
                    if (regex.IsMatch(line))
                    {
                        matched.Add(line.Trim().Ellipsis(240));
                        if (matched.Count >= 5) break;
                    }
                }
                if (matched.Count == 0) continue;

                report.Issues.Add(new CrashIssue
                {
                    Title = pattern.Title,
                    Description = pattern.Description,
                    Severity = pattern.Severity,
                    Suggestions = pattern.Suggestions.ToList(),
                    RelatedLines = matched
                });
            }

            // 退出码兜底分析
            if (exitCode is { } code && code != 0)
            {
                var known = report.Issues.Count > 0;
                if (!known)
                {
                    report.Issues.Add(new CrashIssue
                    {
                        Title = $"进程异常退出（代码 {code}）",
                        Description = "游戏进程以非零代码退出，日志中未匹配到已知错误模式。",
                        Severity = CrashSeverity.Error,
                        Suggestions = new()
                        {
                            "查看日志末尾的 Caused by 堆栈定位问题",
                            "尝试修复安装以重新校验游戏文件",
                            "移除最近安装的模组 / 光影 / 材质包后重试",
                            "在【设置 → Java】中更换 Java 版本"
                        },
                        RelatedLines = lines.AsEnumerable().Reverse().Take(20).Reverse().Select(l => l.Ellipsis(240)).ToList()
                    });
                }
            }

            // 提取可能相关的模组
            var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                var match = ModLineRegex.Match(line);
                if (match.Success) mods.Add(match.Groups[1].Value.Trim('\'', '"', ','));
                if (mods.Count > 40) break;
            }
            report.DetectedMods = mods.Take(20).ToList();

            var fatal = report.Issues.FirstOrDefault(i => i.Severity == CrashSeverity.Fatal)
                        ?? report.Issues.FirstOrDefault(i => i.Severity == CrashSeverity.Error)
                        ?? report.Issues.FirstOrDefault();
            report.RootCause = fatal?.Title;
            report.OsInfo = $"Windows {Environment.OSVersion.Version}";
            return report;
        }

        /// <summary>
        /// 分析日志文件。
        /// </summary>
        public CrashReport AnalyzeFile(string path, int? exitCode = null)
        {
            if (!File.Exists(path)) return new CrashReport { RootCause = "日志文件不存在" };
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                var report = Analyze(text, exitCode, Path.GetFileName(path));
                report.Session!.LogPath = path;
                return report;
            }
            catch (Exception ex)
            {
                Logger.Warn("读取日志失败：" + ex.Message);
                return new CrashReport { RootCause = "无法读取日志：" + ex.Message };
            }
        }

        /// <summary>
        /// 列出历史游戏日志（按时间倒序）。
        /// </summary>
        public static List<FileInfo> ListGameLogs()
        {
            if (!Directory.Exists(AppPaths.GameLogsRoot)) return new List<FileInfo>();
            return Directory.GetFiles(AppPaths.GameLogsRoot, "*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(50)
                .ToList();
        }

        /// <summary>
        /// 生成一键反馈用的报告摘要。
        /// </summary>
        public static string ToShareText(CrashReport report)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Minecraft 崩溃报告");
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"系统：{report.OsInfo}");
            if (report.Session is not null)
            {
                sb.AppendLine($"游戏版本：{report.Session.InstanceName}");
                sb.AppendLine($"退出码：{report.Session.ExitCode}");
                sb.AppendLine($"日志：{report.Session.LogPath}");
            }
            sb.AppendLine($"疑似原因：{report.RootCause ?? "未知"}");
            sb.AppendLine();
            foreach (var issue in report.Issues)
            {
                sb.AppendLine($"## [{issue.SeverityText}] {issue.Title}");
                sb.AppendLine(issue.Description);
                foreach (var s in issue.Suggestions) sb.AppendLine("  - " + s);
                foreach (var line in issue.RelatedLines.Take(3)) sb.AppendLine("  > " + line);
                sb.AppendLine();
            }
            if (report.DetectedMods.Count > 0)
            {
                sb.AppendLine("## 日志中出现的模组");
                sb.AppendLine(string.Join(", ", report.DetectedMods));
            }
            return sb.ToString();
        }
    }
}
