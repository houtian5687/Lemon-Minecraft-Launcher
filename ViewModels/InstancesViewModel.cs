using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;

namespace App1.ViewModels
{
    /// <summary>
    /// 实例管理：编辑实例配置、复制、删除、修复。
    /// </summary>
    public sealed class InstancesViewModel : ObservableObject
    {
        private GameInstance? _selected;
        private string _editName = "";
        private int _editMinMemory;
        private int _editMaxMemory;
        private string? _editJavaPath;
        private string? _editJvmArguments;
        private int _editWindowMode;
        private int _editWidth;
        private int _editHeight;
        private bool _editShowConsole;
        private string? _editNotes;
        private bool _isBusy;
        private string _statusText = "";

        public ObservableCollection<GameInstance> Instances => AppServices.Instances.Instances;
        public ObservableCollection<JavaInfo> JavaList { get; } = new();
        public ObservableCollection<InstalledResource> Resources { get; } = new();

        /// <summary>跳转到「游戏下载」页面（由页面注入）。</summary>
        public Action? NavigateToDownload { get; set; }

        public GameInstance? Selected
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value))
                {
                    LoadFrom(value);
                    Raise(nameof(HasSelection));
                    NotifyCommands();
                    _ = LoadResourcesAsync();
                }
            }
        }

        public bool HasSelection => Selected is not null;

        public string EditName
        {
            get => _editName;
            set => Set(ref _editName, value);
        }

        public int EditMinMemory
        {
            get => _editMinMemory;
            set => Set(ref _editMinMemory, value);
        }

        public int EditMaxMemory
        {
            get => _editMaxMemory;
            set => Set(ref _editMaxMemory, value);
        }

        public string? EditJavaPath
        {
            get => _editJavaPath;
            set => Set(ref _editJavaPath, value);
        }

        public string? EditJvmArguments
        {
            get => _editJvmArguments;
            set => Set(ref _editJvmArguments, value);
        }

        public int EditWindowMode
        {
            get => _editWindowMode;
            set => Set(ref _editWindowMode, value);
        }

        public int EditWidth
        {
            get => _editWidth;
            set => Set(ref _editWidth, value);
        }

        public int EditHeight
        {
            get => _editHeight;
            set => Set(ref _editHeight, value);
        }

        public bool EditShowConsole
        {
            get => _editShowConsole;
            set => Set(ref _editShowConsole, value);
        }

        public string? EditNotes
        {
            get => _editNotes;
            set => Set(ref _editNotes, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => Set(ref _isBusy, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public RelayCommand SaveCommand { get; }
        public RelayCommand DeleteCommand { get; }
        public RelayCommand CloneCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand RefreshResourcesCommand { get; }
        public RelayCommand ToggleInstalledCommand { get; }
        public RelayCommand RemoveInstalledCommand { get; }
        public AsyncRelayCommand RepairCommand { get; }
        public RelayCommand NewInstanceCommand { get; }

        public InstancesViewModel()
        {
            SaveCommand = new RelayCommand(Save, () => HasSelection);
            DeleteCommand = new RelayCommand(Delete, () => HasSelection);
            CloneCommand = new RelayCommand(Clone, () => HasSelection);
            OpenFolderCommand = new RelayCommand(() =>
            {
                if (Selected is not null) InstanceService.OpenFolder(Selected);
            }, () => HasSelection);
            RefreshResourcesCommand = new RelayCommand(() => _ = LoadResourcesAsync());
            ToggleInstalledCommand = new RelayCommand(p =>
            {
                if (p is InstalledResource r) ToggleResource(r);
            });
            RemoveInstalledCommand = new RelayCommand(p =>
            {
                if (p is InstalledResource r) RemoveResource(r);
            });
            RepairCommand = new AsyncRelayCommand(RepairAsync, () => HasSelection && !IsBusy);
            NewInstanceCommand = new RelayCommand(() => NavigateToDownload?.Invoke());
        }

        public async Task InitializeAsync()
        {
            var javas = await AppServices.Java.ScanAsync();
            UiDispatcher.Run(() =>
            {
                JavaList.Clear();
                foreach (var java in javas) JavaList.Add(java);
            });
            if (Selected is null && Instances.Count > 0)
            {
                UiDispatcher.Run(() => Selected = Instances.First());
            }
        }

        private void LoadFrom(GameInstance? instance)
        {
            if (instance is null) return;
            EditName = instance.Name;
            EditMinMemory = instance.MinMemoryMb;
            EditMaxMemory = instance.MaxMemoryMb;
            EditJavaPath = instance.JavaPath;
            EditJvmArguments = instance.JvmArguments;
            EditWindowMode = (int)instance.WindowMode;
            EditWidth = instance.WindowWidth;
            EditHeight = instance.WindowHeight;
            EditShowConsole = instance.ShowConsole;
            EditNotes = instance.Notes;
            Raise(nameof(EditName), nameof(EditMinMemory), nameof(EditMaxMemory), nameof(EditJavaPath),
                nameof(EditJvmArguments), nameof(EditWindowMode), nameof(EditWidth), nameof(EditHeight),
                nameof(EditShowConsole), nameof(EditNotes));
        }

        public void NotifyCommands()
        {
            SaveCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            CloneCommand.NotifyCanExecuteChanged();
            OpenFolderCommand.NotifyCanExecuteChanged();
            RepairCommand.NotifyCanExecuteChanged();
        }

        private void Save()
        {
            if (Selected is null) return;
            Selected.Name = string.IsNullOrWhiteSpace(EditName) ? Selected.Name : EditName.Trim();
            Selected.MinMemoryMb = Math.Clamp(EditMinMemory, 256, 16384);
            Selected.MaxMemoryMb = Math.Clamp(Math.Max(EditMaxMemory, Selected.MinMemoryMb + 256), 512, 32768);
            Selected.JavaPath = EditJavaPath;
            Selected.JvmArguments = EditJvmArguments;
            Selected.WindowMode = (WindowMode)EditWindowMode;
            Selected.WindowWidth = Math.Clamp(EditWidth, 320, 7680);
            Selected.WindowHeight = Math.Clamp(EditHeight, 240, 4320);
            Selected.ShowConsole = EditShowConsole;
            Selected.Notes = EditNotes;
            AppServices.Instances.Save();
            StatusText = "已保存版本设置";
            AppServices.NotifyInfo("版本设置已保存");
        }

        private void Delete()
        {
            if (Selected is null) return;
            var name = Selected.Name;
            var target = Selected;
            UiDispatcher.Run(() => Selected = null);
            AppServices.Instances.Delete(target);
            StatusText = $"已删除版本 {name}";
        }

        private void Clone()
        {
            if (Selected is null) return;
            var copy = AppServices.Instances.Clone(Selected, Selected.Name + " - 副本");
            UiDispatcher.Run(() => Selected = copy);
            StatusText = "已创建副本";
        }

        private async Task RepairAsync()
        {
            if (Selected is null) return;
            IsBusy = true;
            try
            {
                var versionId = Selected.ResolvedVersionId ?? Selected.GameVersion;
                await AppServices.Installer.EnsureVersionAsync(versionId,
                    new Progress<InstallProgress>(p => UiDispatcher.Run(() => StatusText = p.Text)));
                StatusText = "校验完成，游戏文件完整";
                AppServices.NotifyInfo("游戏文件校验完成");
            }
            catch (Exception ex)
            {
                StatusText = "修复失败：" + ex.Message;
                AppServices.NotifyError("修复失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task LoadResourcesAsync()
        {
            if (Selected is null)
            {
                UiDispatcher.Run(() => Resources.Clear());
                return;
            }
            var instance = Selected;
            var list = await Task.Run(() => InstanceService.ScanResources(instance));
            UiDispatcher.Run(() =>
            {
                Resources.Clear();
                foreach (var item in list) Resources.Add(item);
            });
        }

        public void ToggleResource(InstalledResource resource)
        {
            if (Selected is null) return;
            if (InstanceService.ToggleResource(Selected, resource))
            {
                resource.Enabled = !resource.Enabled;
                _ = LoadResourcesAsync();
            }
        }

        public void RemoveResource(InstalledResource resource)
        {
            if (Selected is null) return;
            var folder = ResourceInstallService.FolderOf(resource.Type);
            var path = System.IO.Path.Combine(Selected.Directory, folder, resource.FileName);
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                Selected.InstalledResources.RemoveAll(r => r.FileName == resource.FileName);
                AppServices.Instances.Save();
                _ = LoadResourcesAsync();
            }
            catch (Exception ex)
            {
                AppServices.NotifyError("删除失败", ex.Message);
            }
        }
    }
}
