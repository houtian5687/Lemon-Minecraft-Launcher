using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace App1.Core
{
    /// <summary>
    /// 统一的 JSON 序列化配置与辅助方法。
    /// </summary>
    public static class Json
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true
        };

        /// <summary>
        /// Xbox Live 服务专用序列化选项。
        /// 该服务要求 PascalCase 字段名（Properties / AuthMethod / SiteName / RpsTicket /
        /// RelyingParty / TokenType），而 Minecraft 服务要求 camelCase（identityToken），
        /// 两者不可共用同一套选项，否则请求会被服务端直接拒绝。
        /// </summary>
        public static readonly JsonSerializerOptions XboxOptions = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static readonly JsonSerializerOptions Lenient = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static string Serialize<T>(T value)
            => JsonSerializer.Serialize(value, Options);

        public static void SerializeToFile<T>(string path, T value)
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
        }

        public static T? Deserialize<T>(string json)
            => JsonSerializer.Deserialize<T>(json, Lenient);

        public static T? DeserializeFromFile<T>(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return default;
                return JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(path), Lenient);
            }
            catch (Exception ex)
            {
                Logger.Warn($"读取 JSON 失败：{path} - {ex.Message}");
                return default;
            }
        }

        public static async Task SerializeToFileAsync<T>(string path, T value, CancellationToken ct = default)
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
            await JsonSerializer.SerializeAsync(fs, value, Options, ct);
        }

        public static async Task<T?> DeserializeFromFileAsync<T>(string path, CancellationToken ct = default)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return default;
                using var fs = System.IO.File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<T>(fs, Lenient, ct);
            }
            catch (Exception ex)
            {
                Logger.Warn($"读取 JSON 失败：{path} - {ex.Message}");
                return default;
            }
        }

        /// <summary>
        /// 安全读取 JsonElement 的字符串属性。
        /// </summary>
        public static string? Str(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        public static long? Long(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64()
                : null;

        public static bool Bool(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
