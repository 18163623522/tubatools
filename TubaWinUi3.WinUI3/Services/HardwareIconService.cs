using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TubaWinUi3.Services;

/// <summary>
/// 硬件详情页参数卡片的彩色矢量图标（<c>Assets/HwIcons/&lt;key&gt;.svg</c>）。
/// 与内置工具图标同一套 Fluent 规范，但放在独立目录（内置工具图标目录有「不允许孤儿文件」的回归测试）。
/// 没有对应 SVG 时返回 null，卡片头部只显示标题。
/// </summary>
public static class HardwareIconService
{
    private const string UriPrefix = "ms-appx:///Assets/HwIcons/";

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static string? ResolveSvgPath(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HwIcons", key + ".svg");
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>该分组是否有专属彩色图标。只做文件探测，任意线程可调用。</summary>
    public static bool Has(string? key) => ResolveSvgPath(key) is not null;

    /// <summary>
    /// 彩色图标；没有 SVG 或加载失败返回 null。必须在 UI 线程调用（<see cref="SvgImageSource"/> 是 DependencyObject）。
    /// </summary>
    public static ImageSource? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return Cache.GetOrAdd(key, Create);
    }

    private static ImageSource? Create(string key)
    {
        if (ResolveSvgPath(key) is null) return null;
        try
        {
            return new SvgImageSource(new Uri(UriPrefix + key + ".svg"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HardwareIcon] {key} 加载失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>测试用：丢弃进程内缓存。</summary>
    internal static void ResetCache() => Cache.Clear();
}
