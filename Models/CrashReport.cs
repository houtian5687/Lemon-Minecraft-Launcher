using System;
using System.Collections.Generic;

namespace App1.Models
{
    public enum CrashSeverity
    {
        Info,
        Warning,
        Error,
        Fatal
    }

    /// <summary>
    /// 崩溃分析得出的单条问题。
    /// </summary>
    public sealed class CrashIssue
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public CrashSeverity Severity { get; set; } = CrashSeverity.Error;
        public List<string> Suggestions { get; set; } = new();
        public List<string> RelatedLines { get; set; } = new();

        public string SeverityText => Severity switch
        {
            CrashSeverity.Fatal => "致命",
            CrashSeverity.Error => "错误",
            CrashSeverity.Warning => "警告",
            _ => "提示"
        };
    }

    /// <summary>
    /// 一次游戏会话的日志与崩溃分析结果。
    /// </summary>
    public sealed class GameSessionLog
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string InstanceName { get; set; } = "";
        public string VersionId { get; set; } = "";
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public DateTime? ExitedAt { get; set; }
        public int? ExitCode { get; set; }
        public bool Crashed { get; set; }
        public string LogPath { get; set; } = "";
    }

    /// <summary>
    /// 完整崩溃报告。
    /// </summary>
    public sealed class CrashReport
    {
        public GameSessionLog? Session { get; set; }
        public List<CrashIssue> Issues { get; set; } = new();
        public List<string> DetectedMods { get; set; } = new();
        public string? RootCause { get; set; }
        public string? JavaVersion { get; set; }
        public string? OsInfo { get; set; }
        public int LogLineCount { get; set; }
        public string Summary => Issues.Count == 0 ? "未发现已知问题" : $"发现 {Issues.Count} 个问题";
    }
}
