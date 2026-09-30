using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;

namespace App1.Services
{
    /// <summary>
    /// 游戏实例管理：创建、隔离目录、安装、删除、复制与资源清单。
    /// </summary>
    public sealed class InstanceService
    {
        public ObservableCollection<GameInstance> Instances { get; } = new();

        private static string StoreFile => Path.Combine(AppPaths.Root, "instances.json");

        public void Load()
        {
            var list = Json.DeserializeFromFile<List<GameInstance>>(StoreFile) ?? new List<GameInstance>();
            Instances.Clear();
            foreach (var instance in list.OrderByDescending(i => i.LastPlayed))
            {
                Directory.CreateDirectory(instance.Directory);
                Instances.Add(instance);
            }
            Logger.Info($"已加载 {Instances.Count} 个游戏版本");
        }

        public void Save()
        {
            Json.SerializeToFile(StoreFile, Instances.ToList());
        }

        public GameInstance? Get(string? id) => id is null ? null : Instances.FirstOrDefault(i => i.Id == id);

        /// <summary>
        /// 下载并创建一个新的游戏版本（自动安装游戏本体与加载器）。
        /// </summary>
        public async Task<GameInstance> CreateAsync(string name, string gameVersion, LoaderType loader,
            string? loaderVersion, int maxMemoryMb, string? javaPath = null,
            IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        {
            var instance = new GameInstance
            {
                Name = name,
                GameVersion = gameVersion,
                Loader = loader,
                LoaderVersion = loaderVersion,
                MaxMemoryMb = maxMemoryMb,
                JavaPath = javaPath,
                CreatedAt = DateTime.Now
            };
            Directory.CreateDirectory(instance.Directory);
            foreach (var sub in new[] { "mods", "config", "saves", "resourcepacks", "shaderpacks", "logs", "screenshots" })
                Directory.CreateDirectory(Path.Combine(instance.Directory, sub));

            // 同一次下载的全过程共用一个任务分组，便于在下载队列中查看"版本总进度"
            var groupId = "install:" + instance.Id;
            var groupName = $"{instance.Name}（{gameVersion}）";

            try
            {
                var versionId = await AppServices.Loaders.InstallAsync(gameVersion, loader, loaderVersion,
                    progress, ct, groupId, groupName);
                instance.ResolvedVersionId = versionId;

                progress?.Report(new InstallProgress { Phase = "安装游戏本体", Percent = 0 });
                await AppServices.Installer.EnsureVersionAsync(versionId,
                    new Progress<InstallProgress>(p => progress?.Report(new InstallProgress
                    {
                        Phase = "安装游戏本体：" + p.Phase,
                        Detail = p.Detail,
                        Current = p.Current,
                        Total = p.Total,
                        Percent = p.Percent
                    })), ct, groupId, groupName);
            }
            catch (Exception ex)
            {
                // 安装失败时清理残留实例目录
                instance.Directory.DeleteDirectorySafe();
                Logger.Error("游戏版本创建失败", ex);
                throw;
            }

            UiDispatcher.Run(() => Instances.Insert(0, instance));
            Save();
            Logger.Info($"游戏版本创建成功：{name}（{instance.VersionText}）");
            return instance;
        }

        public void Delete(GameInstance instance)
        {
            UiDispatcher.Run(() => Instances.Remove(instance));
            instance.Directory.DeleteDirectorySafe();
            Save();
            Logger.Info("游戏版本已删除：" + instance.Name);
        }

        public GameInstance Clone(GameInstance source, string newName)
        {
            var copy = new GameInstance
            {
                Name = newName,
                GameVersion = source.GameVersion,
                Loader = source.Loader,
                LoaderVersion = source.LoaderVersion,
                ResolvedVersionId = source.ResolvedVersionId,
                MinMemoryMb = source.MinMemoryMb,
                MaxMemoryMb = source.MaxMemoryMb,
                JavaPath = source.JavaPath,
                JvmArguments = source.JvmArguments,
                WindowMode = source.WindowMode,
                WindowWidth = source.WindowWidth,
                WindowHeight = source.WindowHeight,
                Isolation = source.Isolation,
                Notes = source.Notes,
                IconPath = source.IconPath,
                CreatedAt = DateTime.Now
            };
            Directory.CreateDirectory(copy.Directory);
            UiDispatcher.Run(() => Instances.Insert(0, copy));
            Save();
            return copy;
        }

        public static void OpenFolder(GameInstance instance)
        {
            Directory.CreateDirectory(instance.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", instance.Directory) { UseShellExecute = true });
        }

        /// <summary>
        /// 扫描实例目录中实际存在的资源（模组 / 光影 / 材质包）。
        /// </summary>
        public static List<InstalledResource> ScanResources(GameInstance instance)
        {
            var result = new List<InstalledResource>();
            void Scan(string folder, ResourceType type, string extension)
            {
                var dir = Path.Combine(instance.Directory, folder);
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileName(file);
                    result.Add(new InstalledResource
                    {
                        Id = name,
                        Name = Path.GetFileNameWithoutExtension(file),
                        FileName = name,
                        Type = type,
                        Enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) &&
                                  !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase),
                        InstalledAt = File.GetLastWriteTime(file)
                    });
                }
            }
            Scan("mods", ResourceType.Mod, ".jar");
            Scan("shaderpacks", ResourceType.Shader, ".zip");
            Scan("resourcepacks", ResourceType.ResourcePack, ".zip");
            return result;
        }

        /// <summary>
        /// 启用 / 禁用资源（重命名 .disabled 后缀）。
        /// </summary>
        public static bool ToggleResource(GameInstance instance, InstalledResource resource)
        {
            var dir = resource.Type switch
            {
                ResourceType.Mod => "mods",
                ResourceType.Shader => "shaderpacks",
                ResourceType.ResourcePack => "resourcepacks",
                _ => "mods"
            };
            var current = Path.Combine(instance.Directory, dir, resource.FileName);
            if (!File.Exists(current)) return false;
            var target = resource.Enabled ? current + ".disabled" : current.Replace(".disabled", "", StringComparison.OrdinalIgnoreCase);
            File.Move(current, target);
            return true;
        }
    }
}
