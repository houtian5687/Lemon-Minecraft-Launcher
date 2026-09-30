using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace App1.Core
{
    /// <summary>
    /// 窗口句柄与文件 / 文件夹选择器的辅助封装（WinUI3 需要显式绑定窗口句柄）。
    /// </summary>
    public static class WindowHelper
    {
        private static nint _handle;

        private static Microsoft.UI.WindowId _windowId;

        public static void Initialize(Window window)
        {
            _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            _windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_handle);
            AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(_windowId);
        }

        public static Microsoft.UI.Windowing.AppWindow? AppWindow { get; private set; }

        public static nint Handle => _handle;

        /// <summary>
        /// 按指定像素尺寸调整窗口并居中显示。
        /// 若目标尺寸超出当前显示器工作区，会自动收敛到工作区大小，避免窗口跑到屏幕外。
        /// </summary>
        public static void ApplyWindowSize(int width, int height)
        {
            if (AppWindow is null) return;
            try
            {
                var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                    _windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                var work = area.WorkArea;

                var targetWidth = Math.Max(640, Math.Min(width, work.Width));
                var targetHeight = Math.Max(480, Math.Min(height, work.Height));

                AppWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
                AppWindow.Move(new Windows.Graphics.PointInt32(
                    work.X + (work.Width - targetWidth) / 2,
                    work.Y + (work.Height - targetHeight) / 2));

                Logger.Info($"启动器窗口尺寸：{targetWidth}x{targetHeight}（工作区 {work.Width}x{work.Height}）");
            }
            catch (Exception ex)
            {
                Logger.Warn("设置窗口尺寸失败：" + ex.Message);
            }
        }

        public static async Task<string?> PickFolderAsync(string? suggested = null)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _handle);
                var folder = await picker.PickSingleFolderAsync();
                return folder?.Path;
            }
            catch (Exception ex)
            {
                Logger.Warn("选择文件夹失败：" + ex.Message);
                return null;
            }
        }

        public static async Task<string?> PickFileAsync(params string[] extensions)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add("*");
                foreach (var ext in extensions)
                {
                    if (ext.StartsWith(".")) picker.FileTypeFilter.Add(ext);
                }
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _handle);
                var file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch (Exception ex)
            {
                Logger.Warn("选择文件失败：" + ex.Message);
                return null;
            }
        }
    }
}
