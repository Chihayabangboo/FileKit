using System.Windows.Controls;

namespace FileKit.PluginManager;

/// <summary>
/// 插件即将被卸载运行时（禁用/卸载/覆盖安装）时触发。
/// Host 必须同步完成：移除 TabItem → 清空 Content → 解绑事件。
/// 事件返回后 PluginManager 才会 Dispose 实例并 Unload ALC。
/// </summary>
public sealed class PluginUnloadingEventArgs : EventArgs
{
    public PluginUnloadingEventArgs(InstalledPlugin plugin)
    {
        Plugin = plugin;
    }

    public InstalledPlugin Plugin { get; }

    public string PluginId => Plugin.Id;
}

/// <summary>
/// 启动时成功加载的插件及其主视图。
/// </summary>
public sealed class LoadedPlugin
{
    public LoadedPlugin(InstalledPlugin plugin, UserControl view)
    {
        Plugin = plugin;
        View = view;
    }

    public InstalledPlugin Plugin { get; }

    public UserControl View { get; }
}

public sealed class StartupResult
{
    public StartupResult(IReadOnlyList<LoadedPlugin> loaded, IReadOnlyList<string> errors)
    {
        Loaded = loaded;
        Errors = errors;
    }

    public IReadOnlyList<LoadedPlugin> Loaded { get; }

    /// <summary>启动过程中可跳过的错误（加载失败/pendingDelete 清理失败），只记录不弹窗。</summary>
    public IReadOnlyList<string> Errors { get; }
}

public sealed class LoadResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public InstalledPlugin? Plugin { get; init; }

    public UserControl? View { get; init; }

    internal static LoadResult Ok(InstalledPlugin plugin, UserControl view) =>
        new() { Success = true, Plugin = plugin, View = view };

    internal static LoadResult Ok(InstalledPlugin plugin) =>
        new() { Success = true, Plugin = plugin };

    internal static LoadResult Fail(InstalledPlugin? plugin, string error) =>
        new() { Success = false, Plugin = plugin, Error = error };
}

public sealed class InstallResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public InstalledPlugin? Plugin { get; init; }

    public UserControl? View { get; init; }

    /// <summary>覆盖安装失败并回滚后，旧插件重新加载出的视图（Host 用它恢复 Tab）。</summary>
    public UserControl? RollbackView { get; init; }

    public bool LoadFailed { get; init; }

    public string? LoadError { get; init; }

    public bool WasNewInstall { get; init; }
}

public sealed class UninstallResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    /// <summary>文件被占用时目录已改名为 * _pending_delete_*，下次启动清理。</summary>
    public bool PendingDelete { get; init; }
}
