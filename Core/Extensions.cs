using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace App1.Core
{
    public static class Extensions
    {
        public static string FormatSize(this long bytes)
        {
            if (bytes < 0) return "-";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.##} {units[unit]}";
        }

        public static string FormatSpeed(this double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "0 B/s";
            return ((long)bytesPerSecond).FormatSize() + "/s";
        }

        public static string FormatTime(this TimeSpan span)
        {
            if (span.TotalSeconds < 0) return "--";
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        public static string Ellipsis(this string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";

        public static async Task<string> Sha1Async(this string filePath, CancellationToken ct = default)
        {
            using var stream = File.OpenRead(filePath);
            using var sha1 = SHA1.Create();
            var hash = await sha1.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static async Task<string> Sha256Async(this string filePath, CancellationToken ct = default)
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string Sha256(this string text)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        /// <summary>
        /// 删除目录并容错重试（应对文件占用）。
        /// </summary>
        public static void DeleteDirectorySafe(this string dir)
        {
            if (!Directory.Exists(dir)) return;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Directory.Delete(dir, true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
            Logger.Warn("无法完全删除目录：" + dir);
        }

        public static void CopyDirectory(this string source, string destination, IProgress<double>? progress = null)
        {
            Directory.CreateDirectory(destination);
            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            long total = 0, done = 0;
            foreach (var f in files)
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
                try { done += new FileInfo(file).Length; } catch { }
                progress?.Report(total == 0 ? 1 : (double)done / total);
            }
        }

        public static bool ContainsIgnoreCase(this string text, string value)
            => text.Contains(value, StringComparison.OrdinalIgnoreCase);

        public static IEnumerable<string> SplitLines(this string text)
            => text.Replace("\r\n", "\n").Split('\n');
    }
}
