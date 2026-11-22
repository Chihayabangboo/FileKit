using System.Windows.Controls;

namespace FileKit.Contracts;

/// <summary>
/// FileKit 插件入口契约。Host 负责加载、初始化与生命周期管理，插件只提供主视图。
/// </summary>
public interface IFileKitPlugin : IDisposable
{
    /// <summary>
    /// 在插件被启用、创建主视图之前调用。
    /// </summary>
    /// <param name="context">由 PluginManager 提供的宿主上下文。</param>
    void Initialize(IPluginContext context);

    /// <summary>
    /// 创建插件主视图，由 Host 放入 TabItem 中展示。
    /// </summary>
    UserControl CreateMainView();
}
