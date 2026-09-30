using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace App1.Models
{
    /// <summary>
    /// Mojang 原始版本 JSON 模型（支持 inheritsFrom 继承链，供 Forge / NeoForge 等使用）。
    /// </summary>
    public sealed class RawVersionJson
    {
        public string Id { get; set; } = "";
        public string? InheritsFrom { get; set; }
        public string? MainClass { get; set; }
        public string? MinecraftArguments { get; set; }
        public string? Type { get; set; }
        public string? Jar { get; set; }
        public string? Assets { get; set; }
        public VersionArguments? Arguments { get; set; }
        public List<RawLibrary>? Libraries { get; set; }
        public RawAssetIndex? AssetIndex { get; set; }
        public RawDownloads? Downloads { get; set; }
        public JavaVersionInfo? JavaVersion { get; set; }
        public int? ComplianceLevel { get; set; }
        public string? Time { get; set; }
        public string? ReleaseTime { get; set; }
    }

    public sealed class VersionArguments
    {
        public List<LaunchArgument>? Game { get; set; }
        public List<LaunchArgument>? Jvm { get; set; }
    }

    /// <summary>
    /// 参数项。Mojang 的版本 JSON 中该数组是"字符串"与"带规则的 { rules, value } 对象"混排的，
    /// 因此必须使用自定义转换器同时支持两种形式。
    /// </summary>
    [JsonConverter(typeof(LaunchArgumentConverter))]
    public sealed class LaunchArgument
    {
        /// <summary>字符串形式的参数（最常见）。</summary>
        public string? RawValue { get; set; }

        public List<LaunchRule>? Rules { get; set; }

        /// <summary>对象形式的参数值，可以是字符串或字符串数组。</summary>
        public JsonElement Value { get; set; }

        public List<string> ResolveValues()
        {
            var list = new List<string>();

            if (!string.IsNullOrEmpty(RawValue)) list.Add(RawValue!);

            if (Value.ValueKind == JsonValueKind.String)
            {
                var s = Value.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            else if (Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in Value.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrEmpty(s)) list.Add(s);
                    }
                    else
                    {
                        list.Add(el.ToString());
                    }
                }
            }
            return list;
        }

        public bool HasFeatureFlags => Rules is not null && Rules.Exists(r => r.Features is not null);
    }

    /// <summary>
    /// 同时支持 "字符串" 与 "{ rules, value }" 两种参数形式的转换器。
    /// </summary>
    public sealed class LaunchArgumentConverter : JsonConverter<LaunchArgument>
    {
        public override LaunchArgument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return new LaunchArgument { RawValue = reader.GetString() };

                case JsonTokenType.StartObject:
                {
                    using var document = JsonDocument.ParseValue(ref reader);
                    var root = document.RootElement;
                    var argument = new LaunchArgument();

                    if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                    {
                        argument.Rules = rules.Deserialize<List<LaunchRule>>(options);
                    }
                    if (root.TryGetProperty("value", out var value))
                    {
                        argument.Value = value.Clone();
                    }
                    return argument;
                }

                case JsonTokenType.Null:
                    return new LaunchArgument();

                default:
                    throw new JsonException($"无法解析启动参数（意外的标记：{reader.TokenType}）");
            }
        }

        public override void Write(Utf8JsonWriter writer, LaunchArgument value, JsonSerializerOptions options)
        {
            // 纯字符串参数按原样写回
            if (value.Rules is null or { Count: 0 } && value.Value.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteStringValue(value.RawValue ?? "");
                return;
            }

            writer.WriteStartObject();
            if (value.Rules is { Count: > 0 })
            {
                writer.WritePropertyName("rules");
                JsonSerializer.Serialize(writer, value.Rules, options);
            }
            if (value.Value.ValueKind != JsonValueKind.Undefined)
            {
                writer.WritePropertyName("value");
                value.Value.WriteTo(writer);
            }
            else if (value.RawValue is not null)
            {
                writer.WriteString("value", value.RawValue);
            }
            writer.WriteEndObject();
        }
    }

    public sealed class LaunchRule
    {
        public string? Action { get; set; }
        public RuleOs? Os { get; set; }
        public RuleFeatures? Features { get; set; }
    }

    public sealed class RuleOs
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Arch { get; set; }
    }

    /// <summary>
    /// 参数规则中的功能开关。
    /// 注意：Mojang 使用 snake_case（如 is_demo_user），必须显式映射，
    /// 否则字段会静默丢失并导致 --demo 等条件参数被错误启用。
    /// </summary>
    public sealed class RuleFeatures
    {
        [JsonPropertyName("is_demo_user")]
        public bool? IsDemoUser { get; set; }

        [JsonPropertyName("has_custom_resolution")]
        public bool? HasCustomResolution { get; set; }

        [JsonPropertyName("has_quick_plays_support")]
        public bool? HasQuickPlaysSupport { get; set; }

        [JsonPropertyName("is_quick_play_singleplayer")]
        public bool? IsQuickPlaySingleplayer { get; set; }

        [JsonPropertyName("is_quick_play_multiplayer")]
        public bool? IsQuickPlayMultiplayer { get; set; }

        [JsonPropertyName("is_quick_play_realms")]
        public bool? IsQuickPlayRealms { get; set; }
    }

    public sealed class RawLibrary
    {
        public string Name { get; set; } = "";
        public string? Url { get; set; }
        public LibraryDownloads? Downloads { get; set; }
        public Dictionary<string, string>? Natives { get; set; }
        public List<LaunchRule>? Rules { get; set; }
        public ExtractRule? Extract { get; set; }
        public string? Sha1 { get; set; }
        public long? Size { get; set; }
        public bool? Clientreq { get; set; }
        public string? Md5 { get; set; }
    }

    public sealed class LibraryDownloads
    {
        public DownloadArtifact? Artifact { get; set; }
        public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
    }

    public sealed class DownloadArtifact
    {
        public string? Path { get; set; }
        public string? Url { get; set; }
        public string? Sha1 { get; set; }
        public long? Size { get; set; }
    }

    public sealed class ExtractRule
    {
        public List<string>? Exclude { get; set; }
        public List<string>? Include { get; set; }
    }

    public sealed class RawAssetIndex
    {
        public string? Id { get; set; }
        public string? Sha1 { get; set; }
        public string? Url { get; set; }
        public long? Size { get; set; }
        public long? TotalSize { get; set; }
        public bool? MapToResources { get; set; }
        public bool? Virtual { get; set; }
    }

    public sealed class RawDownloads
    {
        public DownloadArtifact? Client { get; set; }

        [JsonPropertyName("client_mappings")]
        public DownloadArtifact? ClientMappings { get; set; }

        public DownloadArtifact? Server { get; set; }
    }

    public sealed class JavaVersionInfo
    {
        public string? Component { get; set; }
        public int? MajorVersion { get; set; }
    }

    /// <summary>
    /// Mojang 版本清单条目。
    /// </summary>
    public sealed class VersionManifestEntry
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Time { get; set; }
        public string? ReleaseTime { get; set; }
        public string? Sha1 { get; set; }
        public int? ComplianceLevel { get; set; }

        /// <summary>版本类型中文名称。</summary>
        [JsonIgnore]
        public string TypeText => Type switch
        {
            "release" => "正式版",
            "snapshot" => "快照",
            "old_beta" => "远古 Beta",
            "old_alpha" => "远古 Alpha",
            _ => Type
        };

        /// <summary>发布时间（列表展示用）。</summary>
        [JsonIgnore]
        public string ReleaseDateText =>
            DateTime.TryParse(ReleaseTime ?? Time, out var date) ? date.ToString("yyyy-MM-dd") : "";
    }

    public sealed class VersionManifest
    {
        public List<VersionManifestEntry> Versions { get; set; } = new();
        public ManifestLatest? Latest { get; set; }
    }

    public sealed class ManifestLatest
    {
        public string? Release { get; set; }
        public string? Snapshot { get; set; }
    }
}
