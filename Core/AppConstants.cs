using System;

namespace App1.Core
{
    /// <summary>
    /// 启动器内置常量：品牌信息与第三方平台凭据。
    /// 这些值属于实现细节，不通过界面暴露给最终用户；
    /// 如需在开发 / 自建分发场景下覆盖，可通过环境变量注入。
    /// </summary>
    public static class AppConstants
    {
        /// <summary>产品完整名称（界面与清单中展示）。</summary>
        public const string LauncherName = "Lemon Minecraft Launcher";

        /// <summary>产品短名称（用于 JVM 参数、User-Agent、目录名等无空格场景）。</summary>
        public const string LauncherShortName = "LemonLauncher";

        public const string LauncherVersion = "1.0.0";

        public const string Homepage = "https://github.com/lemonlauncher";

        /// <summary>
        /// 增量更新清单地址。已内置到程序中，用户无需（也无法）配置；
        /// 如需在自建分发场景下覆盖，可通过环境变量 LEMON_UPDATE_URL 注入。
        /// </summary>
        private const string DefaultUpdateManifestUrl =
            Homepage + "/releases/latest/download/update.json";

        public static string UpdateManifestUrl =>
            ReadEnvironment(DefaultUpdateManifestUrl, "LEMON_UPDATE_URL", "NOVA_UPDATE_URL");

        public const string UserAgent = LauncherShortName + "/1.0 (+" + Homepage + ")";

        /// <summary>启动器窗口默认尺寸（物理像素）。</summary>
        public const int DefaultWindowWidth = 2212;
        public const int DefaultWindowHeight = 1436;

        /// <summary>
        /// 微软身份平台客户端 ID（Azure 应用注册）。
        ///
        /// 获取方式：portal.azure.com → 应用注册 → 新注册
        ///   1) 「支持的账户类型」必须包含「个人 Microsoft 账户」（代码使用 /consumers 端点）；
        ///   2) 注册后进入「身份验证」→ 拉到最下方「高级设置」，
        ///      将「允许公共客户端流」设为「是」（设备代码流属于公共客户端流）；
        ///   3) 设备代码流不需要配置重定向 URI，也不需要预先添加 API 权限
        ///      （XboxLive.signin 与 offline_access 在请求时动态指定）。
        /// 复制「概述」页的应用程序(客户端) ID 填入下方即可。
        ///
        /// 留空时正版登录不可用，但仍可使用离线账户；
        /// 也可通过环境变量 LEMON_MSA_CLIENT_ID 注入，或在「设置 → 高级选项」中临时填写。
        /// </summary>
        private const string DefaultMicrosoftClientId = "1cae9c21-b072-4e2a-bceb-d35f82670597";

        /// <summary>
        /// CurseForge 平台密钥（Eternal API Key）。
        /// 已内置，用户无需配置；如需覆盖可通过环境变量 LEMON_CURSEFORGE_KEY 注入。
        /// </summary>
        private const string DefaultCurseForgeApiKey =
            "$2a$10$mTKy7ppD/TF1A8n88AHq5OxQ9DOYKiYK3hohex1qkSgfR8xzU33Ze";

        public static string MicrosoftClientId =>
            ReadEnvironment(DefaultMicrosoftClientId, "LEMON_MSA_CLIENT_ID", "NOVA_MSA_CLIENT_ID");

        public static string CurseForgeApiKey =>
            ReadEnvironment(DefaultCurseForgeApiKey, "LEMON_CURSEFORGE_KEY", "NOVA_CURSEFORGE_KEY");

        public static bool HasCurseForgeKey => !string.IsNullOrWhiteSpace(CurseForgeApiKey);

        /// <summary>
        /// 依次读取环境变量，返回首个非空值（保留旧前缀以兼容既有部署脚本）。
        /// </summary>
        private static string ReadEnvironment(string fallback, params string[] names)
        {
            foreach (var name in names)
            {
                try
                {
                    var value = Environment.GetEnvironmentVariable(name);
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                catch
                {
                    // 忽略环境变量读取异常
                }
            }
            return fallback;
        }
    }
}
