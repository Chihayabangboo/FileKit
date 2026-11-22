using System.Reflection;
using System.Runtime.Loader;

namespace FileKit.PluginManager.Internal;

/// <summary>
/// 每个插件独立的可回收 ALC：
/// - Contracts 回退到 Default ALC（返回 null 由运行时回退解析）；
/// - 其它依赖只允许从当前插件的 libs/ 目录加载；
/// - 框架程序集（WPF 等）回退到 Default。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string SharedContractsAssemblyName = "FileKit.Contracts";

    private readonly string _libsRoot;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string pluginId, string entryAssemblyPath, string libsRoot)
        : base($"FileKit.Plugin.{pluginId}", isCollectible: true)
    {
        _libsRoot = Path.GetFullPath(libsRoot);

        try
        {
            _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        }
        catch
        {
            // 没有 deps.json 的插件走 libs/{name}.dll 兜底。
            _resolver = null;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // 共享契约必须解析到 Default ALC，否则插件实例无法与宿主强转。
        if (string.Equals(name, SharedContractsAssemblyName, StringComparison.Ordinal))
        {
            return null;
        }

        string? path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            string candidate = Path.Combine(_libsRoot, name + ".dll");
            if (File.Exists(candidate))
            {
                path = candidate;
            }
        }

        if (path is null)
        {
            // 交给 Default ALC（WPF / 框架程序集）。
            return null;
        }

        string fullPath = Path.GetFullPath(path);
        if (!PathRules.IsUnderDirectory(fullPath, _libsRoot))
        {
            // 解析结果越出 libs/：拒绝，交回 Default。
            return null;
        }

        return LoadFromAssemblyPath(fullPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            return IntPtr.Zero;
        }

        string fullPath = Path.GetFullPath(path);
        if (!PathRules.IsUnderDirectory(fullPath, _libsRoot))
        {
            return IntPtr.Zero;
        }

        return LoadUnmanagedDllFromPath(fullPath);
    }
}
