using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// Modrinth 开放 API 客户端（https://docs.modrinth.com）。
    /// </summary>
    public sealed class ModrinthService
    {
        private const string Base = "https://api.modrinth.com/v2";

        /// <summary>
        /// 检索资源。limit 最大 100，offset 用于翻页。
        /// sort 支持 relevance / downloads / follows / newest / updated。
        /// </summary>
        public async Task<ResourceSearchPage> SearchAsync(string query, ResourceType type,
            string? gameVersion = null, string? loader = null, int limit = 20, int offset = 0,
            string? category = null, string sort = "relevance", CancellationToken ct = default)
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(0, offset);

            var facets = new List<string> { $"[\"project_type:{TypeToProjectType(type)}\"]" };
            if (!string.IsNullOrEmpty(gameVersion)) facets.Add($"[\"versions:{gameVersion}\"]");
            if (!string.IsNullOrEmpty(loader)) facets.Add($"[\"categories:{loader.ToLowerInvariant()}\"]");
            if (!string.IsNullOrEmpty(category)) facets.Add($"[\"categories:{category.ToLowerInvariant()}\"]");

            var index = sort switch
            {
                "downloads" => "downloads",
                "follows" => "follows",
                "newest" => "newest",
                "updated" => "updated",
                _ => "relevance"
            };

            var url = $"{Base}/search?query={Uri.EscapeDataString(query ?? "")}" +
                      $"&limit={limit}&offset={offset}&index={index}" +
                      $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

            try
            {
                var response = await Http.GetJsonAsync<SearchResponse>(url, ct);
                return new ResourceSearchPage
                {
                    Offset = offset,
                    Limit = limit,
                    TotalCount = response?.TotalHits ?? 0,
                    Items = response?.Hits?.Select(ToResult).ToList() ?? new List<ResourceSearchResult>()
                };
            }
            catch (Exception ex)
            {
                Logger.Warn("Modrinth 搜索失败：" + ex.Message);
                throw new IOException("Modrinth 搜索失败：" + ex.Message, ex);
            }
        }

        public async Task<ResourceSearchResult?> GetProjectAsync(string idOrSlug, CancellationToken ct = default)
        {
            var project = await Http.GetJsonAsync<Project>($"{Base}/project/{Uri.EscapeDataString(idOrSlug)}", ct);
            return project is null ? null : ToResult(project);
        }

        public async Task<List<ResourceVersion>> GetVersionsAsync(string idOrSlug, string? gameVersion = null,
            string? loader = null, CancellationToken ct = default)
        {
            var url = $"{Base}/project/{Uri.EscapeDataString(idOrSlug)}/version";
            var query = new List<string>();
            if (!string.IsNullOrEmpty(gameVersion)) query.Add($"game_versions=[\"{gameVersion}\"]");
            if (!string.IsNullOrEmpty(loader)) query.Add($"loaders=[\"{loader.ToLowerInvariant()}\"]");
            if (query.Count > 0) url += "?" + string.Join("&", query.Select(Uri.EscapeDataString));

            var list = await Http.GetJsonAsync<List<ProjectVersion>>(url, ct) ?? new();
            return list.Select(ToVersion).ToList();
        }

        /// <summary>
        /// 拉取指定资源类型的分类标签（用于商店页的分类筛选）。
        /// </summary>
        public async Task<List<string>> GetCategoriesAsync(ResourceType type, CancellationToken ct = default)
        {
            var projectType = TypeToProjectType(type);
            try
            {
                var tags = await Http.GetJsonAsync<List<TagItem>>($"{Base}/tag/category", ct) ?? new();
                return tags.Where(t => string.Equals(t.ProjectType, projectType, StringComparison.OrdinalIgnoreCase))
                           .Select(t => t.Name)
                           .Where(n => !string.IsNullOrEmpty(n))
                           .ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn("获取 Modrinth 分类失败：" + ex.Message);
                return new List<string>();
            }
        }

        private static string TypeToProjectType(ResourceType type) => type switch
        {
            ResourceType.Mod => "mod",
            ResourceType.Modpack => "modpack",
            ResourceType.Shader => "shader",
            ResourceType.ResourcePack => "resourcepack",
            _ => "mod"
        };

        private static ResourceSearchResult ToResult(Project p) => new()
        {
            Id = p.Id ?? p.Slug ?? "",
            Slug = p.Slug ?? "",
            Name = p.Title ?? p.Name ?? p.Slug ?? "",
            Summary = p.Description ?? "",
            Description = p.Body,
            IconUrl = p.IconUrl,
            Author = p.Author,
            Downloads = p.Downloads ?? 0,
            Follows = p.Follows ?? 0,
            UpdatedAt = ParseDate(p.Updated),
            Categories = p.Categories ?? new List<string>(),
            Loaders = (p.Categories ?? new List<string>())
                .Where(c => new[] { "fabric", "forge", "quilt", "neoforge", "vanilla", "bukkit", "spigot" }.Contains(c))
                .ToList(),
            // 搜索结果不含 game_versions（只有版本 ID），仅项目详情可用
            GameVersions = p.GameVersions ?? new List<string>(),
            Type = ProjectTypeToResource(p.ProjectType),
            Platform = ResourcePlatform.Modrinth,
            ProjectUrl = p.Slug is null ? null : $"https://modrinth.com/mod/{p.Slug}",
            SourceUrl = p.SourceUrl,
            // gallery 是对象数组，这里只取出图片地址
            Gallery = p.Gallery?
                .Select(g => g.Url)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!)
                .ToList()
        };

        private static ResourceVersion ToVersion(ProjectVersion v)
        {
            var file = v.Files?.FirstOrDefault(f => f.Primary == true) ?? v.Files?.FirstOrDefault();
            return new ResourceVersion
            {
                Id = v.Id ?? "",
                Name = v.Name ?? v.VersionNumber ?? "",
                VersionNumber = v.VersionNumber,
                Changelog = v.Changelog,
                PublishedAt = ParseDate(v.DatePublished),
                GameVersions = v.GameVersions ?? new List<string>(),
                Loaders = v.Loaders ?? new List<string>(),
                DownloadUrl = file?.Url,
                FileName = file?.FileName,
                FileSize = file?.Size ?? 0,
                Sha1 = file?.Hashes?.Sha1,
                Sha512 = file?.Hashes?.Sha512,
                IsPrimary = file?.Primary ?? false,
                ReleaseChannel = v.VersionType
            };
        }

        private static DateTime? ParseDate(string? value)
            => DateTime.TryParse(value, out var date) ? date.ToLocalTime() : null;

        private static ResourceType ProjectTypeToResource(string? type) => type switch
        {
            "mod" => ResourceType.Mod,
            "modpack" => ResourceType.Modpack,
            "shader" => ResourceType.Shader,
            "resourcepack" => ResourceType.ResourcePack,
            _ => ResourceType.Mod
        };

        // ---------------- DTO ----------------

        internal sealed class SearchResponse
        {
            public List<Project>? Hits { get; set; }
            public int? Offset { get; set; }
            public int? Limit { get; set; }

            [JsonPropertyName("total_hits")]
            public int? TotalHits { get; set; }
        }

        /// <summary>
        /// Modrinth 返回的是 snake_case，必须显式映射，否则字段会静默丢失。
        /// </summary>
        internal sealed class Project
        {
            [JsonPropertyName("project_id")]
            public string? Id { get; set; }

            public string? Slug { get; set; }
            public string? Title { get; set; }
            public string? Name { get; set; }
            public string? Description { get; set; }
            public string? Body { get; set; }

            [JsonPropertyName("icon_url")]
            public string? IconUrl { get; set; }

            public string? Author { get; set; }
            public long? Downloads { get; set; }
            public long? Follows { get; set; }

            [JsonPropertyName("date_modified")]
            public string? Updated { get; set; }

            public List<string>? Categories { get; set; }
            public List<string>? Versions { get; set; }

            [JsonPropertyName("game_versions")]
            public List<string>? GameVersions { get; set; }

            [JsonPropertyName("project_type")]
            public string? ProjectType { get; set; }

            [JsonPropertyName("source_url")]
            public string? SourceUrl { get; set; }

            /// <summary>
            /// 截图列表。搜索接口返回字符串数组、项目详情返回对象数组，
            /// 由 GalleryConverter 统一兼容。
            /// </summary>
            [JsonConverter(typeof(GalleryConverter))]
            public List<GalleryItem>? Gallery { get; set; }
        }

        /// <summary>Modrinth 项目截图项。</summary>
        internal sealed class GalleryItem
        {
            /// <summary>展示用缩略图地址（约 350px 宽）。</summary>
            public string? Url { get; set; }

            [JsonPropertyName("raw_url")]
            public string? RawUrl { get; set; }

            public bool? Featured { get; set; }
            public string? Title { get; set; }
            public string? Description { get; set; }
        }

        /// <summary>
        /// 兼容 gallery 的两种形态：
        /// 搜索接口（/search）返回字符串数组，项目详情（/project/id）返回对象数组。
        /// </summary>
        internal sealed class GalleryConverter : JsonConverter<List<GalleryItem>>
        {
            public override List<GalleryItem> Read(ref Utf8JsonReader reader, Type typeToConvert,
                JsonSerializerOptions options)
            {
                var list = new List<GalleryItem>();
                if (reader.TokenType != JsonTokenType.StartArray) return list;

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) break;

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        // 搜索接口：直接是图片地址
                        list.Add(new GalleryItem { Url = reader.GetString() });
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        // 项目详情：完整对象
                        using var doc = JsonDocument.ParseValue(ref reader);
                        var root = doc.RootElement;
                        list.Add(new GalleryItem
                        {
                            Url = root.TryGetProperty("url", out var url) ? url.GetString() : null,
                            RawUrl = root.TryGetProperty("raw_url", out var raw) ? raw.GetString() : null,
                            Title = root.TryGetProperty("title", out var title) ? title.GetString() : null
                        });
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                return list;
            }

            public override void Write(Utf8JsonWriter writer, List<GalleryItem> value,
                JsonSerializerOptions options)
                => throw new NotSupportedException("截图列表仅用于反序列化");
        }

        internal sealed class ProjectVersion
        {
            public string? Id { get; set; }
            public string? Name { get; set; }

            [JsonPropertyName("version_number")]
            public string? VersionNumber { get; set; }

            public string? Changelog { get; set; }

            [JsonPropertyName("date_published")]
            public string? DatePublished { get; set; }

            [JsonPropertyName("game_versions")]
            public List<string>? GameVersions { get; set; }

            public List<string>? Loaders { get; set; }

            [JsonPropertyName("version_type")]
            public string? VersionType { get; set; }

            public List<VersionFile>? Files { get; set; }
        }

        internal sealed class VersionFile
        {
            public FileHashes? Hashes { get; set; }
            public string? Url { get; set; }

            [JsonPropertyName("filename")]
            public string? FileName { get; set; }

            public bool? Primary { get; set; }
            public long? Size { get; set; }
        }

        internal sealed class FileHashes
        {
            public string? Sha1 { get; set; }
            public string? Sha512 { get; set; }
        }

        /// <summary>分类标签（/tag/category）。</summary>
        internal sealed class TagItem
        {
            public string Name { get; set; } = "";

            [JsonPropertyName("project_type")]
            public string ProjectType { get; set; } = "";
        }
    }
}
