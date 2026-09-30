using System;
using System.Collections.Generic;
using App1.Core;

namespace App1.Models
{
    /// <summary>
    /// 已下载的游戏版本：每个版本拥有独立的游戏目录（存档 / 模组 / 配置 / 材质包 / 光影）。
    /// </summary>
    public sealed class GameInstance : ObservableObject
    {
        private string _name = "";
        private string? _notes;
        private int _minMemoryMb = 512;
        private int _maxMemoryMb = 2048;
        private string? _javaPath;
        private string? _jvmArguments;
        private WindowMode _windowMode = WindowMode.Default;
        private int _windowWidth = 1280;
        private int _windowHeight = 720;
        private DateTime _lastPlayed;
        private long _totalPlaySeconds;
        private bool _showConsole = true;
        private string? _iconPath;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        /// <summary>原版游戏版本号，如 1.20.4。</summary>
        public string GameVersion { get; set; } = "";

        public LoaderType Loader { get; set; } = LoaderType.Vanilla;

        /// <summary>加载器版本（原版为空）。</summary>
        public string? LoaderVersion { get; set; }

        /// <summary>解析后的实际版本目录名（如 fabric-loader-0.15.11-1.20.4）。</summary>
        public string? ResolvedVersionId { get; set; }

        public IsolationLevel Isolation { get; set; } = IsolationLevel.Shared;

        public string? JavaPath
        {
            get => _javaPath;
            set => Set(ref _javaPath, value);
        }

        public int MinMemoryMb
        {
            get => _minMemoryMb;
            set => Set(ref _minMemoryMb, value);
        }

        public int MaxMemoryMb
        {
            get => _maxMemoryMb;
            set => Set(ref _maxMemoryMb, value);
        }

        public string? JvmArguments
        {
            get => _jvmArguments;
            set => Set(ref _jvmArguments, value);
        }

        public WindowMode WindowMode
        {
            get => _windowMode;
            set => Set(ref _windowMode, value);
        }

        public int WindowWidth
        {
            get => _windowWidth;
            set => Set(ref _windowWidth, value);
        }

        public int WindowHeight
        {
            get => _windowHeight;
            set => Set(ref _windowHeight, value);
        }

        public bool ShowConsole
        {
            get => _showConsole;
            set => Set(ref _showConsole, value);
        }

        public string? IconPath
        {
            get => _iconPath;
            set => Set(ref _iconPath, value);
        }

        public string? Notes
        {
            get => _notes;
            set => Set(ref _notes, value);
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime LastPlayed
        {
            get => _lastPlayed;
            set => Set(ref _lastPlayed, value);
        }

        public long TotalPlaySeconds
        {
            get => _totalPlaySeconds;
            set
            {
                if (Set(ref _totalPlaySeconds, value)) Raise(nameof(PlayTimeText));
            }
        }

        /// <summary>最后一次启动绑定的账户（UUID）。</summary>
        public string? LastAccountId { get; set; }

        public List<InstalledResource> InstalledResources { get; set; } = new();

        public string PlayTimeText
        {
            get
            {
                var ts = TimeSpan.FromSeconds(TotalPlaySeconds);
                return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分钟" : $"{ts.Minutes} 分钟";
            }
        }

        public string VersionText => string.IsNullOrEmpty(LoaderVersion)
            ? GameVersion
            : $"{GameVersion} · {Loader.ToText()} {LoaderVersion}";

        public string Directory => AppPaths.InstanceDir(Id);
    }

    /// <summary>
    /// 实例中已安装资源的记录，用于管理与更新。
    /// </summary>
    public sealed class InstalledResource
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public ResourceType Type { get; set; }
        public ResourcePlatform Platform { get; set; }
        public string? VersionId { get; set; }
        public string? VersionName { get; set; }
        public string? Url { get; set; }
        public DateTime InstalledAt { get; set; } = DateTime.Now;
        public bool Enabled { get; set; } = true;
    }
}
