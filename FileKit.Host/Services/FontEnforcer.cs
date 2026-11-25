using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileKit.Host.Services;

/// <summary>
/// 全局字体与文本渲染强制器（“暴君”）：
/// <list type="bullet">
/// <item>字体统一为系统级无衬线字体 <c>Microsoft YaHei UI</c>（西文回退 <c>Segoe UI</c>），不打包任何第三方字体；</item>
/// <item>文本渲染统一为 <see cref="TextRenderingMode.Grayscale"/> + <see cref="TextFormattingMode.Display"/>，
///       消除亚像素渲染导致的彩边/发虚；</item>
/// <item>通过 <see cref="FrameworkElement.LoadedEvent"/> 全局类处理器覆盖每个元素的本地 FontFamily，
///       并每 250ms 对全部窗口做一次兜底扫描，插件硬编码的字体（包括其他中文/英文字体）都会被压回系统雅黑。</item>
/// </list>
/// </summary>
public static class FontEnforcer
{
    /// <summary>App.xaml 中全局字体资源的 Key。</summary>
    public const string AppFontResourceKey = "AppFontFamily";

    /// <summary>系统级无衬线字体（中文优先，西文回退）。</summary>
    public const string SystemFontFamilyName = "Microsoft YaHei UI, Segoe UI";

    private static readonly TimeSpan EnforceInterval = TimeSpan.FromMilliseconds(250);

    private static FontFamily? _fontFamily;
    private static DispatcherTimer? _enforceTimer;
    private static bool _installed;

    /// <summary>当前强制使用的系统字体族。</summary>
    public static FontFamily FontFamily => _fontFamily ??= ResolveFontFamily();

    /// <summary>
    /// 安装全局强制。必须在创建主窗口之前调用（App.OnStartup 起始处）。
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        _fontFamily = ResolveFontFamily();

        // 全局类处理器：任何 FrameworkElement（含插件动态创建的控件）Loaded 时强制字体与渲染模式。
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(HandleFrameworkElementLoaded),
            handledEventsToo: true);

        // “暴君”轮询：插件有时会在加载后修改字体/渲染模式，周期性把全部窗口压回系统雅黑。
        // 仅在当前值不是目标值时才写入，避免无意义的布局刷新。
        _enforceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = EnforceInterval,
        };
        _enforceTimer.Tick += (_, _) => EnforceAllWindows();
        _enforceTimer.Start();

        // 启动早期已存在的窗口做一次全树兜底。
        EnforceAllWindows();

        Debug.WriteLine($"[FileKit] FontEnforcer installed: {FontFamily.Source}");
    }

    /// <summary>立即对当前所有窗口执行一次全树字体/渲染模式强制。</summary>
    public static void EnforceAllWindows()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        foreach (Window window in application.Windows)
        {
            ApplyTextOptions(window);
            EnforceTree(window);
        }
    }

    /// <summary>递归强制整棵可视树的字体与文本渲染模式。</summary>
    public static void EnforceTree(DependencyObject root)
    {
        EnforceElement(root);

        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(root);
        }
        catch
        {
            return;
        }

        for (int index = 0; index < childCount; index++)
        {
            EnforceTree(VisualTreeHelper.GetChild(root, index));
        }
    }

    /// <summary>强制单个元素的 FontFamily 与文本渲染模式（已是目标值时跳过）。</summary>
    public static void EnforceElement(DependencyObject element)
    {
        switch (element)
        {
            case Window window:
                SetFontIfNeeded(window, Control.FontFamilyProperty);
                ApplyTextOptions(window);
                break;
            case Control control:
                SetFontIfNeeded(control, Control.FontFamilyProperty);
                ApplyTextOptions(control);
                break;
            case TextBlock textBlock:
                SetFontIfNeeded(textBlock, TextBlock.FontFamilyProperty);
                ApplyTextOptions(textBlock);
                break;
        }
    }

    /// <summary>统一文本渲染模式：Grayscale 无彩边，Display 更清晰。</summary>
    public static void ApplyTextOptions(DependencyObject target)
    {
        if (!Equals(target.GetValue(TextOptions.TextRenderingModeProperty), TextRenderingMode.Grayscale))
        {
            TextOptions.SetTextRenderingMode(target, TextRenderingMode.Grayscale);
        }

        if (!Equals(target.GetValue(TextOptions.TextFormattingModeProperty), TextFormattingMode.Display))
        {
            TextOptions.SetTextFormattingMode(target, TextFormattingMode.Display);
        }
    }

    private static void HandleFrameworkElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            EnforceElement(element);
        }
    }

    private static void SetFontIfNeeded(DependencyObject element, DependencyProperty property)
    {
        if (element.GetValue(property) is FontFamily current
            && ReferenceEquals(current, FontFamily))
        {
            return;
        }

        element.SetValue(property, FontFamily);
    }

    private static FontFamily ResolveFontFamily()
    {
        if (Application.Current?.TryFindResource(AppFontResourceKey) is FontFamily family)
        {
            return family;
        }

        return new FontFamily(SystemFontFamilyName);
    }
}
