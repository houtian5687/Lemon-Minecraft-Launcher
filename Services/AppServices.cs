using System;
using App1.Core;

namespace App1.Services
{
    /// <summary>
    /// 服务定位器与全局初始化。
    /// </summary>
    public static class AppServices
    {
        public static SettingsService Settings { get; } = new();
        public static DownloadService Downloads { get; } = new();
        public static JavaService Java { get; } = new();
        public static VersionCatalogService Catalog { get; } = new();
        public static GameInstallService Installer { get; } = new();
        public static LoaderService Loaders { get; } = new();
        public static InstanceService Instances { get; } = new();
        public static AuthService Auth { get; } = new();
        public static ModrinthService Modrinth { get; } = new();
        public static CurseForgeService CurseForge { get; } = new();
        public static ResourceInstallService Resources { get; } = new();
        public static LaunchService Launcher { get; } = new();
        public static CrashAnalysisService CrashAnalyzer { get; } = new();
        public static UpdateService Updater { get; } = new();

        /// <summary>全局错误提示（由主窗口订阅后弹出 InfoBar）。</summary>
        public static event Action<string, string?>? ErrorRaised;

        /// <summary>全局信息提示。</summary>
        public static event Action<string>? InfoRaised;

        public static void NotifyError(string message, string? detail = null)
        {
            Logger.Error(message + (detail is null ? "" : " - " + detail));
            UiDispatcher.Run(() => ErrorRaised?.Invoke(message, detail));
        }

        public static void NotifyInfo(string message)
        {
            Logger.Info(message);
            UiDispatcher.Run(() => InfoRaised?.Invoke(message));
        }

        public static void Initialize()
        {
            AppPaths.EnsureAll();
            Settings.Load();
            AppPaths.EnsureAll();
            Downloads.Configure(
                Settings.Settings.MaxConcurrentDownloads,
                Settings.Settings.ChunkCount,
                Settings.Settings.ChunkThresholdMb * 1024 * 1024,
                Settings.Settings.SpeedLimitKb * 1024);
            Instances.Load();
            Auth.Load();
            Logger.Info($"{AppConstants.LauncherName} 初始化完成，游戏目录：{AppPaths.Root}");
        }

        public static void Shutdown()
        {
            try
            {
                Settings.Save();
                Instances.Save();
                Auth.Save();
            }
            catch (Exception ex)
            {
                Logger.Error("退出时保存状态失败", ex);
            }
        }
    }
}
