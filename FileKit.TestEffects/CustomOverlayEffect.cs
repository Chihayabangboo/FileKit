using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FileKit.Contracts.Effects;

namespace FileKit.TestEffects;

/// <summary>
/// 样例：仅注册一个半透明提示 Overlay（文本“自定义特效已加载”，右上角，zIndex=110），
/// 停留 1.5s 后 0.7s 淡出并在完成后注销/移除；不调用 PushBackdrop。
/// 用于验收“安装后多出一个自定义 Overlay，短暂显示后自动消失”。
/// </summary>
public sealed class CustomOverlayEffect : IFileKitEffect
{
    private const string OverlayText = "自定义特效已加载";

    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(700);

    private IEffectHost? _host;
    private FrameworkElement? _overlay;
    private DispatcherTimer? _holdTimer;
    private bool _disposed;

    public string EffectId => "filekit.dev.custom-overlay";

    public EffectTarget Target => EffectTarget.Window;

    public void Attach(IEffectHost host)
    {
        if (host is null || _disposed || _overlay is not null)
        {
            return;
        }

        try
        {
            Border overlay = SampleOverlay.Create(
                OverlayText,
                System.Windows.Media.Color.FromArgb(0xB3, 0x1F, 0x1F, 0x1F),
                HorizontalAlignment.Right,
                VerticalAlignment.Top,
                new Thickness(0, 16, 24, 0));

            host.RegisterOverlay(overlay, 110);
            _host = host;
            _overlay = overlay;
            StartHoldTimer(overlay);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffects] CustomOverlayEffect.Attach failed: {ex.GetType().Name}: {ex.Message}");
        }
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
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffects] Stop fade failed: {ex.GetType().Name}: {ex.Message}");
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
            Debug.WriteLine($"[FileKit.TestEffects] CustomOverlayEffect.Detach failed: {ex.GetType().Name}: {ex.Message}");
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
            Debug.WriteLine($"[FileKit.TestEffects] CustomOverlayEffect.Dispose failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StartHoldTimer(FrameworkElement overlay)
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

    private void StartFadeOut(FrameworkElement overlay)
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
            Debug.WriteLine($"[FileKit.TestEffects] Fade animation failed: {ex.GetType().Name}: {ex.Message}");
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
            Debug.WriteLine($"[FileKit.TestEffects] Auto unregister failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StopHoldTimer()
    {
        _holdTimer?.Stop();
        _holdTimer = null;
    }
}
