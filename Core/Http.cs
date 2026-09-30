using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace App1.Core
{
    /// <summary>
    /// 全局共享的 HttpClient 与常用请求助手。
    /// </summary>
    public static class Http
    {
        /// <summary>
        /// 单次请求的响应头超时（秒）。
        /// 网络层超时抛出的是 TaskCanceledException（消息 The operation was canceled.），
        /// 调用方必须区分它与「用户主动取消」，否则会把网络故障误报成已取消。
        /// </summary>
        public const int RequestTimeoutSeconds = 90;

        public static readonly HttpClient Client;

        /// <summary>
        /// 是否为限速镜像源（BMCLAPI 等）：这类源对请求频率有严格限制，
        /// 需要关闭分块并行并做请求节流，否则极易触发 429。
        /// </summary>
        public static bool IsMirrorUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.Contains("bmclapi", StringComparison.OrdinalIgnoreCase)
                || url.Contains("bangbang93", StringComparison.OrdinalIgnoreCase);
        }

        static Http()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = 64,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                // 放宽到 30 秒：镜像源在限流时会静默丢包，建连本身也可能变慢
                ConnectTimeout = TimeSpan.FromSeconds(30),
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                EnableMultipleHttp2Connections = true
            };
            Client = new HttpClient(handler, true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            Client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.UserAgent);
            Client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        }

        public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        {
            using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }

        public static async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default)
        {
            var json = await GetStringAsync(url, ct);
            return Json.Deserialize<T>(json);
        }

        public static async Task<T?> PostFormJsonAsync<T>(string url, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> form, CancellationToken ct = default)
        {
            using var content = new FormUrlEncodedContent(form);
            using var resp = await Client.PostAsync(url, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"POST {url} 返回 {(int)resp.StatusCode}: {text}");
                return default;
            }
            return Json.Deserialize<T>(text);
        }

        /// <summary>
        /// 以 camelCase 字段名提交 JSON（Minecraft 服务等）。
        /// 注意：Xbox Live / XSTS 要求 PascalCase，必须改用 <see cref="Json.XboxOptions"/> 自行序列化，
        /// 否则请求会被服务端拒绝。
        /// </summary>
        public static async Task<T?> PostJsonAsync<T>(string url, object payload, CancellationToken ct = default)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload, Json.Options);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await Client.PostAsync(url, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"POST {url} 返回 {(int)resp.StatusCode}: {text}");
                return default;
            }
            return Json.Deserialize<T>(text);
        }
    }
}
