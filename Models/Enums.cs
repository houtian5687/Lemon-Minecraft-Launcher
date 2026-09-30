namespace App1.Models
{
    /// <summary>
    /// 模组加载器类型。
    /// </summary>
    public enum LoaderType
    {
        Vanilla,
        Fabric,
        Quilt,
        Forge,
        NeoForge
    }

    /// <summary>
    /// 账户类型。
    /// </summary>
    public enum AccountType
    {
        Offline,
        Microsoft
    }

    /// <summary>
    /// 资源类型（对应下载平台的 project_type）。
    /// </summary>
    public enum ResourceType
    {
        Mod,
        Modpack,
        Shader,
        ResourcePack
    }

    /// <summary>
    /// 资源来源平台。
    /// </summary>
    public enum ResourcePlatform
    {
        Modrinth,
        CurseForge
    }

    /// <summary>
    /// 下载任务状态。
    /// </summary>
    public enum DownloadState
    {
        Queued,
        Downloading,
        Paused,
        Verifying,
        Completed,
        Failed,
        Canceled
    }

    /// <summary>
    /// 实例隔离级别。
    /// </summary>
    public enum IsolationLevel
    {
        /// <summary>共享版本、库与资源，仅隔离存档/模组/配置（默认，节省空间）。</summary>
        Shared,
        /// <summary>完全隔离：实例内自带 versions/libraries/assets 冗余副本。</summary>
        Full
    }

    /// <summary>
    /// 游戏窗口启动模式。
    /// </summary>
    public enum WindowMode
    {
        Default,
        Custom,
        Fullscreen
    }

    public static class EnumText
    {
        public static string ToText(this LoaderType type) => type switch
        {
            LoaderType.Vanilla => "原版",
            LoaderType.Fabric => "Fabric",
            LoaderType.Quilt => "Quilt",
            LoaderType.Forge => "Forge",
            LoaderType.NeoForge => "NeoForge",
            _ => "未知"
        };

        public static string ToText(this ResourceType type) => type switch
        {
            ResourceType.Mod => "模组",
            ResourceType.Modpack => "整合包",
            ResourceType.Shader => "光影",
            ResourceType.ResourcePack => "材质包",
            _ => "资源"
        };

        public static string ToText(this DownloadState state) => state switch
        {
            DownloadState.Queued => "排队中",
            DownloadState.Downloading => "下载中",
            DownloadState.Paused => "已暂停",
            DownloadState.Verifying => "校验中",
            DownloadState.Completed => "已完成",
            DownloadState.Failed => "失败",
            DownloadState.Canceled => "已取消",
            _ => "未知"
        };

        public static string ToText(this AccountType type) => type == AccountType.Microsoft ? "微软正版" : "离线模式";
    }
}
