using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileKit.Contracts.Effects;

namespace FileKit.TestEffects;

/// <summary>
/// 故意失败的特效：Attach 先注册一个 Overlay，再抛 InvalidOperationException，
/// 用于验收 Host 的“单特效异常不影响主程序和其他特效 + 清理该特效贡献”能力。
/// Detach/Dispose 仍保持不抛异常（Host 清理时会调用）。
/// </summary>
public sealed class FaultyEffect : IFileKitEffect
{
    private IEffectHost? _host;
    private FrameworkElement? _overlay;
    private bool _disposed;

    public string EffectId => "filekit.dev.faulty";

    public EffectTarget Target => EffectTarget.Window;

    public void Attach(IEffectHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FaultyEffect));
        }

        // 先注册 Overlay，确保“抛异常前已有副作用”，验证 Host 的兜底清理。
        if (_overlay is null)
        {
            Border overlay = SampleOverlay.Create(
                "故障特效（Attach 将抛异常）",
                Color.FromArgb(0xB3, 0xB4, 0x23, 0x18),
                HorizontalAlignment.Left,
                VerticalAlignment.Bottom,
                new Thickness(24, 0, 0, 24));

            host.RegisterOverlay(overlay, 120);
            _host = host;
            _overlay = overlay;
        }

        // 按设计抛出，Host 必须捕获并隔离。
        throw new InvalidOperationException("故意抛出的 Attach 异常");
    }

    public void Detach()
    {
        FrameworkElement? overlay = _overlay;
        IEffectHost? host = _host;
        _overlay = null;
        _host = null;

        if (overlay is null || host is null)
        {
            return;
        }

        try
        {
            host.UnregisterOverlay(overlay);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffects] FaultyEffect.Detach failed: {ex.GetType().Name}: {ex.Message}");
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
            Debug.WriteLine($"[FileKit.TestEffects] FaultyEffect.Dispose failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
