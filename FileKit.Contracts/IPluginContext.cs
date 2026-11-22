namespace FileKit.Contracts;

/// <summary>
/// 宿主提供给插件的运行时上下文。
/// </summary>
public interface IPluginContext
{
    /// <summary>当前 Host 的三段式版本号，如 1.0.0。</summary>
    string HostVersion { get; }

    /// <summary>当前插件 ID（manifest.id）。</summary>
    string PluginId { get; }

    /// <summary>插件专属数据目录，已由宿主创建。</summary>
    string DataDirectory { get; }
}
