using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// CurseForge 官方 API 客户端（需要 API Key，可在 https://console.curseforge.com 申请）。
    /// </summary>
    public sealed class CurseForgeService
    {
        private const string Base = "https://api.curseforge.com/v1";
        private const int MinecraftGameId = 432;

        public bool HasApiKey => AppConstants.HasCurseForgeKey;

        private static string ApiKey => AppConstants.CurseForgeApiKey;

        private static HttpRequestMessage CreateRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return request;
        }

        private static async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            using var request = CreateRequest(url);
            using var resp = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new IOException($"CurseForge 请求失败（{(int)resp.StatusCode}）：{text.Ellipsis(200)}");
            }
            return text;
        }

        /// <summary>
        /// 检索资源。pageSize 最大 50，index 为起始偏移（用于翻页）。
        /// </summary>
        public async Task<ResourceSearchPage> SearchAsync(string query, ResourceType type,
            string? gameVersion = null, string? loader = null, int pageSize = 20, int index = 0,
            string sort = "popularity", CancellationToken ct = default)
        {
            if (!HasApiKey)
                throw new InvalidOperationException("当前版本未提供 CurseForge 数据源，请切换为 Modrinth 检索");

            pageSize = Math.Clamp(pageSize, 1, 50);
            index = Math.Max(0, index);

            // CurseForge 排序枚举：2=人气，3=最近更新，6=总下载量
            var sortField = sort switch
            {
                "downloads" => 6,
                "newest" => 3,
                "updated" => 3,
                _ => 2
            };

            var url = $"{Base}/mods/search?gameId={MinecraftGameId}&classId={ClassIdOf(type)}" +
                      $"&searchFilter={Uri.EscapeDataString(query ?? "")}" +
                      $"&pageSize={pageSize}&index={index}&sortField={sortField}&sortOrder=desc";
            if (!string.IsNullOrEmpty(gameVersion)) url += "&gameVersion=" + Uri.EscapeDataString(gameVersion!);
            if (!string.IsNullOrEmpty(loader)) url += "&modLoaderType=" + LoaderTypeOf(loader!);

            var json = await GetStringAsync(url, ct);
            var response = Json.Deserialize<SearchResponse>(json);
            return new ResourceSearchPage
            {
                Offset = index,
                Limit = pageSize,
                TotalCount = response?.Pagination?.TotalCount ?? 0,
                Items = response?.Data?.Select(m => ToResult(m, type)).ToList() ?? new List<ResourceSearchResult>()
            };
        }

        public async Task<List<ResourceVersion>> GetFilesAsync(string modId, string? gameVersion = null,
            string? loader = null, CancellationToken ct = default)
        {
            var url = $"{Base}/mods/{modId}/files?pageSize=50";
            if (!string.IsNullOrEmpty(gameVersion)) url += "&gameVersion=" + Uri.EscapeDataString(gameVersion!);
            if (!string.IsNullOrEmpty(loader)) url += "&modLoaderType=" + LoaderTypeOf(loader!);

            var json = await GetStringAsync(url, ct);
            var response = Json.Deserialize<FilesResponse>(json);
            if (response?.Data is null) return new List<ResourceVersion>();
            return response.Data.Select(ToVersion).ToList();
        }

        /// <summary>
        /// 获取文件下载地址（部分文件的 downloadUrl 为空，需要调用下载接口）。
        /// </summary>
        public async Task<string?> ResolveDownloadUrlAsync(string modId, string fileId, string? fileName = null,
            string? downloadUrl = null, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(downloadUrl)) return downloadUrl;
            try
            {
                var json = await GetStringAsync($"{Base}/mods/{modId}/files/{fileId}/download-url", ct);
                return Json.Deserialize<Dictionary<string, string>>(json)?.TryGetValue("data", out var url) == true ? url : null;
            }
            catch (Exception ex)
            {
                Logger.Warn("解析 CurseForge 下载地址失败：" + ex.Message);
                if (string.IsNullOrEmpty(fileName) || !int.TryParse(fileId, out var id)) return null;
                return $"https://mediafilez.forgecdn.net/files/{id / 1000}/{id % 1000}/{fileName}";
            }
        }

        private static int ClassIdOf(ResourceType type) => type switch
        {
            ResourceType.Mod => 6,
            ResourceType.Modpack => 4471,
            ResourceType.Shader => 6552,
            ResourceType.ResourcePack => 12,
            _ => 6
        };

        private static int LoaderTypeOf(string loader) => loader?.ToLowerInvariant() switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            _ => 0
        };

        private static ResourceSearchResult ToResult(Mod m, ResourceType type) => new()
        {
            Id = m.Id?.ToString() ?? "",
            Slug = m.Slug ?? "",
            Name = m.Name ?? "",
            Summary = m.Summary ?? "",
            IconUrl = m.Logo?.Url,
            Author = m.Authors?.FirstOrDefault()?.Name,
            Downloads = m.DownloadCount ?? 0,
            Follows = m.ThumbsUpCount ?? 0,
            UpdatedAt = DateTime.TryParse(m.DateModified, out var d) ? d.ToLocalTime() : null,
            Categories = m.Categories?.Select(c => c.Name ?? "").Where(n => n.Length > 0).ToList() ?? new List<string>(),
            Loaders = m.LatestFilesIndexes?
                          .Select(i => i.ModLoader?.ToString())
                          .Where(l => l is not null)
                          .Distinct()
                          .Select(MapLoader)
                          .ToList() ?? new List<string>(),
            GameVersions = m.GameVersions ?? new List<string>(),
            Type = type,
            Platform = ResourcePlatform.CurseForge,
            ProjectUrl = m.Links?.WebsiteUrl
        };

        private static string MapLoader(string? id) => id switch
        {
            "1" => "Forge",
            "4" => "Fabric",
            "5" => "Quilt",
            _ => "Any"
        };

        private static readonly string[] LoaderTokens = { "Forge", "Fabric", "Quilt", "NeoForge", "LiteLoader", "Rift" };

        /// <summary>CurseForge 会在 gameVersions 中混入运行环境标记，展示前需剔除。</summary>
        private static readonly string[] EnvironmentTokens = { "Client", "Server" };

        private static ResourceVersion ToVersion(ModFile f)
        {
            // CurseForge 把加载器名与环境标记混在 gameVersions 中，需要拆分出来
            var all = f.GameVersions ?? new List<string>();
            return new ResourceVersion
            {
                Id = f.Id?.ToString() ?? "",
                Name = f.DisplayName ?? f.FileName ?? "",
                VersionNumber = f.DisplayName,
                PublishedAt = DateTime.TryParse(f.FileDate, out var d) ? d.ToLocalTime() : null,
                GameVersions = all.Where(v =>
                    !LoaderTokens.Contains(v, StringComparer.OrdinalIgnoreCase) &&
                    !EnvironmentTokens.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList(),
                Loaders = all.Where(v => LoaderTokens.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList(),
                DownloadUrl = f.DownloadUrl,
                FileName = f.FileName,
                FileSize = f.FileLength ?? 0,
                Sha1 = f.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value,
                ReleaseChannel = f.ReleaseType?.ToString()
            };
        }

        // ---------------- DTO ----------------

        internal sealed class SearchResponse
        {
            public List<Mod>? Data { get; set; }
            public Pagination? Pagination { get; set; }
        }

        internal sealed class FilesResponse
        {
            public List<ModFile>? Data { get; set; }
        }

        internal sealed class Pagination
        {
            public int? Index { get; set; }
            public int? PageSize { get; set; }
            public int? TotalCount { get; set; }
        }

        internal sealed class Mod
        {
            public int? Id { get; set; }
            public string? Name { get; set; }
            public string? Slug { get; set; }
            public string? Summary { get; set; }
            public long? DownloadCount { get; set; }
            public long? ThumbsUpCount { get; set; }
            public string? DateModified { get; set; }
            public ModLogo? Logo { get; set; }
            public List<ModAuthor>? Authors { get; set; }
            public List<ModCategory>? Categories { get; set; }
            public List<string>? GameVersions { get; set; }
            public List<FileIndex>? LatestFilesIndexes { get; set; }
            public ModLinks? Links { get; set; }
        }

        internal sealed class ModLogo
        {
            public string? Url { get; set; }
        }

        internal sealed class ModAuthor
        {
            public string? Name { get; set; }
        }

        internal sealed class ModCategory
        {
            public string? Name { get; set; }
            public int? ClassId { get; set; }
        }

        internal sealed class FileIndex
        {
            public int? ModLoader { get; set; }
            public string? GameVersion { get; set; }
        }

        internal sealed class ModLinks
        {
            public string? WebsiteUrl { get; set; }
        }

        internal sealed class ModFile
        {
            public int? Id { get; set; }
            public string? DisplayName { get; set; }
            public string? FileName { get; set; }
            public string? DownloadUrl { get; set; }
            public string? FileDate { get; set; }
            public long? FileLength { get; set; }
            public int? ReleaseType { get; set; }
            public List<string>? GameVersions { get; set; }
            public List<FileHash>? Hashes { get; set; }
        }

        internal sealed class FileHash
        {
            public int? Algo { get; set; }
            public string? Value { get; set; }
        }
    }
}
