using System.Collections.Generic;
using App1.Core;

namespace App1.Models
{
    /// <summary>
    /// 检测到的 Java 运行时信息。
    /// </summary>
    public sealed class JavaInfo : ObservableObject
    {
        private bool _isRecommended;

        public string Path { get; set; } = "";

        /// <summary>javaw.exe 路径（无控制台窗口，优先用于启动游戏）。</summary>
        public string WPath { get; set; } = "";

        public string Version { get; set; } = "";

        public int MajorVersion { get; set; }

        public bool Is64Bit { get; set; } = true;

        public string Vendor { get; set; } = "";

        public string Source { get; set; } = "";

        public bool Exists { get; set; }

        public bool IsRecommended
        {
            get => _isRecommended;
            set
            {
                if (Set(ref _isRecommended, value)) Raise(nameof(DisplayText));
            }
        }

        public string Architecture => Is64Bit ? "64 位" : "32 位";

        public string DisplayText =>
            $"{Version} · {Architecture}{(string.IsNullOrEmpty(Vendor) ? "" : " · " + Vendor)}" +
            (IsRecommended ? " · 推荐" : "") +
            (string.IsNullOrEmpty(Source) ? "" : $"（{Source}）");

        public override string ToString() => DisplayText;
    }

    /// <summary>
    /// Java 版本需求（来自版本 JSON 的 javaVersion.majorVersion）。
    /// </summary>
    public static class JavaRequirement
    {
        /// <summary>
        /// 根据 MC 版本推荐最低 Java 主版本。
        /// 1.20.5+ / 1.21+ 需 Java 21；1.18 ~ 1.20.4 需 17；1.17 需 16；1.16 及更早需 8。
        /// </summary>
        public static int RecommendFor(string? gameVersion)
        {
            if (string.IsNullOrEmpty(gameVersion)) return 17;
            var parts = gameVersion.Split('.');
            if (parts.Length < 2) return 17;
            if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
                return 17;

            // 新式版本号（如 25w14a 之外的 26.1 等）直接按主版本判断
            if (major > 1) return major >= 21 ? 21 : 17;

            if (minor >= 21) return 21;
            if (minor == 20)
            {
                var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
                return patch >= 5 ? 21 : 17;
            }
            if (minor >= 18) return 17;
            if (minor == 17) return 16;
            return 8;
        }

        public static IEnumerable<string> SearchDirectories()
        {
            var list = new List<string>();
            var pf = System.Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            var pf86 = System.Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "AdoptOpenJDK", "Zulu", "BellSoft", "Microsoft", "Amazon", "GraalVM", "OpenLogic", "SAP" })
            {
                list.Add(System.IO.Path.Combine(pf, vendor));
                list.Add(System.IO.Path.Combine(pf86, vendor));
            }
            list.Add(System.IO.Path.Combine(pf, "Minecraft Launcher", "runtime"));
            list.Add(System.IO.Path.Combine(pf86, "Minecraft Launcher", "runtime"));
            list.Add(AppPaths.JavaRoot);
            return list;
        }
    }
}
