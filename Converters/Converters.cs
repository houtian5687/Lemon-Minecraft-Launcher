using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace App1.Converters
{
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Inverse { get; set; }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var flag = value is bool b && b;
            if (Inverse) flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is Visibility v && v == Visibility.Visible;
    }

    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public bool Inverse { get; set; }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var isNull = value is null || (value is string s && string.IsNullOrEmpty(s));
            if (Inverse) isNull = !isNull;
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 判断绑定值是否等于 ConverterParameter（用于单选按钮与枚举绑定）。
    /// </summary>
    public sealed class ValueEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => Equals(value?.ToString(), parameter?.ToString());

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is true ? parameter ?? string.Empty : false;
    }

    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is bool b && !b;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is bool b && !b;
    }

    /// <summary>
    /// 布尔值 → 水平对齐：用于自绘开关的滑块位置（开=靠右，关=靠左）。
    /// </summary>
    public sealed class BoolToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 布尔值 → "开" / "关" 文字：Windows 设置风格的开关状态标签。
    /// </summary>
    public sealed class BoolToOnOffConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? "开" : "关";

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 崩溃严重度 → 背景色。
    /// </summary>
    public sealed class SeverityToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var key = value?.ToString() switch
            {
                "Fatal" => "SystemFillColorCriticalBackgroundBrush",
                "Error" => "SystemFillColorCriticalBackgroundBrush",
                "Warning" => "SystemFillColorCautionBackgroundBrush",
                _ => "SystemFillColorAttentionBackgroundBrush"
            };
            return BrushLookup.Lookup(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 下载状态 → 前景色（用于状态标签着色）。
    /// </summary>
    public sealed class StateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var key = value?.ToString() switch
            {
                "Failed" => "SystemFillColorCriticalBrush",
                "Completed" => "SystemFillColorSuccessBrush",
                "Paused" => "SystemFillColorCautionBrush",
                "Downloading" => "AccentFillColorDefaultBrush",
                _ => "TextFillColorSecondaryBrush"
            };
            return BrushLookup.Lookup(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 资源来源平台 → 徽标底色：Modrinth 用品牌绿、CurseForge 用品牌橙，
    /// 让用户一眼分清资源来自哪个平台。
    /// </summary>
    public sealed class PlatformToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var isCurseForge = string.Equals(value?.ToString(), "CurseForge", StringComparison.OrdinalIgnoreCase);
            var color = isCurseForge
                ? ColorHelper.FromArgb(255, 241, 100, 54)
                : ColorHelper.FromArgb(255, 27, 150, 90);
            return new SolidColorBrush(color);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 把可用宽度收敛到上限内，用于让设置页内容靠左排列、
    /// 同时避免超宽屏下行被拉得过长。
    /// 可用宽度未知时返回 NaN，交由布局按自动宽度处理，避免溢出。
    /// </summary>
    public sealed class ClampWidthConverter : IValueConverter
    {
        /// <summary>内容区的最大宽度（含内边距）。</summary>
        private const double MaxWidth = 820;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not double available || double.IsNaN(available) || available <= 0)
                return double.NaN;

            var max = MaxWidth;
            if (double.TryParse(parameter?.ToString(), out var parsed) && parsed > 0) max = parsed;

            return Math.Min(available, max);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    internal static class BrushLookup
    {
        /// <summary>
        /// 从应用资源中查找画刷，找不到时返回透明画刷，避免空引用。
        /// </summary>
        public static object Lookup(string key)
        {
            if (Application.Current?.Resources is { } resources &&
                resources.TryGetValue(key, out var brush) && brush is not null)
            {
                return brush;
            }
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }
}
