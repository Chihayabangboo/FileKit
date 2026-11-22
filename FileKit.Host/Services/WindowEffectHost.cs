using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FileKit.Contracts.Effects;

namespace FileKit.Host.Services;

/// <summary>
/// IEffectHost 的宿主实现：材质栈 + Overlay 归属管理 + per-effect 门面。
/// 所有公开方法可从任意线程调用，内部统一切换到 UI 线程执行；
/// 除参数为 null 外不向外抛异常，单个特效的异常不会影响其他特效。
/// </summary>
public sealed class WindowEffectHost : IDisposable
{
    private sealed class BackdropEntry
    {
        public required string EffectId { get; init; }

        public required BackdropType Type { get; init; }
    }

    private sealed class EffectContribution
    {
        public List<FrameworkElement> Overlays { get; } = new();
    }

    private readonly Dispatcher _dispatcher;
    private readonly IntPtr _hwnd;
    private readonly EffectMaterialController? _materialController;
    private readonly Action<BackdropType?>? _materialSink;
    private readonly List<BackdropEntry> _backdrops = new();
    private readonly Dictionary<string, EffectContribution> _contributions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<FrameworkElement, string> _overlayOwners = new();

    private FrameworkElement? _overlayLayer;
    private bool _disposed;

    /// <summary>
    /// 创建宿主实现。
    /// </summary>
    /// <param name="hwnd">主窗口句柄；自测时可传 <see cref="IntPtr.Zero"/>。</param>
    /// <param name="dispatcher">主窗口 Dispatcher。</param>
    /// <param name="overlayLayer">Overlay 层容器（Host 的 EffectOverlayLayer，实际需为 <see cref="Panel"/>）。</param>
    /// <param name="materialWindow">承载材质的窗口；为 null 时不做真实 DWM 调用。</param>
    /// <param name="materialSink">材质应用回调（自测注入）；非 null 时优先于真实 DWM 应用。</param>
    public WindowEffectHost(
        IntPtr hwnd,
        Dispatcher dispatcher,
        FrameworkElement? overlayLayer,
        Window? materialWindow = null,
        Action<BackdropType?>? materialSink = null)
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _overlayLayer = overlayLayer;
        _materialSink = materialSink;

        if (materialSink is null && materialWindow is not null && hwnd != IntPtr.Zero)
        {
            try
            {
                _materialController = new EffectMaterialController(materialWindow, hwnd);
            }
            catch (Exception ex)
            {
                // 材质控制器创建失败只降级为纯色，不影响特效加载。
                Debug.WriteLine($"[FileKit] Effect material controller unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public nint WindowHandle => _hwnd;

    public bool IsDisposed => _disposed;

    /// <summary>
    /// 替换 Overlay 层容器：已注册的 Overlay 会从旧层迁移到新层（供测试/第二步换层使用）。
    /// </summary>
    public void SetOverlayLayer(FrameworkElement? overlayLayer)
    {
        RunOnUiThread(() =>
        {
            if (_disposed || ReferenceEquals(_overlayLayer, overlayLayer))
            {
                return;
            }

            if (_overlayLayer is Panel previousLayer)
            {
                foreach (FrameworkElement element in _overlayOwners.Keys.ToArray())
                {
                    try
                    {
                        previousLayer.Children.Remove(element);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[FileKit] Overlay detach failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            _overlayLayer = overlayLayer;

            if (overlayLayer is not null and not Panel)
            {
                Debug.WriteLine("[FileKit] Overlay layer is not a Panel; registered overlays will not be displayed.");
            }

            foreach (FrameworkElement element in _overlayOwners.Keys.ToArray())
            {
                TryAddOverlayToLayer(element);
            }
        });
    }

    /// <summary>为某个特效创建绑定其 effectId 的门面，防止特效互相干扰。</summary>
    public IEffectHost CreateBinding(string effectId)
    {
        if (string.IsNullOrWhiteSpace(effectId))
        {
            throw new ArgumentException("effectId 不能为空。", nameof(effectId));
        }

        return new EffectHostBinding(this, effectId);
    }

    /// <summary>
    /// 强制清理某个特效的全部贡献：弹出其材质栈条目并移除其注册的全部 Overlay。
    /// 幂等；供 Host 在 Detach / Attach 失败 / Dispose 时兜底。
    /// </summary>
    public void CleanupEffect(string effectId)
    {
        if (string.IsNullOrWhiteSpace(effectId))
        {
            return;
        }

        RunOnUiThread(() => CleanupEffectCore(effectId));
    }

    public void Dispose()
    {
        RunOnUiThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Panel? layer = _overlayLayer as Panel;
            foreach (EffectContribution contribution in _contributions.Values)
            {
                foreach (FrameworkElement element in contribution.Overlays.ToArray())
                {
                    try
                    {
                        layer?.Children.Remove(element);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[FileKit] Overlay removal failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                contribution.Overlays.Clear();
            }

            _contributions.Clear();
            _overlayOwners.Clear();

            bool hadBackdrops = _backdrops.Count > 0;
            _backdrops.Clear();
            if (hadBackdrops)
            {
                ApplyEffectiveBackdrop();
            }

            try
            {
                _materialController?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileKit] Material controller dispose failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void PushBackdropCore(string effectId, BackdropType type)
    {
        if (_disposed)
        {
            return;
        }

        for (int index = _backdrops.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_backdrops[index].EffectId, effectId, StringComparison.OrdinalIgnoreCase))
            {
                _backdrops.RemoveAt(index);
            }
        }

        _backdrops.Add(new BackdropEntry { EffectId = effectId, Type = type });
        ApplyEffectiveBackdrop();
    }

    private void PopBackdropCore(string effectId)
    {
        if (_disposed)
        {
            return;
        }

        int removed = _backdrops.RemoveAll(entry =>
            string.Equals(entry.EffectId, effectId, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            Debug.WriteLine($"[FileKit] PopBackdrop ignored unknown effectId '{effectId}'.");
            return;
        }

        ApplyEffectiveBackdrop();
    }

    private void ApplyEffectiveBackdrop()
    {
        BackdropType? effective = _backdrops.Count == 0 ? null : _backdrops[^1].Type;

        if (_materialSink is not null)
        {
            try
            {
                _materialSink(effective);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileKit] Material sink failed: {ex.GetType().Name}: {ex.Message}");
            }

            return;
        }

        try
        {
            _materialController?.Apply(effective);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Material apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RegisterOverlayCore(string effectId, FrameworkElement element, int zIndex)
    {
        if (_disposed)
        {
            return;
        }

        if (_overlayOwners.TryGetValue(element, out string? existingOwner))
        {
            Debug.WriteLine(string.Equals(existingOwner, effectId, StringComparison.OrdinalIgnoreCase)
                ? $"[FileKit] Overlay already registered by '{effectId}', ignored."
                : $"[FileKit] Overlay already owned by '{existingOwner}'; '{effectId}' registration rejected.");
            return;
        }

        // 已被其他容器占用的元素不允许再挂到 Overlay 层，避免破坏主程序/其他特效的视觉树。
        if (element.Parent is not null && !ReferenceEquals(element.Parent, _overlayLayer))
        {
            Debug.WriteLine(
                $"[FileKit] Overlay for '{effectId}' already has a parent ({element.Parent.GetType().Name}); registration rejected.");
            return;
        }

        // 先记录归属再挂载：即使层暂不可用（SetOverlayLayer 之前）也能正确迁移/清理。
        _overlayOwners[element] = effectId;
        GetContribution(effectId).Overlays.Add(element);
        Panel.SetZIndex(element, zIndex);
        TryAddOverlayToLayer(element);
    }

    /// <summary>把已登记归属的 Overlay 挂到当前层；层不是 Panel 时只记录日志（归属仍保留）。</summary>
    private void TryAddOverlayToLayer(FrameworkElement element)
    {
        if (_overlayLayer is not Panel layer)
        {
            Debug.WriteLine("[FileKit] Overlay layer unavailable; overlay registration tracked, waiting for a Panel layer.");
            return;
        }

        try
        {
            if (!layer.Children.Contains(element))
            {
                layer.Children.Add(element);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Overlay attach failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UnregisterOverlayCore(string effectId, FrameworkElement element)
    {
        if (_disposed)
        {
            return;
        }

        if (!_overlayOwners.TryGetValue(element, out string? owner))
        {
            Debug.WriteLine($"[FileKit] UnregisterOverlay ignored unregistered element for '{effectId}'.");
            return;
        }

        if (!string.Equals(owner, effectId, StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine($"[FileKit] Overlay owned by '{owner}'; unregister by '{effectId}' rejected.");
            return;
        }

        RemoveOverlayCore(effectId, element);
    }

    private void RemoveOverlayCore(string effectId, FrameworkElement element)
    {
        _overlayOwners.Remove(element);

        try
        {
            (_overlayLayer as Panel)?.Children.Remove(element);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Overlay removal failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (_contributions.TryGetValue(effectId, out EffectContribution? contribution))
        {
            contribution.Overlays.Remove(element);
        }
    }

    private void CleanupEffectCore(string effectId)
    {
        int removed = _backdrops.RemoveAll(entry =>
            string.Equals(entry.EffectId, effectId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            ApplyEffectiveBackdrop();
        }

        if (!_contributions.TryGetValue(effectId, out EffectContribution? contribution))
        {
            return;
        }

        foreach (FrameworkElement element in contribution.Overlays.ToArray())
        {
            _overlayOwners.Remove(element);

            try
            {
                (_overlayLayer as Panel)?.Children.Remove(element);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileKit] Overlay cleanup failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        contribution.Overlays.Clear();
        _contributions.Remove(effectId);
    }

    private EffectContribution GetContribution(string effectId)
    {
        if (!_contributions.TryGetValue(effectId, out EffectContribution? contribution))
        {
            contribution = new EffectContribution();
            _contributions[effectId] = contribution;
        }

        return contribution;
    }

    private bool RunOnUiThread(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return false;
        }

        if (_dispatcher.CheckAccess())
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileKit] Effect host operation failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        try
        {
            _dispatcher.Invoke(action);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Effect host dispatch failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>绑定单个特效的 IEffectHost 门面：所有调用以绑定 effectId 归属。</summary>
    private sealed class EffectHostBinding : IEffectHost
    {
        private readonly WindowEffectHost _host;
        private readonly string _effectId;

        public EffectHostBinding(WindowEffectHost host, string effectId)
        {
            _host = host;
            _effectId = effectId;
        }

        public nint WindowHandle => _host.WindowHandle;

        public void PushBackdrop(string effectId, BackdropType type)
        {
            if (!string.Equals(effectId, _effectId, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine(
                    $"[FileKit] Effect '{_effectId}' pushed backdrop with mismatched id '{effectId}'; using bound id.");
            }

            _host.RunOnUiThread(() => _host.PushBackdropCore(_effectId, type));
        }

        public void PopBackdrop(string effectId)
        {
            if (!string.Equals(effectId, _effectId, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine(
                    $"[FileKit] Effect '{_effectId}' popped backdrop with mismatched id '{effectId}'; using bound id.");
            }

            _host.RunOnUiThread(() => _host.PopBackdropCore(_effectId));
        }

        public void RegisterOverlay(FrameworkElement element, int zIndex)
        {
            ArgumentNullException.ThrowIfNull(element);
            _host.RunOnUiThread(() => _host.RegisterOverlayCore(_effectId, element, zIndex));
        }

        public void UnregisterOverlay(FrameworkElement element)
        {
            ArgumentNullException.ThrowIfNull(element);
            _host.RunOnUiThread(() => _host.UnregisterOverlayCore(_effectId, element));
        }
    }
}
