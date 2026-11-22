namespace FileKit.PluginManager;

/// <summary>
/// PluginManager 的构造参数。Host 手动 new，不使用第三方 DI 容器。
/// </summary>
public sealed class PluginManagerOptions
{
    /// <summary>Host 版本，必须是三段式（如 1.0.0），来自 Host 程序集 AssemblyVersion。</summary>
    public required string HostVersion { get; init; }

    /// <summary>插件安装根目录，如 %LOCALAPPDATA%\FileKit\plugins。</summary>
    public required string PluginsDirectory { get; init; }

    /// <summary>状态文件路径，如 %LOCALAPPDATA%\FileKit\plugins.json。</summary>
    public required string StateFilePath { get; init; }

    /// <summary>插件数据根目录，如 %LOCALAPPDATA%\FileKit\data。</summary>
    public required string DataDirectory { get; init; }

    /// <summary>日志目录，如 %LOCALAPPDATA%\FileKit\logs。</summary>
    public required string LogDirectory { get; init; }
}
