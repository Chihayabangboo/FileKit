using FileKit.Contracts;

namespace FileKit.PluginManager.Internal;

/// <summary>
/// IPluginContext 的实现，绑定当前插件的数据目录。
/// </summary>
internal sealed class PluginContext : IPluginContext
{
    public PluginContext(string hostVersion, string pluginId, string dataDirectory)
    {
        HostVersion = hostVersion;
        PluginId = pluginId;
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(DataDirectory);
    }

    public string HostVersion { get; }

    public string PluginId { get; }

    public string DataDirectory { get; }
}
