using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FileKit.Host.Services;

namespace FileKit.Host;

/// <summary>
/// FileKit 主窗口。DWM 材质申请与降级逻辑保持 M1 原样；
/// 插件标签栏与安装/管理入口由 <see cref="PluginSessionCoordinator"/> 驱动。
/// </summary>
public partial class MainWindow : Window
{
    private readonly WindowMaterialService _windowMaterialService = new();
    private readonly bool _animationsEnabled = SystemParameters.ClientAreaAnimation;

    public MainWindow()
    {
        InitializeComponent();

        // Preserve the final state from the first frame when the system disables animations.
        RootLayout.Opacity = _animationsEnabled ? 0d : 1d;

        SourceInitialized += HandleSourceInitialized;
        Loaded += HandleLoaded;

        InitializePluginSystem();
        InitializeEffectSystem();
    }

    private void HandleSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (!_windowMaterialService.TryApply(hwnd))
            {
                return;
            }

            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is not { } compositionTarget)
            {
                Debug.WriteLine("[FileKit] WPF composition target unavailable; keeping the solid background.");
                return;
            }

            // WPF's composition surface is black by default. This lets DWM's
            // backdrop show through the transparent content root.
            compositionTarget.BackgroundColor = Colors.Transparent;
            RootLayout.Background = Brushes.Transparent;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Material presentation failed: {ex.GetType().Name}: {ex.Message}");
            RootLayout.Background = (Brush)FindResource("SurfaceFallbackBrush");
        }
    }

    private void HandleLoaded(object sender, RoutedEventArgs e)
    {
        // 启动时加载所有 enabled 插件并生成标签；失败的只记日志、跳过。
        InitializePluginTabs();

        if (!_animationsEnabled)
        {
            RootLayout.Opacity = 1d;
            return;
        }

        var fadeIn = new DoubleAnimation
        {
            From = 0d,
            To = 1d,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        fadeIn.Completed += (_, _) =>
        {
            RootLayout.BeginAnimation(UIElement.OpacityProperty, null);
            RootLayout.Opacity = 1d;
        };

        RootLayout.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    private void InstallPluginButton_Click(object sender, RoutedEventArgs e)
    {
        InstallPluginFromPackage();
    }

    private void ManagePluginsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPluginManagerWindow();
    }
}
