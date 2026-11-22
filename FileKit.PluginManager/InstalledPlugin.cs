using FileKit.PluginManager.Internal;

namespace FileKit.PluginManager;

/// <summary>
/// 已安装插件的内存视图。Name 从插件目录中的 manifest 解析，不进入 plugins.json。
/// </summary>
public sealed class InstalledPlugin
{
    internal InstalledPlugin(
        string id,
        string name,
        string version,
        string directoryPath,
        bool enabled)
    {
        Id = id;
        Name = name;
        Version = version;
        DirectoryPath = directoryPath;
        Enabled = enabled;
    }

    public string Id { get; }

    public string Name { get; internal set; }

    public string Version { get; internal set; }

    public string DirectoryPath { get; internal set; }

    public bool Enabled { get; internal set; }

    public bool IsLoaded => Runtime is not null;

    internal PluginRuntime? Runtime { get; set; }

    public override string ToString() =>
        $"{Name} ({Id}) v{Version} enabled={Enabled} loaded={IsLoaded}";
}
