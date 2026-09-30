using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    public sealed class DownloadRequest
    {
        public string Url { get; init; } = "";
        public string Destination { get; init; } = "";
        public string? Sha1 { get; init; }
        public long? Size { get; init; }
        public string? FileName { get; init; }
        public string? IconUrl { get; init; }

        /// <summary>可选文件：失败时仅跳过，不影响整体结果。</summary>
        public bool Optional { get; init; }

        /// <summary>备用下载地址。</summary>
        public List<string>? FallbackUrls { get; init; }
    }

    public sealed class BatchDownloadResult
    {
        public int Succeeded { get; set; }
        public int Failed { get; set; }

        /// <summary>可选文件跳过数量（不算失败）。</summary>
        public int Skipped { get; set; }

        public long TotalBytes { get; set; }
        public List<string> Errors { get; } = new();

        public bool Success => Failed == 0;
    }

    /// <summary>
    /// 全局下载服务：多任务并行 + 单文件分块并行 + 断点续传 + 限速 + 哈希校验。
    /// </summary>
    public sealed class DownloadService
    {
        private readonly object _sync = new();
        private readonly ConcurrentQueue<DownloadItem> _pending = new();
        private readonly List<CancellationTokenSource> _workerCts = new();
        private readonly RateLimiter _limiter = new();

        /// <summary>
        /// 镜像源请求节流：BMCLAPI 短期配额约 10 次 / 60 秒，
        /// 这里留出安全余量，避免并发请求直接撞上限流。
        /// </summary>
        private readonly RequestThrottle _mirrorThrottle = new(8, TimeSpan.FromSeconds(60));

        private int _maxParallel = 6;
        private int _chunkCount = 4;
        private long _chunkThreshold = 8L * 1024 * 1024;

        public ObservableCollection<DownloadItem> Items { get; } = new();

        /// <summary>已完成的历史任务（界面可清理）。</summary>
        public ObservableCollection<DownloadItem> History { get; } = new();

        public event EventHandler<DownloadItem>? ItemAdded;

        public int ActiveCount => Items.Count(i => i.IsRunning);

        public void Configure(int maxParallel, int chunkCount, long chunkThresholdBytes, long speedLimitBytesPerSecond)
        {
            lock (_sync)
            {
                _maxParallel = Math.Clamp(maxParallel, 1, 32);
                _chunkCount = Math.Clamp(chunkCount, 1, 16);
                _chunkThreshold = Math.Max(1024, chunkThresholdBytes);
            }
            _limiter.SetLimit(speedLimitBytesPerSecond);
            SyncWorkers();
        }

        private void SyncWorkers()
        {
            lock (_sync)
            {
                while (_workerCts.Count < _maxParallel)
                {
                    var cts = new CancellationTokenSource();
                    _workerCts.Add(cts);
                    _ = Task.Run(() => WorkerLoopAsync(cts.Token));
                }
                while (_workerCts.Count > _maxParallel)
                {
                    var last = _workerCts[^1];
                    _workerCts.RemoveAt(_workerCts.Count - 1);
                    last.Cancel();
                    last.Dispose();
                }
            }
        }

        private async Task WorkerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_pending.TryDequeue(out var item))
                {
                    try { await Task.Delay(120, ct); } catch { return; }
                    continue;
                }
                if (item.State == DownloadState.Canceled) continue;
                try
                {
                    await ProcessAsync(item);
                }
                catch (Exception ex)
                {
                    Logger.Error("下载任务异常：" + item.FileName, ex);
                }
            }
        }

        // ---------------- 公开 API ----------------

        public DownloadItem Enqueue(string url, string destination, string? sha1 = null, long? size = null,
            string? groupId = null, string? groupName = null, string? fileName = null, string? iconUrl = null,
            bool optional = false, IEnumerable<string>? fallbackUrls = null)
        {
            var item = new DownloadItem
            {
                Url = url,
                DestinationPath = destination,
                ExpectedSha1 = sha1,
                ExpectedSize = size,
                GroupId = groupId,
                GroupName = groupName,
                FileName = fileName ?? Path.GetFileName(destination),
                IconUrl = iconUrl,
                IsOptional = optional,
                FallbackUrls = fallbackUrls?.ToList(),
                TotalBytes = size ?? 0
            };
            Register(item);
            return item;
        }

        private void Register(DownloadItem item)
        {
            item.Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            UiDispatcher.Run(() =>
            {
                Items.Add(item);
                ItemAdded?.Invoke(this, item);
            });
            item.State = DownloadState.Queued;
            _pending.Enqueue(item);
            SyncWorkers();
        }

        /// <summary>
        /// 等待任务进入终态（完成 / 失败 / 取消）。
        /// 采用轮询而非单次 Task 等待，可正确处理"暂停后恢复"的场景。
        /// </summary>
        public async Task<bool> WaitAsync(DownloadItem item)
        {
            while (true)
            {
                switch (item.State)
                {
                    case DownloadState.Completed:
                        return true;
                    case DownloadState.Failed:
                    case DownloadState.Canceled:
                        return false;
                }
                try
                {
                    await Task.Delay(200);
                }
                catch
                {
                    return item.State == DownloadState.Completed;
                }
            }
        }

        /// <summary>
        /// 批量下载：并行入队、统一进度汇报，返回失败详情。
        /// 传入同一个 groupId 可让多次批量下载在界面中聚合为同一个"下载任务"。
        /// </summary>
        public async Task<BatchDownloadResult> DownloadAllAsync(IEnumerable<DownloadRequest> requests,
            string groupName, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default,
            string? groupId = null)
        {
            var list = requests.ToList();
            groupId ??= Guid.NewGuid().ToString("N");
            var result = new BatchDownloadResult();
            var items = new List<DownloadItem>();

            foreach (var r in list)
            {
                if (r.Sha1 is { Length: 40 } && File.Exists(r.Destination))
                {
                    // 已存在且哈希一致，跳过
                    try
                    {
                        var existing = await r.Destination.Sha1Async(ct);
                        if (string.Equals(existing, r.Sha1, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Succeeded++;
                            continue;
                        }
                    }
                    catch { /* 继续下载 */ }
                }
                items.Add(Enqueue(r.Url, r.Destination, r.Sha1, r.Size, groupId, groupName, r.FileName, r.IconUrl,
                    r.Optional, r.FallbackUrls));
            }

            if (items.Count == 0)
            {
                progress?.Report(new DownloadProgress { Completed = result.Succeeded, Total = result.Succeeded });
                return result;
            }

            long totalBytes = items.Sum(i => i.ExpectedSize ?? 0);
            var reporter = new ProgressReporter(items, progress, totalBytes, ct);

            var tasks = items.Select(WaitAsync).ToArray();
            await Task.WhenAll(tasks);
            reporter.Dispose();

            foreach (var item in items)
            {
                if (item.State == DownloadState.Completed)
                {
                    result.Succeeded++;
                    result.TotalBytes += item.TotalBytes;
                }
                else if (item.IsOptional)
                {
                    // 旧格式依赖库中确实不存在的文件，跳过即可
                    result.Skipped++;
                    Logger.Warn($"可选文件跳过：{item.FileName}（{item.ErrorMessage ?? item.StateText}）");
                }
                else
                {
                    result.Failed++;
                    result.Errors.Add($"{item.FileName}：{item.ErrorMessage ?? item.StateText}");
                }
            }

            progress?.Report(new DownloadProgress
            {
                Completed = result.Succeeded,
                Total = list.Count,
                TotalBytes = totalBytes,
                ReceivedBytes = items.Sum(i => i.ReceivedBytes)
            });
            return result;
        }

        public void Pause(DownloadItem item)
        {
            if (item.State is not (DownloadState.Downloading or DownloadState.Queued)) return;
            item.Cts?.Cancel();
            item.State = DownloadState.Paused;
            item.Speed = 0;
        }

        public void Resume(DownloadItem item)
        {
            if (!item.CanResume) return;
            item.ErrorMessage = null;
            item.RetryCount = 0;
            item.Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            item.State = DownloadState.Queued;
            _pending.Enqueue(item);
            SyncWorkers();
        }

        public void Cancel(DownloadItem item)
        {
            item.Cts?.Cancel();
            item.State = DownloadState.Canceled;
            item.Speed = 0;
            item.Completion?.TrySetResult(false);
        }

        public void CancelGroup(string groupId)
        {
            foreach (var item in Items.ToList())
            {
                if (item.GroupId == groupId) Cancel(item);
            }
        }

        public void ClearCompleted()
        {
            UiDispatcher.Run(() =>
            {
                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    if (Items[i].State is DownloadState.Completed or DownloadState.Canceled)
                    {
                        History.Insert(0, Items[i]);
                        Items.RemoveAt(i);
                    }
                }
            });
        }

        // ---------------- 下载核心 ----------------

        /// <summary>
        /// 统一切换任务状态：绑定属性必须在 UI 线程更新，否则会抛跨线程访问异常。
        /// </summary>
        private static void SetState(DownloadItem item, DownloadState state, string? status = null, string? error = null)
        {
            UiDispatcher.Run(() =>
            {
                if (status is not null) item.StatusText = status;
                if (error is not null) item.ErrorMessage = error;
                item.State = state;
            });
        }

        private async Task ProcessAsync(DownloadItem item)
        {
            item.Cts = new CancellationTokenSource();
            var ct = item.Cts.Token;
            try
            {
                SetState(item, DownloadState.Downloading, "下载中");
                await DownloadCoreAsync(item, ct);
                SetState(item, DownloadState.Verifying, "校验文件");
                var ok = await VerifyAsync(item, ct);
                if (!ok)
                {
                    throw new IOException("文件校验失败，可能与源文件不一致");
                }
                UiDispatcher.Run(() => item.Speed = 0);
                SetState(item, DownloadState.Completed, "已完成");
                Logger.Info($"下载完成：{item.FileName}（{item.TotalBytes.FormatSize()}）");
                item.Completion?.TrySetResult(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 仅当「本任务的令牌」被取消时才视作用户操作（暂停 / 取消）。
                // 网络层超时同样抛出 TaskCanceledException，但用户的令牌并未取消，
                // 因此不会命中此分支，而会进入下方的失败重试流程。
                if (item.State == DownloadState.Paused)
                {
                    Logger.Info($"下载已暂停：{item.FileName}");
                    return; // 保持暂停状态，等待用户恢复
                }
                UiDispatcher.Run(() => item.Speed = 0);
                SetState(item, DownloadState.Canceled, "已取消");
                item.Completion?.TrySetResult(false);
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(item, ex);
            }
            finally
            {
                item.Cts?.Dispose();
                item.Cts = null;
            }
        }

        /// <summary>
        /// 统一处理下载失败：限流退避 → 切换备用源 → 常规重试 → 最终失败。
        /// </summary>
        private async Task HandleFailureAsync(DownloadItem item, Exception ex)
        {
            var message = Describe(ex);
            Logger.Warn($"下载失败 {item.Url}：{message}");
            UiDispatcher.Run(() => item.Speed = 0);

            // 1）下载源限流：按递增间隔退避，并放宽重试次数（镜像源恢复需要时间）
            if (IsRateLimited(ex))
            {
                item.RetryCount = 0;
                item.MaxRetry = Math.Max(item.MaxRetry, 12);
                item.ThrottleBackoff++;
                var wait = Math.Min(60, 8 * item.ThrottleBackoff);
                Logger.Info($"下载源限流，{wait} 秒后重试：{item.FileName}");
                SetState(item, DownloadState.Queued, $"源限流，{wait} 秒后重试", message);
                await Task.Delay(TimeSpan.FromSeconds(wait), CancellationToken.None);
                _pending.Enqueue(item);
                SyncWorkers();
                return;
            }

            // 2）主地址失败时优先切换备用源（官方源 ↔ BMCLAPI 镜像）
            if (item.FallbackUrls is { Count: > 0 })
            {
                item.Url = item.FallbackUrls[0];
                item.FallbackUrls.RemoveAt(0);
                Logger.Info($"切换备用下载源：{item.Url}");
                SetState(item, DownloadState.Queued, "切换备用源", message);
                _pending.Enqueue(item);
                SyncWorkers();
                return;
            }

            // 3）常规重试（退避时间随次数递增）
            if (item.RetryCount < item.MaxRetry)
            {
                item.RetryCount++;
                SetState(item, DownloadState.Queued, $"重试 {item.RetryCount}/{item.MaxRetry}", message);
                await Task.Delay(500 + 500 * item.RetryCount, CancellationToken.None);
                _pending.Enqueue(item);
                SyncWorkers();
                return;
            }

            // 4）最终失败
            SetState(item, DownloadState.Failed, "失败", message);
            item.Completion?.TrySetResult(false);
        }

        private static bool IsRateLimited(Exception ex) =>
            ex is HttpRequestException
            {
                StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            };

        /// <summary>
        /// 将技术性异常转换为面向用户的中文说明。
        /// </summary>
        private static string Describe(Exception ex) => ex switch
        {
            TaskCanceledException or OperationCanceledException
                => "连接超时（网络不通或下载源限流）",
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
                => "下载源限流（请求过于频繁）",
            HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable }
                => "下载源暂时不可用",
            HttpRequestException { StatusCode: { } status }
                => $"服务器返回 {(int)status}（{status}）",
            IOException io => io.Message,
            _ => ex.Message
        };

        /// <summary>
        /// 发起一次带超时的请求。超时抛出的 TaskCanceledException 不会取消用户令牌，
        /// 因此会被识别为网络故障而非用户取消。镜像源请求会自动按配额节流。
        /// </summary>
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, DownloadItem item, CancellationToken ct)
        {
            if (Http.IsMirrorUrl(item.Url))
            {
                // 配额已满时给出明确的状态提示，避免界面看起来"卡住不动"
                if (_mirrorThrottle.IsThrottled)
                    SetState(item, DownloadState.Downloading, "等待下载源配额");
                await _mirrorThrottle.AcquireAsync(ct);
            }
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Http.RequestTimeoutSeconds));
            // 响应头到达即释放定时器；正文读取只受用户令牌控制
            return await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }

        private async Task DownloadCoreAsync(DownloadItem item, CancellationToken ct)
        {
            var dir = Path.GetDirectoryName(item.DestinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 镜像源（BMCLAPI 等）请求配额紧张：关闭分块并行，
            // 避免瞬时并发直接撞上限流阈值（实测约 10 次 / 60 秒）
            var isMirror = Http.IsMirrorUrl(item.Url);
            if (isMirror) item.AllowChunking = false;

            var probe = await ProbeAsync(item, ct, isMirror);
            long total = probe.Total;
            item.SupportsRange = probe.SupportsRange;
            UiDispatcher.Run(() => item.TotalBytes = total);

            if (total > 0 && File.Exists(item.DestinationPath))
            {
                var existing = new FileInfo(item.DestinationPath).Length;
                if (existing == total)
                {
                    UiDispatcher.Run(() => item.ReceivedBytes = total);
                    return; // 交给校验阶段
                }
                if (existing > total) File.Delete(item.DestinationPath);
            }

            int chunks;
            lock (_sync) { chunks = _chunkCount; }
            var threshold = _chunkThreshold;

            if (item.AllowChunking && probe.SupportsRange && total > threshold && chunks > 1)
            {
                await DownloadChunkedAsync(item, total, chunks, ct);
            }
            else
            {
                await DownloadSequentialAsync(item, total, probe.SupportsRange, ct);
            }
        }

        private async Task<ProbeResult> ProbeAsync(DownloadItem item, CancellationToken ct, bool isMirror)
        {
            // 镜像源配额紧张：已知大小时跳过探测（探测会额外消耗一次请求配额），
            // 是否支持断点续传由实际请求自适应（见 DownloadSequentialAsync）
            if (item.ExpectedSize is > 0 && isMirror)
                return new ProbeResult(item.ExpectedSize.Value, true);

            if (item.ExpectedSize is > 0)
            {
                // 已知大小时仍需确认是否支持断点续传
                using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
                req.Headers.Range = new RangeHeaderValue(0, 0);
                try
                {
                    using var resp = await SendAsync(req, item, ct);
                    if (resp.StatusCode == HttpStatusCode.PartialContent &&
                        resp.Content.Headers.ContentRange?.Length is { } len)
                    {
                        return new ProbeResult(len, true);
                    }
                    return new ProbeResult(resp.Content.Headers.ContentLength ?? item.ExpectedSize.Value,
                        resp.StatusCode == HttpStatusCode.PartialContent);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // 用户取消，向上传递
                }
                catch (Exception ex)
                {
                    // 探测失败不影响后续下载：按已知大小、不支持续传处理
                    Logger.Warn($"探测文件信息失败 {item.Url}：{Describe(ex)}");
                    return new ProbeResult(item.ExpectedSize.Value, false);
                }
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
                req.Headers.Range = new RangeHeaderValue(0, 0);
                using var resp = await SendAsync(req, item, ct);
                if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.Length is { } len)
                    return new ProbeResult(len, true);
                if (resp.Content.Headers.ContentLength is { } size)
                    return new ProbeResult(size, false);
                resp.Dispose();
                await Task.Delay(200, ct);
            }
            return new ProbeResult(-1, false);
        }

        private readonly record struct ProbeResult(long Total, bool SupportsRange);

        private async Task DownloadSequentialAsync(DownloadItem item, long total, bool supportsRange, CancellationToken ct)
        {
            long start = 0;
            var mode = FileMode.Create;
            if (supportsRange && File.Exists(item.DestinationPath))
            {
                var existing = new FileInfo(item.DestinationPath).Length;
                if (existing > 0 && (total <= 0 || existing < total))
                {
                    start = existing;
                    mode = FileMode.Open;
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
            if (start > 0) request.Headers.Range = new RangeHeaderValue(start, null);
            using var resp = await SendAsync(request, item, ct);
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                start = 0;
                mode = FileMode.Create;
                using var retry = new HttpRequestMessage(HttpMethod.Get, item.Url);
                using var retryResp = await SendAsync(retry, item, ct);
                await CopyToAsync(item, retryResp, start, total, ct, mode, chunkIndex: -1);
                return;
            }
            resp.EnsureSuccessStatusCode();

            // 请求了 Range 但服务器返回完整内容（200）时，必须从头覆盖写入，
            // 否则会把整个文件写到错误偏移处，导致文件损坏
            if (start > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                start = 0;
                mode = FileMode.Create;
            }

            if (total <= 0 && resp.Content.Headers.ContentLength is { } len)
                UiDispatcher.Run(() => item.TotalBytes = len);
            await CopyToAsync(item, resp, start, total, ct, mode, chunkIndex: -1);
        }

        private async Task DownloadChunkedAsync(DownloadItem item, long total, int chunks, CancellationToken ct)
        {
            // 立刻释放预分配文件句柄，否则后续分块会因独占锁而失败
            using (var fsInit = new FileStream(item.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fsInit.SetLength(total);
            }

            var chunkProgress = item.ChunkProgress ??= new long[chunks];
            var ranges = SplitRange(total, chunks);
            var tasks = new List<Task>();
            for (int i = 0; i < ranges.Count; i++)
            {
                var (start, end) = ranges[i];
                var index = i;
                if (chunkProgress[index] >= end - start + 1) continue;
                tasks.Add(Task.Run(() => DownloadChunkAsync(item, start + chunkProgress[index], end, index, ct), ct));
            }
            await Task.WhenAll(tasks);
            UiDispatcher.Run(() => item.ReceivedBytes = total);
        }

        private async Task DownloadChunkAsync(DownloadItem item, long start, long end, int index, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            using var resp = await SendAsync(request, item, ct);
            resp.EnsureSuccessStatusCode();

            // 服务器忽略 Range 直接返回完整内容时，分块写入会破坏文件结构，
            // 此时抛出异常交由上层改走单流下载
            if (resp.StatusCode != HttpStatusCode.PartialContent)
                throw new IOException("下载源不支持分块下载，将改用单线程重试");

            using var fs = new FileStream(item.DestinationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
            fs.Seek(start, SeekOrigin.Begin);
            await CopyToAsync(item, resp, start, end - start + 1, ct, FileMode.Open, index, fs);
        }

        private async Task CopyToAsync(DownloadItem item, HttpResponseMessage resp, long start, long total,
            CancellationToken ct, FileMode mode, int chunkIndex, FileStream? providedStream = null)
        {
            FileStream? fs = providedStream;
            if (fs is null)
            {
                fs = new FileStream(item.DestinationPath, mode, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
                if (mode == FileMode.Open) fs.Seek(start, SeekOrigin.Begin);
            }
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[1 << 16];
                long written = start;
                var tracker = new ProgressTracker(item, start, total, chunkIndex);
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await _limiter.ConsumeAsync(read, ct);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    tracker.Add(read);
                }
                await fs.FlushAsync(ct);
                tracker.Flush();
            }
            finally
            {
                if (providedStream is null) fs?.Dispose();
            }
        }

        private static List<(long Start, long End)> SplitRange(long total, int chunks)
        {
            var list = new List<(long, long)>();
            long part = total / chunks;
            long current = 0;
            for (int i = 0; i < chunks; i++)
            {
                long end = (i == chunks - 1) ? total - 1 : current + part - 1;
                list.Add((current, end));
                current = end + 1;
            }
            return list;
        }

        private async Task<bool> VerifyAsync(DownloadItem item, CancellationToken ct)
        {
            if (!File.Exists(item.DestinationPath)) return false;
            if (item.TotalBytes > 0)
            {
                var size = new FileInfo(item.DestinationPath).Length;
                if (size != item.TotalBytes)
                {
                    item.ErrorMessage = $"文件大小不一致（预期 {item.TotalBytes.FormatSize()}，实际 {size.FormatSize()}）";
                    return false;
                }
            }
            if (string.IsNullOrWhiteSpace(item.ExpectedSha1)) return true;

            var actual = await item.DestinationPath.Sha1Async(ct);
            if (!string.Equals(actual, item.ExpectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                item.ErrorMessage = $"SHA1 校验失败（预期 {item.ExpectedSha1[..8]}…，实际 {actual[..8]}…）";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 汇总一组任务的整体进度。
        /// </summary>
        private sealed class ProgressReporter : IDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly Task? _task;

            public ProgressReporter(List<DownloadItem> items, IProgress<DownloadProgress>? progress, long totalBytes, CancellationToken ct)
            {
                if (progress is null) return;
                _task = Task.Run(async () =>
                {
                    while (!_cts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        progress.Report(new DownloadProgress
                        {
                            Completed = items.Count(i => i.State == DownloadState.Completed),
                            Total = items.Count,
                            CurrentFile = items.FirstOrDefault(i => i.State == DownloadState.Downloading)?.FileName,
                            ReceivedBytes = items.Sum(i => i.ReceivedBytes),
                            TotalBytes = totalBytes
                        });
                        await Task.Delay(300, CancellationToken.None);
                    }
                });
            }

            public void Dispose() => _cts.Cancel();
        }

        /// <summary>
        /// 单文件进度节流与速度计算。
        /// </summary>
        private sealed class ProgressTracker
        {
            private readonly DownloadItem _item;
            private readonly long _start;
            private readonly long _total;
            private readonly int _chunkIndex;
            private long _received;
            private long _lastTick = Environment.TickCount64;
            private long _lastBytes;

            public ProgressTracker(DownloadItem item, long start, long total, int chunkIndex)
            {
                _item = item;
                _start = start;
                _total = total;
                _chunkIndex = chunkIndex;
                _lastBytes = start;
                _received = start;
            }

            public void Add(int count)
            {
                _received += count;
                if (_chunkIndex >= 0 && _item.ChunkProgress is { } progress && _chunkIndex < progress.Length)
                {
                    progress[_chunkIndex] = _received - _start;
                }
                var now = Environment.TickCount64;
                var elapsed = now - _lastTick;
                if (elapsed < 250) return;
                var bytesDelta = _received - _lastBytes;
                var received = _received;
                var speed = bytesDelta / (elapsed / 1000d);
                _lastBytes = received;
                _lastTick = now;

                // 绑定属性必须回到 UI 线程更新（定时器已做 250ms 节流）
                UiDispatcher.Run(() =>
                {
                    _item.Speed = speed;
                    _item.ReceivedBytes = received;
                });
            }

            public void Flush()
            {
                var received = _received;
                UiDispatcher.Run(() =>
                {
                    _item.ReceivedBytes = received;
                    _item.Speed = 0;
                });
            }
        }
    }

    /// <summary>
    /// 全局限速器（令牌桶）。
    /// </summary>
    internal sealed class RateLimiter
    {
        private long _bytesPerSecond;
        private long _budget;
        private long _last = Environment.TickCount64;
        private readonly object _sync = new();

        public void SetLimit(long bytesPerSecond)
        {
            lock (_sync)
            {
                _bytesPerSecond = bytesPerSecond;
                _budget = bytesPerSecond;
                _last = Environment.TickCount64;
            }
        }

        public async Task ConsumeAsync(int count, CancellationToken ct)
        {
            if (_bytesPerSecond <= 0) return;
            while (!ct.IsCancellationRequested)
            {
                int waitMs;
                lock (_sync)
                {
                    var now = Environment.TickCount64;
                    var elapsed = now - _last;
                    if (elapsed > 0)
                    {
                        _budget = Math.Min(_bytesPerSecond, _budget + elapsed * _bytesPerSecond / 1000);
                        _last = now;
                    }
                    if (_budget >= count)
                    {
                        _budget -= count;
                        return;
                    }
                    waitMs = (int)Math.Clamp((count - _budget) * 1000 / _bytesPerSecond, 1, 250);
                }
                await Task.Delay(waitMs, ct);
            }
        }
    }

    /// <summary>
    /// 请求频率节流器：在滑动窗口内限制请求次数（用于 BMCLAPI 等有硬性配额的源）。
    /// </summary>
    internal sealed class RequestThrottle
    {
        private readonly int _maxRequests;
        private readonly double _windowMs;
        private readonly Queue<long> _timestamps = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile bool _throttled;

        public RequestThrottle(int maxRequests, TimeSpan window)
        {
            _maxRequests = Math.Max(1, maxRequests);
            _windowMs = window.TotalMilliseconds;
        }

        /// <summary>当前窗口配额是否已用尽（用于界面提示）。</summary>
        public bool IsThrottled => _throttled;

        /// <summary>
        /// 获取一次请求许可；超出窗口配额时等待至最早的那次请求过期。
        /// </summary>
        public async Task AcquireAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                int waitMs;
                await _gate.WaitAsync(ct);
                try
                {
                    var now = Environment.TickCount64;
                    while (_timestamps.Count > 0 && now - _timestamps.Peek() >= _windowMs)
                    {
                        _timestamps.Dequeue();
                    }
                    if (_timestamps.Count < _maxRequests)
                    {
                        _timestamps.Enqueue(now);
                        _throttled = false;
                        return;
                    }
                    _throttled = true;
                    waitMs = (int)Math.Clamp(_windowMs - (now - _timestamps.Peek()) + 50, 50, 5000);
                }
                finally
                {
                    _gate.Release();
                }
                await Task.Delay(waitMs, ct);
            }
            ct.ThrowIfCancellationRequested();
        }
    }
}
