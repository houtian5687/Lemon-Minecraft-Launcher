using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;

namespace App1.Models
{
    /// <summary>
    /// 单个下载任务：支持断点续传、分块并行、暂停 / 继续 / 重试与速度统计。
    /// </summary>
    public sealed class DownloadItem : ObservableObject
    {
        private long _receivedBytes;
        private long _totalBytes;
        private DownloadState _state = DownloadState.Queued;
        private double _speed;
        private string? _errorMessage;
        private string _statusText = "排队中";

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string Url { get; set; } = "";

        public string DestinationPath { get; set; } = "";

        public string FileName { get; set; } = "";

        public string? ExpectedSha1 { get; set; }

        public long? ExpectedSize { get; set; }

        /// <summary>分组标识，用于整组任务（如"安装 1.20.4"）合并显示。</summary>
        public string? GroupId { get; set; }

        public string? GroupName { get; set; }

        public int RetryCount { get; set; }

        public int MaxRetry { get; set; } = 3;

        /// <summary>被下载源限流后的退避计数（每次退避等待时间递增）。</summary>
        public int ThrottleBackoff { get; set; }

        public bool CanPause { get; set; } = true;

        /// <summary>
        /// 是否允许分块并行下载。部分镜像对 Range 支持不完整，
        /// 大文件（如 Java 运行时）建议关闭，改用单流 + 断点续传。
        /// </summary>
        public bool AllowChunking { get; set; } = true;

        public string? IconUrl { get; set; }

        /// <summary>
        /// 可选文件：下载失败时仅记录警告，不影响整体任务结果。
        /// （旧格式依赖库中部分文件在仓库中并不存在）
        /// </summary>
        public bool IsOptional { get; set; }

        /// <summary>备用下载地址（主地址失败后依次尝试，例如官方源失败后改用镜像）。</summary>
        public List<string>? FallbackUrls { get; set; }

        public long ReceivedBytes
        {
            get => _receivedBytes;
            set
            {
                if (Set(ref _receivedBytes, value)) Raise(nameof(Progress), nameof(ProgressText), nameof(DetailText));
            }
        }

        public long TotalBytes
        {
            get => _totalBytes;
            set
            {
                if (Set(ref _totalBytes, value)) Raise(nameof(Progress), nameof(ProgressText), nameof(DetailText), nameof(IsIndeterminate));
            }
        }

        public double Speed
        {
            get => _speed;
            set
            {
                if (Set(ref _speed, value)) Raise(nameof(SpeedText), nameof(RemainingText), nameof(DetailText));
            }
        }

        public DownloadState State
        {
            get => _state;
            set
            {
                if (Set(ref _state, value))
                {
                    Raise(nameof(StatusText), nameof(IsRunning), nameof(CanResume), nameof(CanCancel), nameof(IsIndeterminate));
                    StateText = value.ToText();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public string StateText { get; private set; } = "排队中";

        public string? ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (Set(ref _errorMessage, value)) Raise(nameof(HasError), nameof(DetailText));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public double Progress => TotalBytes > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes * 100d, 0, 100) : 0;

        public bool IsIndeterminate => State == DownloadState.Downloading && TotalBytes <= 0;

        public bool IsRunning => State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Verifying;

        public bool CanResume => State is DownloadState.Paused or DownloadState.Failed or DownloadState.Canceled;

        public bool CanCancel => State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Paused;

        public string ProgressText => $"{Progress:0.#}%";

        public string SpeedText => Speed.FormatSpeed();

        public string RemainingText
        {
            get
            {
                if (Speed <= 1 || TotalBytes <= 0) return "--";
                var remain = TotalBytes - ReceivedBytes;
                if (remain <= 0) return "即将完成";
                return TimeSpan.FromSeconds(remain / Speed).FormatTime();
            }
        }

        public string DetailText =>
            $"{ReceivedBytes.FormatSize()} / {(TotalBytes > 0 ? TotalBytes.FormatSize() : "未知")} · {SpeedText} · 剩余 {RemainingText}";

        public override string ToString() => $"{FileName} - {State}";

        // ---- 运行期状态（不参与绑定）----
        internal CancellationTokenSource? Cts { get; set; }
        internal long[]? ChunkProgress { get; set; }
        internal bool SupportsRange { get; set; }
        internal TaskCompletionSource<bool>? Completion { get; set; }
    }

    /// <summary>
    /// 下载任务批量进度汇报。
    /// </summary>
    public sealed class DownloadProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public string? CurrentFile { get; set; }
        public long ReceivedBytes { get; set; }
        public long TotalBytes { get; set; }

        public double Percent => Total == 0 ? 0 : (double)Completed / Total * 100;

        public string Text => $"{Completed}/{Total}";
    }
}
