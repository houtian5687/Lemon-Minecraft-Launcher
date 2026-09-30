using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// 安装进度汇报。
    /// </summary>
    public sealed class InstallProgress
    {
        public string Phase { get; set; } = "";
        public string? Detail { get; set; }
        public double Percent { get; set; }
        public int Current { get; set; }
        public int Total { get; set; }

        public string Text => Total > 0 ? $"{Phase}（{Current}/{Total}）" : Phase;
    }

    /// <summary>
    /// 版本清单、版本 JSON 获取与继承链解析。
    /// </summary>
    public sealed class VersionCatalogService
    {
        private const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        private const string MirrorManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
        private const string OfficialLibraries = "https://libraries.minecraft.net/";
        private const string MirrorLibraries = "https://bmclapi2.bangbang93.com/maven/";
        private const string OfficialAssets = "https://resources.download.minecraft.net/";
        private const string MirrorAssets = "https://bmclapi2.bangbang93.com/assets/";

        private VersionManifest? _manifest;
        private readonly Dictionary<string, RawVersionJson> _rawCache = new(StringComparer.OrdinalIgnoreCase);

        public string ManifestUrl => UseMirror ? MirrorManifest : OfficialManifest;
        public string LibrariesBase => UseMirror ? MirrorLibraries : OfficialLibraries;
        public string AssetsBase => UseMirror ? MirrorAssets : OfficialAssets;

        private static bool UseMirror => AppServices.Settings.Settings.UseBmclApiMirror;

        // ---------------- 版本清单 ----------------

        public async Task<List<VersionManifestEntry>> GetVersionListAsync(bool includeSnapshots = false,
            bool includeOld = true, bool forceRefresh = false, CancellationToken ct = default)
        {
            if (_manifest is null || forceRefresh)
            {
                var cached = Path.Combine(AppPaths.CacheRoot, "version_manifest.json");
                if (!forceRefresh && File.Exists(cached) &&
                    DateTime.Now - File.GetLastWriteTime(cached) < TimeSpan.FromHours(1))
                {
                    _manifest = Json.DeserializeFromFile<VersionManifest>(cached);
                }
                if (_manifest is null)
                {
                    _manifest = await Http.GetJsonAsync<VersionManifest>(ManifestUrl, ct)
                                ?? throw new IOException("无法获取版本清单，请检查网络连接");
                    Directory.CreateDirectory(AppPaths.CacheRoot);
                    Json.SerializeToFile(cached, _manifest);
                }
            }

            var query = _manifest!.Versions.AsEnumerable();
            if (!includeSnapshots) query = query.Where(v => v.Type == "release");
            if (!includeOld) query = query.Where(v => IsModern(v.Id));
            return query.OrderByDescending(v => v.ReleaseTime ?? v.Time).ToList();
        }

        /// <summary>判断是否为 1.13+ 的"现代"版本。</summary>
        public static bool IsModern(string id)
        {
            var parts = id.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return false;
            return major > 1 || minor >= 13;
        }

        // ---------------- 版本 JSON ----------------

        public async Task<RawVersionJson?> DownloadVersionJsonAsync(string versionId, string? url = null,
            CancellationToken ct = default)
        {
            var target = AppPaths.VersionJson(versionId);
            url ??= _manifest?.Versions.FirstOrDefault(v => v.Id == versionId)?.Url ?? OfficialVersionUrl(versionId);
            if (UseMirror) url = $"https://bmclapi2.bangbang93.com/version/{versionId}/json";

            var raw = await Http.GetJsonAsync<RawVersionJson>(url, ct);
            if (raw is null) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Json.SerializeToFile(target, raw);
            _rawCache[versionId] = raw;
            return raw;
        }

        private static string OfficialVersionUrl(string versionId)
            => $"https://piston-meta.mojang.com/v1/packages/{Hash(versionId)}/{versionId}.json";

        private static string Hash(string versionId)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                "https://piston-meta.mojang.com/v1/objects/" + versionId))).ToLowerInvariant();
        }

        public async Task<RawVersionJson?> LoadRawAsync(string versionId, bool downloadIfMissing = true,
            CancellationToken ct = default)
        {
            if (_rawCache.TryGetValue(versionId, out var cached)) return cached;
            var file = AppPaths.VersionJson(versionId);
            if (File.Exists(file))
            {
                var raw = Json.DeserializeFromFile<RawVersionJson>(file);
                if (raw is not null)
                {
                    _rawCache[versionId] = raw;
                    return raw;
                }
            }
            return downloadIfMissing ? await DownloadVersionJsonAsync(versionId, null, ct) : null;
        }

        public void SaveRaw(RawVersionJson raw, string? versionId = null)
        {
            var id = versionId ?? raw.Id;
            Directory.CreateDirectory(AppPaths.VersionDir(id));
            Json.SerializeToFile(AppPaths.VersionJson(id), raw);
            _rawCache[id] = raw;
        }

        // ---------------- 继承链解析 ----------------

        /// <summary>
        /// 解析版本（含 inheritsFrom 继承链）为启动器可直接使用的规格，并缓存为 lemon-spec.json。
        /// </summary>
        public async Task<GameVersionSpec> ResolveAsync(string versionId, CancellationToken ct = default)
        {
            var chain = new List<RawVersionJson>();
            var current = await LoadRawAsync(versionId, true, ct)
                          ?? throw new IOException($"找不到版本 {versionId} 的元数据");
            var guard = 0;
            while (current is not null && guard++ < 16)
            {
                chain.Add(current);
                if (string.IsNullOrWhiteSpace(current.InheritsFrom)) break;
                var parent = await LoadRawAsync(current.InheritsFrom!, true, ct);
                if (parent is null) break;
                current = parent;
            }
            chain.Reverse();

            var spec = new GameVersionSpec
            {
                Id = versionId,
                InheritedId = chain[0].Id
            };

            var libraries = new List<RawLibrary>();
            foreach (var raw in chain)
            {
                if (!string.IsNullOrEmpty(raw.MainClass)) spec.MainClass = raw.MainClass!;
                if (!string.IsNullOrEmpty(raw.MinecraftArguments)) spec.MinecraftArguments = raw.MinecraftArguments;
                if (raw.Arguments?.Jvm is { } jvm)
                    spec.JvmArguments.AddRange(jvm.Select(ToResolved));
                if (raw.Arguments?.Game is { } game)
                    spec.GameArguments.AddRange(game.Select(ToResolved));
                if (raw.Libraries is { } libs) libraries.AddRange(libs);
                if (raw.AssetIndex is { } idx)
                {
                    spec.AssetIndex = new AssetIndexInfo
                    {
                        Id = idx.Id ?? raw.Assets ?? raw.Id,
                        Url = idx.Url ?? "",
                        Sha1 = idx.Sha1,
                        Size = idx.Size,
                        TotalSize = idx.TotalSize
                    };
                    spec.MapToResources = idx.MapToResources ?? false;
                    spec.UseVirtualAssets = idx.Virtual ?? false;
                }
                if (!string.IsNullOrEmpty(raw.Assets)) spec.LegacyAssetsId = raw.Assets;
                if (raw.JavaVersion?.MajorVersion is { } jv) spec.JavaMajorVersion = jv;
                if (!string.IsNullOrEmpty(raw.ReleaseTime)) spec.ReleaseTime = raw.ReleaseTime;
                if (raw.Downloads?.Client is { } client) spec.ClientJar = ToArtifact(client, raw);
            }

            if (string.IsNullOrEmpty(spec.ClientJar.Url))
            {
                // 使用继承链中某个版本的 client jar（jar 字段或最底层版本）
                var jarId = chain.Last().Jar ?? spec.InheritedId;
                var jarRaw = await LoadRawAsync(jarId, true, ct);
                if (jarRaw?.Downloads?.Client is { } c2) spec.ClientJar = ToArtifact(c2, jarRaw);
            }

            if (UseMirror && spec.ClientJar.Url.StartsWith("https://piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                // 保留官方地址作为兜底：镜像限流时可自动回退
                var official = spec.ClientJar.Url;
                spec.ClientJar.Url = $"https://bmclapi2.bangbang93.com/version/{spec.InheritedId}/client";
                spec.ClientJar.FallbackUrls ??= new List<string>();
                if (!spec.ClientJar.FallbackUrls.Contains(official)) spec.ClientJar.FallbackUrls.Add(official);
            }

            spec.Libraries = ResolveLibraries(libraries);

            Directory.CreateDirectory(AppPaths.VersionDir(versionId));
            Json.SerializeToFile(AppPaths.SpecFile(versionId), spec);
            Logger.Info($"版本解析完成：{versionId}（{spec.Libraries.Count} 个库，主类 {spec.MainClass}）");
            return spec;
        }

        private static ResolvedArgument ToResolved(LaunchArgument arg)
            => new() { Values = arg.ResolveValues(), Rules = arg.Rules };

        private ResolvedArtifact ToArtifact(DownloadArtifact artifact, RawVersionJson raw)
        {
            var url = artifact.Url ?? "";
            List<string>? fallbacks = null;
            if (string.IsNullOrEmpty(url))
            {
                var path = artifact.Path ?? $"{raw.Id}/{raw.Id}.jar";
                url = LibrariesBase + path;
                fallbacks = BuildFallbacks(url);
            }
            else if (UseMirror && url.StartsWith("https://piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                // 切换镜像时保留官方地址作为兜底
                fallbacks = new List<string> { url };
                url = url.Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com");
            }
            else
            {
                fallbacks = BuildFallbacks(url);
            }
            return new ResolvedArtifact
            {
                Url = url,
                Sha1 = artifact.Sha1,
                Size = artifact.Size,
                Path = Path.Combine(AppPaths.VersionDir(raw.Id), raw.Id + ".jar"),
                FallbackUrls = fallbacks
            };
        }

        /// <summary>
        /// 应用平台规则、展开 natives 分类器，得到需要下载的库列表。
        /// </summary>
        public List<ResolvedLibrary> ResolveLibraries(IEnumerable<RawLibrary> libraries)
        {
            var result = new List<ResolvedLibrary>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lib in libraries)
            {
                if (!RuleMatches(lib.Rules)) continue;
                if (lib.Clientreq == false) continue;

                var coords = lib.Name.Split(':');
                if (coords.Length < 3) continue;
                var group = coords[0];
                var artifact = coords[1];
                var version = coords[2];
                var classifier = coords.Length > 3 ? coords[3] : null;
                if (classifier is not null && (classifier.Contains('$') || classifier == "sources")) continue;

                var basePath = group.Replace('.', '/') + "/" + artifact + "/" + version + "/" + artifact + "-" + version;
                var plainPath = basePath + (classifier is null ? "" : "-" + classifier) + ".jar";

                // 主 artifact
                var hasClassifiers = lib.Downloads?.Classifiers is { Count: > 0 };
                if (lib.Downloads?.Artifact is { } art)
                {
                    var path = art.Path ?? plainPath;
                    var url = !string.IsNullOrEmpty(art.Url)
                        ? MirrorUrl(art.Url!)
                        : LibrariesBase + path;
                    if (seen.Add(lib.Name))
                    {
                        result.Add(new ResolvedLibrary
                        {
                            Name = lib.Name,
                            Path = path,
                            Url = url,
                            Sha1 = art.Sha1,
                            Size = art.Size,
                            FallbackUrls = BuildFallbacks(url)
                        });
                    }
                }
                else if (classifier is null && !hasClassifiers)
                {
                    // 旧格式（未声明 downloads）：按约定路径尝试，文件可能并不存在于仓库
                    var relative = path2(group, artifact, version);
                    var url = !string.IsNullOrEmpty(lib.Url)
                        ? MirrorUrl(lib.Url!) + relative
                        : LibrariesBase + relative;
                    if (seen.Add(lib.Name))
                    {
                        result.Add(new ResolvedLibrary
                        {
                            Name = lib.Name,
                            Path = plainPath,
                            Url = url,
                            Sha1 = lib.Sha1,
                            Size = lib.Size,
                            IsOptional = true,
                            FallbackUrls = BuildFallbacks(url)
                        });
                    }
                }
                // 仅有平台分类器（classifiers）的库不存在主 jar，直接跳过，避免必然 404

                // natives（windows）
                var nativeKey = GetWindowsNativeKey(lib.Natives);
                if (nativeKey is null) continue;
                var nativeArtifact = lib.Downloads?.Classifiers?.TryGetValue(nativeKey, out var na) == true ? na : null;
                var nativePath = nativeArtifact?.Path ?? basePath + "-" + nativeKey + ".jar";
                var nativeUrl = !string.IsNullOrEmpty(nativeArtifact?.Url)
                    ? MirrorUrl(nativeArtifact!.Url!)
                    : LibrariesBase + nativePath;
                var key = lib.Name + ":" + nativeKey;
                if (seen.Add(key))
                {
                    result.Add(new ResolvedLibrary
                    {
                        Name = lib.Name + ":" + nativeKey,
                        Path = nativePath,
                        Url = nativeUrl,
                        Sha1 = nativeArtifact?.Sha1,
                        Size = nativeArtifact?.Size,
                        IsNative = true,
                        NativeClassifier = nativeKey,
                        ExtractExclude = lib.Extract?.Exclude,
                        FallbackUrls = BuildFallbacks(nativeUrl)
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// 为主下载地址生成备用地址（官方源 ↔ BMCLAPI 镜像双向互备）。
        /// 仅在主地址失败后才会被使用，因此不会额外消耗正常下载的请求配额。
        /// </summary>
        internal static List<string>? BuildFallbacks(string primaryUrl)
        {
            if (string.IsNullOrEmpty(primaryUrl)) return null;

            // 官方源 → 镜像
            var officialMap = new (string From, string To)[]
            {
                ("https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/maven/"),
                ("https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/"),
                ("https://piston-data.mojang.com/", "https://bmclapi2.bangbang93.com/"),
                ("https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/")
            };
            foreach (var (from, to) in officialMap)
            {
                if (primaryUrl.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                    return new List<string> { to + primaryUrl[from.Length..] };
            }

            // 镜像 → 官方源
            const string mirrorRoot = "https://bmclapi2.bangbang93.com/";
            if (primaryUrl.StartsWith(mirrorRoot, StringComparison.OrdinalIgnoreCase))
            {
                var rest = primaryUrl[mirrorRoot.Length..];
                if (rest.StartsWith("maven/", StringComparison.OrdinalIgnoreCase))
                    return new List<string> { "https://libraries.minecraft.net/" + rest["maven/".Length..] };
                if (rest.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                    return new List<string> { "https://resources.download.minecraft.net/" + rest["assets/".Length..] };
                return new List<string> { "https://piston-data.mojang.com/" + rest };
            }

            return null;
        }

        private static string path2(string group, string artifact, string version)
            => group.Replace('.', '/') + "/" + artifact + "/" + version + "/" + artifact + "-" + version + ".jar";

        private string MirrorUrl(string url)
        {
            if (!UseMirror) return url;
            if (url.StartsWith("https://libraries.minecraft.net/", StringComparison.OrdinalIgnoreCase))
                return MirrorLibraries + url["https://libraries.minecraft.net/".Length..];
            return url;
        }

        private static string? GetWindowsNativeKey(Dictionary<string, string>? natives)
        {
            if (natives is null) return null;
            if (!natives.TryGetValue("windows", out var key) || string.IsNullOrEmpty(key)) return null;
            return key.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
        }

        /// <summary>
        /// 计算规则是否适用于当前平台（库与参数的通用规则判定）。
        /// </summary>
        public static bool RuleMatches(List<LaunchRule>? rules)
        {
            if (rules is null || rules.Count == 0) return true;
            var allowed = false;
            foreach (var rule in rules)
            {
                var match = MatchesOs(rule.Os);
                var action = rule.Action ?? "allow";
                if (action.Equals("allow", StringComparison.OrdinalIgnoreCase))
                {
                    if (match) allowed = true;
                }
                else if (action.Equals("disallow", StringComparison.OrdinalIgnoreCase))
                {
                    if (match) return false;
                }
            }
            return allowed;
        }

        private static bool MatchesOs(RuleOs? os)
        {
            if (os is null) return true;
            if (!string.IsNullOrEmpty(os.Name) &&
                !os.Name.Equals("windows", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(os.Arch) && !LaunchService.MatchesArch(os.Arch)) return false;
            if (!string.IsNullOrEmpty(os.Version))
            {
                try
                {
                    var regex = new System.Text.RegularExpressions.Regex(os.Version);
                    var currentVersion = Environment.OSVersion.Version.ToString();
                    if (!regex.IsMatch(currentVersion)) return false;
                }
                catch { }
            }
            return true;
        }
    }
}
