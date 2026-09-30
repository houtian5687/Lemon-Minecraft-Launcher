using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;
using Microsoft.UI.Dispatching;

namespace App1.ViewModels
{
    /// <summary>
    /// 下载队列中的"任务组"：一次版本安装 / 整合包导入 / 单资源下载聚合为一条。
    /// </summary>
    public sealed class DownloadGroupSummary : ObservableObject
    {
        private string _name = "";
        private int _total;
        private int _completed;
        private int _failed;
        private int _active;
        private int _paused;
        private int _canceled;
        private long _receivedBytes;
        private long _totalBytes;
        private double _speed;

        public string Key { get; init; } = "";

        public string Name
        {
            get => _name;
            private set => Set(ref _name, value);
        }

        public int Total => _total;
        public int Completed => _completed;
        public int Failed => _failed;
        public int Active => _active;
        public int Paused => _paused;
        public int Canceled => _canceled;

        /// <summary>总进度（优先按字节，缺失时按文件数）。</summary>
        public double Progress
        {
            get
            {
                if (_totalBytes > 0) return Math.Clamp(_receivedBytes * 100d / _totalBytes, 0, 100);
                if (_total > 0) return Math.Clamp(_completed * 100d / _total, 0, 100);
                return 0;
            }
        }

        public string ProgressText => $"{Progress:0.#}%";

        public string DetailText => _totalBytes > 0
            ? $"{_receivedBytes.FormatSize()} / {_totalBytes.FormatSize()} · {_completed}/{_total} 个文件"
            : $"{_completed}/{_total} 个文件";

        public string SpeedText => _speed.FormatSpeed();

        public bool IsRunning => _active > 0;

        public bool CanCancel => _active > 0 || _paused > 0 || _canceled == 0 && _completed < _total && _failed < _total;

        public string StateText
        {
            get
            {
                if (_active > 0) return _paused > 0 ? "下载中（含暂停）" : "下载中";
                if (_paused > 0) return "已暂停";
                if (_failed > 0) return _completed > 0 ? "部分失败" : "下载失败";
                if (_canceled > 0) return "已取消";
                if (_total > 0 && _completed >= _total) return "已完成";
                return "排队中";
            }
        }

        public string StateKey
        {
            get
            {
                if (_active > 0) return "Downloading";
                if (_paused > 0) return "Paused";
                if (_failed > 0) return "Failed";
                if (_canceled > 0) return "Canceled";
                if (_total > 0 && _completed >= _total) return "Completed";
                return "Queued";
            }
        }

        public bool IsFinished => _active == 0 && _paused == 0 &&
                                  (_total == 0 || _completed + _failed + _canceled >= _total);

        public void Update(string name, int total, int completed, int failed, int active, int paused, int canceled,
            long receivedBytes, long totalBytes, double speed)
        {
            Name = name;
            _total = total;
            _completed = completed;
            _failed = failed;
            _active = active;
            _paused = paused;
            _canceled = canceled;
            _receivedBytes = receivedBytes;
            _totalBytes = totalBytes;
            _speed = speed;

            Raise(nameof(Total), nameof(Completed), nameof(Failed), nameof(Active), nameof(Paused),
                nameof(Canceled), nameof(Progress), nameof(ProgressText), nameof(DetailText),
                nameof(SpeedText), nameof(IsRunning), nameof(CanCancel), nameof(StateText),
                nameof(StateKey), nameof(IsFinished));
        }
    }

    /// <summary>
    /// 下载队列：按任务组展示总进度，不显示单个文件的细节。
    /// </summary>
    public sealed class DownloadsViewModel : ObservableObject
    {
        private readonly Dictionary<string, DownloadGroupSummary> _map = new();
        private string _summaryText = "";
        private string _speedText = "0 B/s";
        private bool _isEmpty = true;
        private DispatcherQueueTimer? _timer;

        public ObservableCollection<DownloadGroupSummary> Groups { get; } = new();

        public string SummaryText
        {
            get => _summaryText;
            set => Set(ref _summaryText, value);
        }

        public string SpeedText
        {
            get => _speedText;
            set => Set(ref _speedText, value);
        }

        public bool IsEmpty
        {
            get => _isEmpty;
            set => Set(ref _isEmpty, value);
        }

        public RelayCommand CancelGroupCommand { get; }
        public RelayCommand ClearCommand { get; }
        public RelayCommand OpenFolderCommand { get; }

        public DownloadsViewModel()
        {
            CancelGroupCommand = new RelayCommand(p =>
            {
                if (p is DownloadGroupSummary group) CancelGroup(group);
            });
            ClearCommand = new RelayCommand(() =>
            {
                AppServices.Downloads.ClearCompleted();
                SyncGroups();
            });
            OpenFolderCommand = new RelayCommand(() =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.Root)
                {
                    UseShellExecute = true
                });
            });
        }

        public Task InitializeAsync()
        {
            SyncGroups();
            return Task.CompletedTask;
        }

        public void Start()
        {
            if (_timer is not null) return;
            _timer = UiDispatcher.Queue?.CreateTimer();
            if (_timer is null) return;
            _timer.Interval = TimeSpan.FromMilliseconds(400);
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public void Stop()
        {
            if (_timer is null) return;
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer = null;
        }

        private void OnTick(DispatcherQueueTimer sender, object args) => SyncGroups();

        private static string KeyOf(DownloadItem item) => item.GroupId ?? "solo:" + item.Id;

        /// <summary>
        /// 按分组汇总当前下载队列（每 400ms 刷新一次，避免逐文件刷屏）。
        /// </summary>
        private void SyncGroups()
        {
            var items = AppServices.Downloads.Items;
            var alive = new HashSet<string>(StringComparer.Ordinal);

            foreach (var group in items.GroupBy(KeyOf))
            {
                var list = group.ToList();
                alive.Add(group.Key);

                if (!_map.TryGetValue(group.Key, out var summary))
                {
                    summary = new DownloadGroupSummary { Key = group.Key };
                    _map[group.Key] = summary;
                    Groups.Add(summary);
                }

                int Count(Func<DownloadItem, bool> predicate) => list.Count(predicate);

                summary.Update(
                    name: list.FirstOrDefault(i => i.GroupName is not null)?.GroupName ?? list[0].FileName,
                    total: list.Count,
                    completed: Count(i => i.State == DownloadState.Completed),
                    failed: Count(i => i.State == DownloadState.Failed),
                    active: Count(i => i.State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Verifying),
                    paused: Count(i => i.State == DownloadState.Paused),
                    canceled: Count(i => i.State == DownloadState.Canceled),
                    receivedBytes: list.Sum(i => i.ReceivedBytes),
                    totalBytes: list.Sum(i => i.TotalBytes),
                    speed: list.Where(i => i.State == DownloadState.Downloading).Sum(i => i.Speed));
            }

            foreach (var key in _map.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                var summary = _map[key];
                _map.Remove(key);
                Groups.Remove(summary);
            }

            IsEmpty = Groups.Count == 0;
            SpeedText = items.Where(i => i.State == DownloadState.Downloading).Sum(i => i.Speed).FormatSpeed();

            var running = Groups.Count(g => g.IsRunning);
            var finished = Groups.Count(g => g.IsFinished);
            var failed = Groups.Count(g => !g.IsRunning && g.Failed > 0);
            SummaryText = $"进行中 {running} · 已完成 {finished}" + (failed > 0 ? $" · 失败 {failed}" : "");
        }

        private void CancelGroup(DownloadGroupSummary group)
        {
            foreach (var item in AppServices.Downloads.Items.Where(i => KeyOf(i) == group.Key).ToList())
            {
                AppServices.Downloads.Cancel(item);
            }
            SyncGroups();
        }
    }
}
