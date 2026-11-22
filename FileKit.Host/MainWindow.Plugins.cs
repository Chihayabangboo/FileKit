using System.IO;
using System.Windows;
using FileKit.Host.Services;
using FileKit.PluginManager;
using PluginManagerService = FileKit.PluginManager.PluginManager;

namespace FileKit.Host;

/// <summary>
/// 主窗口的插件系统接线：手动创建 PluginManager（不使用第三方 DI），
/// 管理插件管理弹窗与窗口关闭时的运行时释放。
/// </summary>
public partial class MainWindow
{
    private PluginManagerService _pluginManager = null!;
    private PluginSessionCoordinator _pluginCoordinator = null!;
    private PluginManagerWindow? _pluginManagerWindow;

    private void InitializePluginSystem()
    {
        // 优先 %LOCALAPPDATA%\FileKit；受限环境不可写时自动回退到 exe 目录/临时目录下的 FileKitData。
        string fileKitRoot = HostPaths.FileKitRoot;

        var options = new PluginManagerOptions
        {
            HostVersion = GetHostVersion(),
            PluginsDirectory = Path.Combine(fileKitRoot, "plugins"),
            StateFilePath = Path.Combine(fileKitRoot, "plugins.json"),
            DataDirectory = Path.Combine(fileKitRoot, "data"),
            LogDirectory = Path.Combine(fileKitRoot, "logs"),
        };

        _pluginManager = new PluginManagerService(options);
        _pluginCoordinator = new PluginSessionCoordinator(
            PluginTabControl,
            EmptyStatePanel,
            _pluginManager,
            this);

        Closed += HandlePluginSystemClosed;
    }

    private void InitializePluginTabs()
    {
        _pluginCoordinator.Initialize();
    }

    private void InstallPluginFromPackage()
    {
        _pluginCoordinator.InstallInteractive();
    }

    private void ShowPluginManagerWindow()
    {
        if (_pluginManagerWindow is { IsVisible: true })
        {
            _pluginManagerWindow.Activate();
            return;
        }

        _pluginManagerWindow = new PluginManagerWindow(_pluginCoordinator)
        {
            Owner = this,
        };
        _pluginManagerWindow.Closed += HandlePluginManagerWindowClosed;
        _pluginManagerWindow.ShowDialog();
    }

    private void HandlePluginManagerWindowClosed(object? sender, EventArgs e)
    {
        if (_pluginManagerWindow is not null)
        {
            _pluginManagerWindow.Closed -= HandlePluginManagerWindowClosed;
        }

        _pluginManagerWindow = null;
    }

    private void HandlePluginSystemClosed(object? sender, EventArgs e)
    {
        Closed -= HandlePluginSystemClosed;
        _pluginCoordinator.Dispose();
    }

    /// <summary>
    /// HostVersion 来自 Host 程序集 AssemblyVersion（1.0.0.0 → 1.0.0）。
    /// </summary>
    private static string GetHostVersion()
    {
        Version? version = typeof(MainWindow).Assembly.GetName().Version;
        int build = version is null || version.Build < 0 ? 0 : version.Build;
        return $"{version?.Major ?? 1}.{version?.Minor ?? 0}.{build}";
    }
}
