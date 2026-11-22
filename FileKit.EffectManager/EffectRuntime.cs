using FileKit.Contracts.Effects;
using FileKit.PluginManager.Internal;

namespace FileKit.EffectManager;

/// <summary>
/// 一个特效的运行时句柄：可回收 ALC + 特效实例。
/// 释放顺序固定：实例 Dispose() → ALC.Unload() → 尽力 GC，用于释放程序集文件句柄。
/// 只能由 <see cref="EffectManager"/> 创建；Host 在 Detach 与清理后调用 Dispose。
/// </summary>
public sealed class EffectRuntime : IDisposable
{
    private readonly object _sync = new();
    private readonly EffectLog _log;
    private readonly Action<EffectRuntime>? _onDisposed;

    private IFileKitEffect? _instance;
    private PluginLoadContext? _loadContext;
    private bool _disposed;

    internal EffectRuntime(
        string effectId,
        IFileKitEffect instance,
        PluginLoadContext loadContext,
        EffectLog log,
        Action<EffectRuntime>? onDisposed)
    {
        EffectId = effectId;
        _instance = instance;
        _loadContext = loadContext;
        _log = log;
        _onDisposed = onDisposed;
    }

    internal string EffectId { get; }

    internal bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposed;
            }
        }
    }

    /// <summary>特效实例。Dispose 之后再访问会抛 <see cref="ObjectDisposedException"/>。</summary>
    public IFileKitEffect Instance =>
        _instance ?? throw new ObjectDisposedException(nameof(EffectRuntime));

    public void Dispose()
    {
        IFileKitEffect? instance;
        PluginLoadContext? loadContext;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            instance = _instance;
            loadContext = _loadContext;
            _instance = null;
            _loadContext = null;
        }

        if (instance is not null)
        {
            try
            {
                instance.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error($"特效实例 Dispose 异常（{EffectId}）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (loadContext is not null)
        {
            try
            {
                loadContext.Unload();
            }
            catch (Exception ex)
            {
                _log.Error($"特效 ALC Unload 异常（{EffectId}）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        catch
        {
            // 回收失败不影响卸载流程。
        }

        try
        {
            _onDisposed?.Invoke(this);
        }
        catch
        {
            // 跟踪清理失败不向外抛。
        }
    }
}
