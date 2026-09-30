using System;
using System.IO;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// 启动器全局配置（持久化到 settings.json）。
    /// </summary>
    public sealed class AppSettings : ObservableObject
    {
        private string _gameDirectory = AppPaths.Root;
        private string _theme = "Default";
        private int _defaultMinMemoryMb = 512;
        private int _defaultMaxMemoryMb = RecommendMaxMemory();
        private string? _javaPath;
        private bool _autoSelectJava = true;
        private int _maxConcurrentDownloads = 6;
        private int _chunkCount = 4;
        private long _chunkThresholdMb = 8;
        private long _speedLimitKb;
        private bool _autoCheckUpdate = true;
        private DateTime? _lastUpdateCheck;
        private bool _closeOnLaunch;
        private bool _showGameConsole = true;
        private bool _enableAnimations = true;
        private int _defaultWindowWidth = AppConstants.DefaultWindowWidth;
        private int _defaultWindowHeight = AppConstants.DefaultWindowHeight;
        private string? _lastInstanceId;
        private string? _lastAccountId;
        private bool _useBmclApiMirror;
        private int _maxLogLines = 4000;
        private bool _enableSkinPreview = true;
        private int _backgroundIndex = -1;

        /// <summary>依据物理内存推荐默认最大内存（约为物理内存的 1/4，限制在 1G~8G）。</summary>
        private static int RecommendMaxMemory()
        {
            try
            {
                var mb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
                return Math.Clamp(mb / 4, 1024, 8192);
            }
            catch
            {
                return 2048;
            }
        }

        public string GameDirectory
        {
            get => _gameDirectory;
            set => Set(ref _gameDirectory, value);
        }

        /// <summary>
        /// 启动页背景图索引。每次启动启动器时递增，实现「每次打开轮换一张」。
        /// </summary>
        public int BackgroundIndex
        {
            get => _backgroundIndex;
            set => Set(ref _backgroundIndex, value);
        }

        public string Theme
        {
            get => _theme;
            set => Set(ref _theme, value);
        }

        public int DefaultMinMemoryMb
        {
            get => _defaultMinMemoryMb;
            set => Set(ref _defaultMinMemoryMb, value);
        }

        public int DefaultMaxMemoryMb
        {
            get => _defaultMaxMemoryMb;
            set => Set(ref _defaultMaxMemoryMb, value);
        }

        public string? JavaPath
        {
            get => _javaPath;
            set => Set(ref _javaPath, value);
        }

        /// <summary>
        /// 用户手动添加的 Java 路径（可为 java.exe 或 Java 安装目录），
        /// 自动扫描失败时可通过该列表兜底。
        /// </summary>
        public System.Collections.Generic.List<string> ManualJavaPaths { get; set; } = new();

        public bool AutoSelectJava
        {
            get => _autoSelectJava;
            set => Set(ref _autoSelectJava, value);
        }

        public int MaxConcurrentDownloads
        {
            get => _maxConcurrentDownloads;
            set => Set(ref _maxConcurrentDownloads, Math.Clamp(value, 1, 32));
        }

        public int ChunkCount
        {
            get => _chunkCount;
            set => Set(ref _chunkCount, Math.Clamp(value, 1, 16));
        }

        public long ChunkThresholdMb
        {
            get => _chunkThresholdMb;
            set => Set(ref _chunkThresholdMb, Math.Clamp(value, 1, 1024));
        }

        /// <summary>下载限速，单位 KB/s，0 表示不限速。</summary>
        public long SpeedLimitKb
        {
            get => _speedLimitKb;
            set => Set(ref _speedLimitKb, Math.Max(0, value));
        }

        public bool AutoCheckUpdate
        {
            get => _autoCheckUpdate;
            set => Set(ref _autoCheckUpdate, value);
        }

        public DateTime? LastUpdateCheck
        {
            get => _lastUpdateCheck;
            set => Set(ref _lastUpdateCheck, value);
        }

        public bool CloseOnLaunch
        {
            get => _closeOnLaunch;
            set => Set(ref _closeOnLaunch, value);
        }

        public bool ShowGameConsole
        {
            get => _showGameConsole;
            set => Set(ref _showGameConsole, value);
        }

        public bool EnableAnimations
        {
            get => _enableAnimations;
            set => Set(ref _enableAnimations, value);
        }

        public int DefaultWindowWidth
        {
            get => _defaultWindowWidth;
            set => Set(ref _defaultWindowWidth, value);
        }

        public int DefaultWindowHeight
        {
            get => _defaultWindowHeight;
            set => Set(ref _defaultWindowHeight, value);
        }

        public string? LastInstanceId
        {
            get => _lastInstanceId;
            set => Set(ref _lastInstanceId, value);
        }

        public string? LastAccountId
        {
            get => _lastAccountId;
            set => Set(ref _lastAccountId, value);
        }

        /// <summary>使用 BMCLAPI 镜像加速（国内网络）。</summary>
        public bool UseBmclApiMirror
        {
            get => _useBmclApiMirror;
            set => Set(ref _useBmclApiMirror, value);
        }

        public int MaxLogLines
        {
            get => _maxLogLines;
            set => Set(ref _maxLogLines, Math.Clamp(value, 500, 20000));
        }

        public bool EnableSkinPreview
        {
            get => _enableSkinPreview;
            set => Set(ref _enableSkinPreview, value);
        }
    }

    /// <summary>
    /// 配置的加载与保存。
    /// </summary>
    public sealed class SettingsService
    {
        public AppSettings Settings { get; private set; } = new();

        public void Load()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.AppDataRoot);
                Settings = Json.DeserializeFromFile<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
                if (string.IsNullOrWhiteSpace(Settings.GameDirectory))
                    Settings.GameDirectory = AppPaths.Root;
                AppPaths.Root = Settings.GameDirectory;
                Logger.LogDirectory = Path.Combine(AppPaths.AppDataRoot, "logs");

                // 旧配置中的默认窗口尺寸迁移到新的默认值（用户手动改过的尺寸不受影响）
                if (Settings.DefaultWindowWidth == 1280 && Settings.DefaultWindowHeight == 720)
                {
                    Settings.DefaultWindowWidth = AppConstants.DefaultWindowWidth;
                    Settings.DefaultWindowHeight = AppConstants.DefaultWindowHeight;
                    Save();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载配置失败，使用默认配置", ex);
                Settings = new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                Json.SerializeToFile(AppPaths.SettingsFile, Settings);
            }
            catch (Exception ex)
            {
                Logger.Error("保存配置失败", ex);
            }
        }
    }
}
