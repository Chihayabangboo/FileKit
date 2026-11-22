using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FileKit.Contracts.Effects;

namespace FileKit.TestEffect;

/// <summary>
/// M3 内置测试特效：Attach 时注册半透明提示 Overlay（文字“内置特效已加载”）。
/// 提示先停留 1.5s，再用 0.7s 淡出，动画完成后注销并从视觉树移除；
/// 特效卸载后重新挂载会重新走完整流程，不残留、不叠加。不依赖材质变化。
/// </summary>
public sealed class TestEffect : IFileKitEffect
{
    private const string OverlayText = "内置特效已加载";

    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(700);

    private IEffectHost? _host;
    private Border? _overlay;
    private DispatcherTimer? _holdTimer;
    private bool _disposed;

    public string EffectId => "filekit.test-effect";

    public EffectTarget Target => EffectTarget.Window;

    public void Attach(IEffectHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TestEffect));
        }

        // 幂等：重复 Attach 不重复注册。
        if (_overlay is not null)
        {
            return;
        }

        Border overlay = CreateOverlay();
        _host = host;
        _overlay = overlay;
        host.RegisterOverlay(overlay, 100);
        StartHoldTimer(overlay);
    }

    public void Detach()
    {
        FrameworkElement? overlay = _overlay;
        IEffectHost? host = _host;
        _overlay = null;
        _host = null;
        StopHoldTimer();

        if (overlay is null)
        {
            return;
        }

        try
        {
            // 若正处于淡出动画中，先停掉动画，避免动画完成回调再次注销。
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffect] Stop fade failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (host is null)
        {
            return;
        }

        try
        {
            host.UnregisterOverlay(overlay);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffect] UnregisterOverlay failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Detach();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffect] Detach on Dispose failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Border CreateOverlay()
    {
        var text = new TextBlock
        {
            Text = OverlayText,
            FontSize = 14,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        };

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x1F, 0x1F, 0x1F)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(0, 0, 24, 24),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = text,
            Tag = "filekit.test-effect",
        };
        overlay.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, OverlayText);
        return overlay;
    }

    private void StartHoldTimer(Border overlay)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, overlay.Dispatcher)
        {
            Interval = HoldDuration,
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (ReferenceEquals(_overlay, overlay))
            {
                StartFadeOut(overlay);
            }
        };

        _holdTimer = timer;
        timer.Start();
    }

    private void StartFadeOut(Border overlay)
    {
        var fade = new DoubleAnimation
        {
            From = 1d,
            To = 0d,
            Duration = new Duration(FadeDuration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };

        fade.Completed += (_, _) => RemoveAndUnregister(overlay);

        try
        {
            overlay.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffect] Fade animation failed: {ex.GetType().Name}: {ex.Message}");
            RemoveAndUnregister(overlay);
        }
    }

    private void RemoveAndUnregister(FrameworkElement overlay)
    {
        FrameworkElement? current = _overlay;
        IEffectHost? host = _host;

        if (!ReferenceEquals(current, overlay) || host is null)
        {
            return;
        }

        _overlay = null;
        _host = null;
        StopHoldTimer();

        try
        {
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
        }
        catch
        {
            // 动画清理失败不影响注销。
        }

        try
        {
            host.UnregisterOverlay(overlay);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffect] Auto unregister failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StopHoldTimer()
    {
        _holdTimer?.Stop();
        _holdTimer = null;
    }
}
