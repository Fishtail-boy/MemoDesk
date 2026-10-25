using System.Windows;
using System.Windows.Media;

namespace MemoDesk.App.Services;

/// <summary>共享浅色蓝青主题；高对比度切换时更新动态资源。</summary>
public static class AppTheme
{
    private static readonly Dictionary<string, object> Normal = new();
    public static void Start()
    {
        var resources = Application.Current.Resources;
        foreach (var key in resources.Keys.OfType<string>().Where(k => k.EndsWith("Brush", StringComparison.Ordinal)).ToArray())
            Normal[key] = resources[key];
        Apply();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(SystemParameters.HighContrast)) Application.Current?.Dispatcher.InvokeAsync(Apply); };
    }
    private static void Apply()
    {
        if (Application.Current is not { } app) return;
        foreach (var pair in Normal) app.Resources[pair.Key] = pair.Value;
        if (!SystemParameters.HighContrast) return;
        foreach (var key in new[] { "PaperBrush", "SheetBrush", "CoverBrush", "AccentSoftBrush", "SealSoftBrush", "WarnSoftBrush" }) app.Resources[key] = SystemColors.WindowBrush;
        foreach (var key in new[] { "InkBrush", "BodyBrush", "MutedBrush", "FaintBrush", "CoverTextBrush", "CoverMutedBrush", "RuleBrush", "HairlineBrush", "LineBrush", "DangerBrush" }) app.Resources[key] = SystemColors.WindowTextBrush;
        app.Resources["AccentBrush"] = app.Resources["SealBrush"] = SystemColors.HighlightBrush;
        app.Resources["PrimaryTextBrush"] = SystemColors.HighlightTextBrush;
    }
}
