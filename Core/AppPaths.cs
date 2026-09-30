using System;
using System.IO;

namespace App1.Core
{
    /// <summary>
    /// 启动器的目录布局。游戏根目录可在设置中修改（修改后重启生效）。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>旧版本使用的数据目录名（用于自动迁移）。</summary>
        private const string LegacyAppDataFolder = "NovaLauncher";

        public static readonly string AppDataRoot =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppConstants.LauncherShortName);

        static AppPaths() => MigrateLegacyAppData();

        /// <summary>
        /// 旧品牌数据目录迁移：在首次运行新版时把整个目录搬到新位置，
        /// 保证设置、账户与已下载的游戏文件不丢失。
        /// </summary>
        private static void MigrateLegacyAppData()
        {
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var legacy = Path.Combine(local, LegacyAppDataFolder);
                var current = Path.Combine(local, AppConstants.LauncherShortName);

                if (Directory.Exists(legacy) && !Directory.Exists(current))
                {
                    Directory.Move(legacy, current);
                }
            }
            catch
            {
                // 迁移失败（如目录被占用）时按新目录继续运行，不影响启动
            }
        }

        public static string Root { get; set; } = Path.Combine(AppDataRoot, "game");

        public static string SettingsFile => Path.Combine(AppDataRoot, "settings.json");

        public static string InstancesRoot => Path.Combine(Root, "instances");
        public static string VersionsRoot => Path.Combine(Root, "versions");
        public static string LibrariesRoot => Path.Combine(Root, "libraries");
        public static string AssetsRoot => Path.Combine(Root, "assets");
        public static string AssetsObjectsRoot => Path.Combine(AssetsRoot, "objects");
        public static string AssetsIndexesRoot => Path.Combine(AssetsRoot, "indexes");
        public static string ResourcesRoot => Path.Combine(Root, "resources");
        public static string RuntimeRoot => Path.Combine(Root, "runtime");
        public static string JavaRoot => Path.Combine(RuntimeRoot, "java");
        public static string TempRoot => Path.Combine(Root, "temp");
        public static string CacheRoot => Path.Combine(Root, "cache");
        public static string CrashLogsRoot => Path.Combine(Root, "crash-logs");
        public static string GameLogsRoot => Path.Combine(Root, "game-logs");
        public static string PatchRoot => Path.Combine(Root, "patches");

        public static string InstanceDir(string instanceId) => Path.Combine(InstancesRoot, instanceId);
        public static string VersionDir(string versionId) => Path.Combine(VersionsRoot, versionId);
        public static string VersionJson(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".json");
        public static string SpecFile(string versionId) => Path.Combine(VersionDir(versionId), "lemon-spec.json");
        public static string ClientJar(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".jar");

        public static void EnsureAll()
        {
            foreach (var dir in new[]
                     {
                         Root, InstancesRoot, VersionsRoot, LibrariesRoot, AssetsObjectsRoot, AssetsIndexesRoot,
                         ResourcesRoot, JavaRoot, TempRoot, CacheRoot, CrashLogsRoot, GameLogsRoot, PatchRoot
                     })
            {
                Directory.CreateDirectory(dir);
            }
        }
    }
}
