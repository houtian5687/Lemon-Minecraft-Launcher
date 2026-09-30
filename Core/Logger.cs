using System;
using System.Collections.Concurrent;
using System.IO;

namespace App1.Core
{
    /// <summary>
    /// 轻量日志系统：按天分文件写入，同时提供内存环形缓冲供界面查看。
    /// </summary>
    public static class Logger
    {
        private static readonly ConcurrentQueue<LogEntry> Buffer = new();
        private const int MaxBuffer = 2000;
        private static readonly object FileLock = new();
        private static string? _file;

        public static string LogDirectory { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppConstants.LauncherShortName, "logs");

        public static event Action<LogEntry>? EntryAdded;

        private static string File0
        {
            get
            {
                if (_file is null)
                {
                    Directory.CreateDirectory(LogDirectory);
                    _file = Path.Combine(LogDirectory, $"launcher-{DateTime.Now:yyyyMMdd}.log");
                }
                return _file;
            }
        }

        public static void Info(string message) => Write(LogLevel.Info, message, null);
        public static void Warn(string message) => Write(LogLevel.Warning, message, null);
        public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);
        public static void Debug(string message) => Write(LogLevel.Debug, message, null);

        public static void Write(LogLevel level, string message, Exception? ex)
        {
            var entry = new LogEntry(DateTime.Now, level, message, ex?.ToString());
            Buffer.Enqueue(entry);
            while (Buffer.Count > MaxBuffer) Buffer.TryDequeue(out _);
            try
            {
                EntryAdded?.Invoke(entry);
            }
            catch { /* 忽略 UI 回调异常 */ }

            try
            {
                lock (FileLock)
                {
                    File.AppendAllText(File0, entry.ToString() + Environment.NewLine);
                }
            }
            catch { /* 日志写入失败不应影响主流程 */ }
        }

        public static System.Collections.Generic.IEnumerable<LogEntry> Snapshot() => Buffer.ToArray();
    }

    public enum LogLevel { Debug, Info, Warning, Error }

    public sealed record LogEntry(DateTime Time, LogLevel Level, string Message, string? Detail)
    {
        public override string ToString()
            => $"[{Time:HH:mm:ss.fff}] [{Level}] {Message}{(Detail is null ? "" : Environment.NewLine + Detail)}";
    }
}
