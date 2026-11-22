using FileKit.Contracts;

namespace FileKit.PluginManager.Internal;

/// <summary>
/// 一个插件的运行时句柄：ALC + 插件实例。
/// 禁用/卸载时必须按“Dispose 实例 → Unload ALC”顺序释放。
/// </summary>
internal sealed class PluginRuntime : IDisposable
{
    private readonly PluginLoadContext _loadContext;
    private readonly PluginManagerLog _log;
    private IFileKitPlugin? _instance;

    public PluginRuntime(PluginLoadContext loadContext, IFileKitPlugin instance, PluginManagerLog log)
    {
        _loadContext = loadContext;
        _instance = instance;
        _log = log;
    }

    public void Dispose()
    {
        IFileKitPlugin? instance = _instance;
        _instance = null;

        if (instance is not null)
        {
            try
            {
                instance.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error($"插件 Dispose 异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            _loadContext.Unload();
        }
        catch (Exception ex)
        {
            _log.Error($"ALC Unload 异常：{ex.GetType().Name}: {ex.Message}");
        }

        // 尽快回收，释放插件程序集文件句柄（不影响正确性，只影响立即覆盖/删除的成功率）。
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        catch
        {
            // 忽略：回收失败不应影响卸载流程。
        }
    }
}
