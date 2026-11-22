using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FileKit.Host.Services;
using FileKit.PluginManager;

namespace FileKit.Host;

/// <summary>
/// 插件管理弹窗：显示名称/版本/状态，并提供逐行启用/禁用、卸载（二次确认）。
/// 所有真实操作仍由 <see cref="PluginSessionCoordinator"/> 统一驱动主界面标签。
/// </summary>
public partial class PluginManagerWindow : Window
{
    private readonly PluginSessionCoordinator _coordinator;

    public PluginManagerWindow(PluginSessionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;

        InitializeComponent();

        _coordinator.PluginsChanged += HandlePluginsChanged;
        Loaded += HandleLoaded;
        Closed += HandleClosed;
    }

    private void HandleLoaded(object sender, RoutedEventArgs e)
    {
        RefreshRows();
    }

    private void HandlePluginsChanged(object? sender, EventArgs e)
    {
        RefreshRows();
    }

    private void HandleClosed(object? sender, EventArgs e)
    {
        _coordinator.PluginsChanged -= HandlePluginsChanged;
        Loaded -= HandleLoaded;
        Closed -= HandleClosed;
    }

    private void RefreshRows()
    {
        IReadOnlyList<InstalledPlugin> plugins = _coordinator.Plugins;
        PluginListView.ItemsSource = plugins.Select(CreateRow).ToList();
        SummaryText.Text = $"共 {plugins.Count} 个插件";

        bool isEmpty = plugins.Count == 0;
        EmptyListText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        PluginListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private static PluginRowViewModel CreateRow(InstalledPlugin plugin)
    {
        string statusText = plugin.Enabled
            ? (plugin.IsLoaded ? "已启用" : "启用失败")
            : "已禁用";

        return new PluginRowViewModel
        {
            Id = plugin.Id,
            Name = string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name,
            Version = plugin.Version,
            StatusText = statusText,
            ToggleText = plugin.Enabled ? "禁用" : "启用",
        };
    }

    private void TogglePluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string pluginId })
        {
            return;
        }

        InstalledPlugin? plugin = _coordinator.Plugins
            .FirstOrDefault(candidate => string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));

        if (plugin is null)
        {
            return;
        }

        if (plugin.Enabled)
        {
            _coordinator.DisablePlugin(pluginId);
        }
        else
        {
            _coordinator.EnablePlugin(pluginId);
        }

        RefreshRows();
    }

    private void UninstallPluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string pluginId })
        {
            return;
        }

        InstalledPlugin? plugin = _coordinator.Plugins
            .FirstOrDefault(candidate => string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        string displayName = plugin?.Name ?? pluginId;

        MessageBoxResult confirmation = MessageBox.Show(
            this,
            $"确定要卸载插件“{displayName}”吗？\n\n卸载后插件文件会被删除，插件数据目录会保留。",
            "卸载确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _coordinator.UninstallPlugin(pluginId, askForConfirmation: false);
        RefreshRows();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>插件管理列表的一行。</summary>
public sealed class PluginRowViewModel
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string StatusText { get; init; }

    public required string ToggleText { get; init; }
}
