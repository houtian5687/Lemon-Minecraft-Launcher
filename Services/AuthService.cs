using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// 账户系统：微软正版（OAuth2 设备代码流 → Xbox Live → Minecraft 服务）与离线模式。
    /// </summary>
    public sealed class AuthService
    {
        private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        private const string XboxAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
        private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
        private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
        private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
        private const string McEntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";

        public ObservableCollection<Account> Accounts { get; } = new();

        private static string StoreFile => Path.Combine(AppPaths.Root, "accounts.json");

        public void Load()
        {
            var list = Json.DeserializeFromFile<List<Account>>(StoreFile) ?? new List<Account>();
            Accounts.Clear();
            foreach (var account in list) Accounts.Add(account);
            Logger.Info($"已加载 {Accounts.Count} 个账户");
        }

        public void Save() => Json.SerializeToFile(StoreFile, Accounts.ToList());

        public Account? Active => Accounts.FirstOrDefault(a => a.IsActive);

        public void SetActive(Account? account)
        {
            foreach (var a in Accounts) a.IsActive = false;
            if (account is not null) account.IsActive = true;
            Save();
        }

        public void Remove(Account account)
        {
            UiDispatcher.Run(() => Accounts.Remove(account));
            Save();
        }

        // ---------------- 离线账户 ----------------

        public Account CreateOffline(string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerName)) throw new ArgumentException("玩家名不能为空", nameof(playerName));
            var account = new Account
            {
                Type = AccountType.Offline,
                PlayerName = playerName.Trim(),
                Uuid = OfflineUuid(playerName.Trim()),
                OwnsGame = false
            };
            account.AccessToken = Guid.NewGuid().ToString("N");
            UiDispatcher.Run(() => Accounts.Add(account));
            Save();
            return account;
        }

        /// <summary>离线 UUID：与 Minecraft 服务端兼容的 v3 派生算法。</summary>
        public static string OfflineUuid(string playerName)
        {
            var data = System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName);
            var hash = System.Security.Cryptography.MD5.HashData(data);
            hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
        }

        // ---------------- 微软账户 ----------------

        /// <summary>
        /// 发起设备代码登录，返回需要用户在浏览器中输入的代码。
        /// </summary>
        /// <summary>
        /// 微软登录使用的客户端 ID：由 AppConstants 内置（可通过环境变量覆盖），
        /// 不再向用户暴露配置入口。
        /// </summary>
        private static string ClientId => AppConstants.MicrosoftClientId;

        public async Task<DeviceCodeSession> BeginMicrosoftLoginAsync(CancellationToken ct = default)
        {
            var clientId = ClientId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new IOException(
                    "尚未配置微软登录所需的客户端 ID。\n" +
                    "请在「设置 → 高级选项」中填入你自己注册的 Azure 应用客户端 ID" +
                    "（账户类型需包含个人 Microsoft 账户，并启用「允许公共客户端流」）；" +
                    "若暂时不需要正版登录，可直接添加离线账户。");
            }

            var form = new List<KeyValuePair<string, string>>
            {
                new("client_id", clientId),
                new("scope", "XboxLive.signin offline_access")
            };
            using var content = new FormUrlEncodedContent(form);
            using var resp = await Http.Client.PostAsync(DeviceCodeUrl, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // 该阶段的失败几乎都源于 Azure 应用配置，需要给出可操作的指引
                var error = Json.Deserialize<OAuthErrorResponse>(text);
                Logger.Warn($"设备代码流初始化失败：{text}");
                throw new IOException(DescribeOAuthError(error));
            }

            var response = Json.Deserialize<DeviceCodeResponse>(text);
            if (response is null || string.IsNullOrEmpty(response.DeviceCode))
                throw new IOException("无法创建设备登录会话：服务器返回内容无法解析");

            return new DeviceCodeSession
            {
                DeviceCode = response.DeviceCode,
                UserCode = response.UserCode ?? "",
                VerificationUri = response.VerificationUri ?? "https://www.microsoft.com/link",
                ExpiresIn = response.ExpiresIn ?? 900,
                Interval = response.Interval ?? 5,
                Message = response.Message ?? ""
            };
        }

        /// <summary>
        /// 轮询设备代码登录结果，成功后返回账户。
        /// </summary>
        public async Task<Account> CompleteMicrosoftLoginAsync(DeviceCodeSession session,
            IProgress<string>? status = null, CancellationToken ct = default)
        {
            var clientId = ClientId;
            var deadline = DateTime.UtcNow.AddSeconds(session.ExpiresIn);
            string? accessToken = null;
            string? refreshToken = null;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var form = new List<KeyValuePair<string, string>>
                {
                    new("grant_type", "device_code"),
                    new("client_id", clientId),
                    new("device_code", session.DeviceCode)
                };
                using var content = new FormUrlEncodedContent(form);
                using var resp = await Http.Client.PostAsync(TokenUrl, content, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                var payload = Json.Deserialize<TokenResponse>(text);

                if (payload?.AccessToken is not null)
                {
                    accessToken = payload.AccessToken;
                    refreshToken = payload.RefreshToken;
                    break;
                }

                var error = payload?.Error;
                if (error is "authorization_pending")
                {
                    status?.Report("等待用户在浏览器中完成授权…");
                }
                else if (error is "authorization_declined")
                {
                    throw new IOException("用户拒绝了授权请求");
                }
                else if (error is "expired_token")
                {
                    throw new IOException("登录会话已过期，请重新发起登录");
                }
                else if (error is not null)
                {
                    throw new IOException("登录失败：" + (payload?.ErrorDescription ?? error));
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, session.Interval)), ct);
            }

            if (accessToken is null) throw new IOException("登录超时，请重试");

            status?.Report("正在向 Xbox Live 验证身份…");
            var xbl = await AuthenticateXboxAsync(accessToken, ct);
            status?.Report("正在获取 XSTS 令牌…");
            var xsts = await AuthorizeXstsAsync(xbl.Token, ct);
            status?.Report("正在登录 Minecraft 服务…");
            var mc = await LoginMinecraftAsync(xsts.UserHash, xsts.Token, ct);

            var account = new Account
            {
                Type = AccountType.Microsoft,
                PlayerName = mc.Name ?? "MinecraftPlayer",
                Uuid = mc.Id ?? "",
                ExpiresAt = DateTimeOffset.Now.AddSeconds(mc.ExpiresIn ?? 86400),
                SkinUrl = mc.SkinUrl
            };
            account.AccessToken = mc.AccessToken ?? "";
            account.RefreshToken = refreshToken ?? "";
            account.OwnsGame = await CheckEntitlementAsync(mc.AccessToken ?? "", ct);

            UiDispatcher.Run(() => Accounts.Add(account));
            Save();
            Logger.Info($"微软账户登录成功：{account.PlayerName}（拥有游戏：{account.OwnsGame}）");
            return account;
        }

        /// <summary>
        /// 使用刷新令牌续期微软账户。
        /// </summary>
        public async Task<bool> RefreshAsync(Account account, CancellationToken ct = default)
        {
            if (account.Type != AccountType.Microsoft) return false;
            var refreshToken = account.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken)) return false;

            try
            {
                var form = new List<KeyValuePair<string, string>>
                {
                    new("client_id", ClientId),
                    new("grant_type", "refresh_token"),
                    new("refresh_token", refreshToken),
                    new("scope", "XboxLive.signin offline_access")
                };
                var payload = await Http.PostFormJsonAsync<TokenResponse>(TokenUrl, form, ct);
                if (payload?.AccessToken is null) return false;

                var xbl = await AuthenticateXboxAsync(payload.AccessToken, ct);
                var xsts = await AuthorizeXstsAsync(xbl.Token, ct);
                var mc = await LoginMinecraftAsync(xsts.UserHash, xsts.Token, ct);

                account.AccessToken = mc.AccessToken ?? "";
                account.RefreshToken = payload.RefreshToken ?? refreshToken;
                account.ExpiresAt = DateTimeOffset.Now.AddSeconds(mc.ExpiresIn ?? 86400);
                account.PlayerName = mc.Name ?? account.PlayerName;
                account.Uuid = mc.Id ?? account.Uuid;
                account.OwnsGame = await CheckEntitlementAsync(mc.AccessToken ?? "", ct);
                Save();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"刷新账户失败 {account.PlayerName}：{ex.Message}");
                return false;
            }
        }

        /// <summary>确保账户令牌有效（过期自动刷新）。</summary>
        public async Task<bool> EnsureValidAsync(Account? account, CancellationToken ct = default)
        {
            if (account is null) return false;
            if (account.Type == AccountType.Offline) return true;
            if (!account.IsExpired) return true;
            return await RefreshAsync(account, ct);
        }

        private static async Task<(string Token, string UserHash)> AuthenticateXboxAsync(string accessToken, CancellationToken ct)
        {
            var payload = new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = "d=" + accessToken
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            };

            // Xbox Live 要求 PascalCase 字段名，不能用默认的 camelCase 选项
            var json = System.Text.Json.JsonSerializer.Serialize(payload, Json.XboxOptions);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.Client.PostAsync(XboxAuthUrl, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"Xbox Live 身份验证失败（HTTP {(int)resp.StatusCode}）：{text}");
                throw new IOException(DescribeXboxAuthError(resp.StatusCode, text));
            }

            var result = Json.Deserialize<XboxAuthResponse>(text)
                         ?? throw new IOException("Xbox Live 身份验证失败：服务器返回内容无法解析");
            var token = result.Token;
            var uhs = ExtractUserHash(result.DisplayClaims);
            if (string.IsNullOrEmpty(token)) throw new IOException("Xbox Live 令牌为空");
            return (token!, uhs);
        }

        /// <summary>
        /// 解析 user.auth.xboxlive.com 的错误响应，给出可操作的排查方向。
        /// </summary>
        private static string DescribeXboxAuthError(System.Net.HttpStatusCode status, string body)
        {
            var detail = string.IsNullOrWhiteSpace(body)
                ? "（无返回内容）"
                : body.Length > 300 ? body[..300] + "…" : body;

            return status switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    "Xbox Live 拒绝了微软账户令牌（HTTP 401）。请依次检查：\n" +
                    "1) Azure 应用是否已启用「允许公共客户端流」；\n" +
                    "2) 「支持的账户类型」是否包含个人 Microsoft 账户；\n" +
                    "3) 该微软账户是否访问过 xbox.com（部分账户需先建立 Xbox 档案）；\n" +
                    "4) 是否开启了代理 / VPN 干扰对 xboxlive.com 的访问。\n" +
                    $"服务器返回：{detail}",
                System.Net.HttpStatusCode.TooManyRequests =>
                    "Xbox Live 请求过于频繁（HTTP 429），请等待约 1 分钟后重试。",
                _ => $"Xbox Live 身份验证失败（HTTP {(int)status}）：{detail}"
            };
        }

        private static async Task<(string Token, string UserHash)> AuthorizeXstsAsync(string xblToken, CancellationToken ct)
        {
            var payload = new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { xblToken }
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            };

            // 同样必须使用 PascalCase 字段名
            var json = System.Text.Json.JsonSerializer.Serialize(payload, Json.XboxOptions);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.Client.PostAsync(XstsUrl, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // XSTS 失败会返回 XErr 错误码，是正版登录最常见的失败点
                var error = Json.Deserialize<XboxErrorResponse>(text);
                Logger.Warn($"XSTS 授权失败（XErr {error?.XErr}）：{text}");
                throw new IOException(DescribeXstsError(error?.XErr));
            }

            var result = Json.Deserialize<XboxAuthResponse>(text)
                         ?? throw new IOException("XSTS 授权失败：服务器返回内容无法解析");
            var uhs = ExtractUserHash(result.DisplayClaims);
            var token = result.Token ?? "";
            if (string.IsNullOrEmpty(token)) throw new IOException("XSTS 令牌为空");
            return (token, uhs);
        }

        /// <summary>
        /// 将 Azure 返回的 AADSTS 错误翻译为可操作的中文提示。
        /// </summary>
        private static string DescribeOAuthError(OAuthErrorResponse? error)
        {
            var description = error?.ErrorDescription ?? "";

            if (description.Contains("AADSTS700016", StringComparison.OrdinalIgnoreCase))
                return "客户端 ID 无效：Azure 中不存在该应用，或它属于其他租户。请核对「设置 → 高级选项」中填写的 ID。";

            if (description.Contains("AADSTS7000218", StringComparison.OrdinalIgnoreCase)
                || description.Contains("AADSTS70002", StringComparison.OrdinalIgnoreCase))
                return "该 Azure 应用未启用「允许公共客户端流」。请到 Azure 门户 → 应用注册 → 身份验证 → " +
                       "最下方「高级设置」，将「允许公共客户端流」设为「是」后重试。";

            if (description.Contains("AADSTS50059", StringComparison.OrdinalIgnoreCase))
                return "该 Azure 应用不支持个人 Microsoft 账户登录。请到「支持的账户类型」中改为包含个人账户的选项。";

            if (description.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase)
                || description.Contains("AADSTS65004", StringComparison.OrdinalIgnoreCase))
                return "该应用无权请求 XboxLive.signin 权限。请确认应用已启用公共客户端流，且账户类型支持个人 Microsoft 账户。";

            if (description.Contains("AADSTS500011", StringComparison.OrdinalIgnoreCase)
                || description.Contains("AADSTS900971", StringComparison.OrdinalIgnoreCase))
                return "Azure 应用未关联 Xbox Live 服务主体，请确认应用的「支持的账户类型」包含个人 Microsoft 账户。";

            var code = error?.Error ?? "unknown_error";
            return string.IsNullOrWhiteSpace(description)
                ? $"微软登录服务返回错误（{code}）"
                : $"微软登录失败（{code}）：{description}";
        }

        /// <summary>
        /// 将 Xbox Live 的 XErr 错误码翻译为可操作的中文提示。
        /// </summary>
        private static string DescribeXstsError(long? xerr) => xerr switch
        {
            2148916227 => "该微软账户已被 Xbox Live 封禁，无法用于正版登录。",
            2148916233 => "该微软账户还没有 Xbox 档案。请先访问 xbox.com 用同一账户登录并创建档案，然后重试。",
            2148916235 => "当前国家 / 地区不支持 Xbox Live，无法使用正版登录。",
            2148916236 or 2148916237 => "该账户需要通过年龄验证（家长同意）后才能登录。",
            2148916238 => "该账户未满 18 岁，需由成年成员将其加入 Microsoft 家庭组后再试。",
            2148916239 => "该账户受到 Xbox Live 访问限制，请到 account.microsoft.com 检查账户状态。",
            null or 0 => "XSTS 授权失败，请稍后重试。",
            _ => $"XSTS 授权被拒绝（XErr {xerr}），请检查该微软账户的 Xbox 服务状态。"
        };

        /// <summary>
        /// 从 Xbox 声明中提取用户哈希（兼容对象与数组两种格式）。
        /// </summary>
        private static string ExtractUserHash(XboxDisplayClaims? claims)
        {
            if (claims?.Xdi is null) return "";
            foreach (var element in claims.Xdi.Values)
            {
                try
                {
                    if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in element.EnumerateArray())
                        {
                            if (item.TryGetProperty("uhs", out var v)) return v.GetString() ?? "";
                        }
                    }
                    else if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (element.TryGetProperty("uhs", out var v)) return v.GetString() ?? "";
                    }
                }
                catch { }
            }
            return "";
        }

        private static async Task<MinecraftAuthResponse> LoginMinecraftAsync(string userHash, string xstsToken, CancellationToken ct)
        {
            // Minecraft 服务要求 camelCase 字段名（identityToken），与 Xbox Live 相反
            var payload = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };
            var json = System.Text.Json.JsonSerializer.Serialize(payload, Json.Options);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var loginResp = await Http.Client.PostAsync(McLoginUrl, content, ct);
            var text = await loginResp.Content.ReadAsStringAsync(ct);

            if (!loginResp.IsSuccessStatusCode)
            {
                Logger.Warn($"Minecraft 服务登录失败（HTTP {(int)loginResp.StatusCode}）：{text}");
                if (text.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("该 Azure 客户端 ID 未被 Minecraft 服务认可。" +
                                          "请确认应用「支持的账户类型」包含个人 Microsoft 账户，并已启用「允许公共客户端流」。");
                if (loginResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw new IOException("Minecraft 服务拒绝了该账户的登录令牌，请确认账户已购买 Java 版后重试。");
                if (loginResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    throw new IOException("该账户无权访问 Minecraft 服务（可能尚未购买 Java 版，或所在地区不可用）。");
                throw new IOException($"Minecraft 服务登录失败（HTTP {(int)loginResp.StatusCode}）");
            }

            var result = Json.Deserialize<MinecraftAuthResponse>(text)
                         ?? throw new IOException("Minecraft 服务返回内容无法解析");
            if (result.AccessToken is null) throw new IOException("Minecraft 访问令牌为空");

            // 拉取玩家资料（名称 / UUID / 皮肤）
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.AccessToken);
                using var resp = await Http.Client.SendAsync(request, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var profile = Json.Deserialize<MinecraftProfile>(await resp.Content.ReadAsStringAsync(ct));
                    if (profile is not null)
                    {
                        result.Name = profile.Name;
                        result.Id = profile.Id;
                        result.SkinUrl = profile.Skins?.FirstOrDefault(s => s.State == "ACTIVE")?.Url;
                    }
                }
                else
                {
                    Logger.Warn("未获取到 Minecraft 资料（HTTP " + (int)resp.StatusCode + "），该账户可能未购买游戏");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("获取玩家资料失败：" + ex.Message);
            }
            return result;
        }

        private static async Task<bool> CheckEntitlementAsync(string accessToken, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, McEntitlementsUrl);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                using var resp = await Http.Client.SendAsync(request, ct);
                if (!resp.IsSuccessStatusCode) return false;
                var json = await resp.Content.ReadAsStringAsync(ct);
                var payload = Json.Deserialize<EntitlementsResponse>(json);
                return payload?.Items?.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        // ---------------- DTO ----------------

        // 注意：微软接口返回 snake_case，必须显式映射

        /// <summary>Azure AD 错误响应（设备代码流初始化失败时返回）。</summary>
        internal sealed class OAuthErrorResponse
        {
            public string? Error { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("error_description")]
            public string? ErrorDescription { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("error_codes")]
            public List<long>? ErrorCodes { get; set; }
        }

        /// <summary>Xbox Live 错误响应（XSTS 授权失败时返回 XErr 错误码）。</summary>
        internal sealed class XboxErrorResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("XErr")]
            public long? XErr { get; set; }

            public string? Message { get; set; }
        }

        internal sealed class DeviceCodeResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("user_code")]
            public string? UserCode { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("device_code")]
            public string? DeviceCode { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("verification_uri")]
            public string? VerificationUri { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
            public int? ExpiresIn { get; set; }

            public int? Interval { get; set; }

            public string? Message { get; set; }
        }

        internal sealed class TokenResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("access_token")]
            public string? AccessToken { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
            public string? RefreshToken { get; set; }

            public string? Error { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("error_description")]
            public string? ErrorDescription { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
            public int? ExpiresIn { get; set; }
        }

        internal sealed class XboxAuthResponse
        {
            public string? Token { get; set; }
            public XboxDisplayClaims? DisplayClaims { get; set; }
        }

        internal sealed class XboxDisplayClaims
        {
            public Dictionary<string, System.Text.Json.JsonElement>? Xdi { get; set; }
        }

        internal sealed class MinecraftAuthResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("access_token")]
            public string? AccessToken { get; set; }

            public string? Username { get; set; }
            public string? Name { get; set; }
            public string? Id { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
            public int? ExpiresIn { get; set; }

            public string? SkinUrl { get; set; }
        }

        internal sealed class MinecraftProfile
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public List<MinecraftSkin>? Skins { get; set; }
        }

        internal sealed class MinecraftSkin
        {
            public string? Url { get; set; }
            public string? State { get; set; }
        }

        internal sealed class EntitlementsResponse
        {
            public List<object>? Items { get; set; }
        }
    }
}
