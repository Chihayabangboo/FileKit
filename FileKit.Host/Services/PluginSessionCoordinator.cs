using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileKit.PluginManager;
using PluginManagerService = FileKit.PluginManager.PluginManager;

namespace FileKit.Host.Services;

/// <summary>
/// 主界面插件工作区协调器：负责启动加载、TabItem 生成/移除、选中切换、
/// 安装/启用/禁用/卸载交互与 MessageBox 提示。所有方法在 UI 线程调用。
/// </summary>
public sealed class PluginSessionCoordinator : IDisposable
{
    private static readonly Brush CardBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CardBorderBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0x00, 0x00, 0x00));

    private readonly TabControl _tabControl;
    private readonly FrameworkElement _emptyState;
    private readonly PluginManagerService _manager;
    private readonly Window? _owner;
    private readonly Action<string, string, MessageBoxImage> _showMessage;
    private readonly Func<string, bool> _confirm;
    private readonly Func<string?> _pickPackage;
    private bool _initialized;
    private bool _disposed;

    public PluginSessionCoordinator(
        TabControl tabControl,
        FrameworkElement emptyState,
        PluginManagerService manager,
        Window? owner = null,
        Action<string, string, MessageBoxImage>? showMessage = null,
        Func<string, bool>? confirm = null,
        Func<string?>? pickPackage = null)
    {
        _tabControl = tabControl ?? throw new ArgumentNullException(nameof(tabControl));
        _emptyState = emptyState ?? throw new ArgumentNullException(nameof(emptyState));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _owner = owner;

        _showMessage = showMessage ?? ((title, message, icon) =>
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon));
        _confirm = confirm ?? (message =>
            MessageBox.Show(owner, message, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes);
        _pickPackage = pickPackage ?? SelectPackageFile;

        _manager.PluginUnloading += HandlePluginUnloading;
        UpdateEmptyState();
    }

    public event EventHandler? PluginsChanged;

    public IReadOnlyList<InstalledPlugin> Plugins => _manager.Plugins;

    public int TabCount => _tabControl.Items.Count;

    public bool IsEmptyStateVisible => _emptyState.Visibility == Visibility.Visible;

    public string? SelectedPluginId =>
        _tabControl.SelectedItem is TabItem { Tag: string pluginId } ? pluginId : null;

    public void Initialize()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;

        StartupResult startup = _manager.Initialize();
        foreach (string error in startup.Errors)
        {
            Debug.WriteLine($"[FileKit] 插件启动加载问题：{error}");
        }

        foreach (LoadedPlugin loaded in startup.Loaded)
        {
            AddTab(loaded.Plugin, loaded.View, select: false);
        }

        if (_tabControl.Items.Count > 0)
        {
            _tabControl.SelectedIndex = 0;
        }

        UpdateEmptyState();
        RaisePluginsChanged();
    }

    public void InstallInteractive()
    {
        string? packagePath = _pickPackage();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }

        InstallPackage(packagePath);
    }

    public InstallResult InstallPackage(string packagePath)
    {
        if (_disposed)
        {
            return new InstallResult { Success = false, Error = "插件系统已释放。" };
        }

        InstallResult result;
        try
        {
            result = _manager.Install(packagePath);
        }
        catch (Exception ex)
        {
            string message = $"插件系统异常：{ex.GetType().Name}: {ex.Message}";
            ShowError("安装失败", AppendLogHint(message));
            RaisePluginsChanged();
            return new InstallResult { Success = false, Error = message };
        }

        if (!result.Success)
        {
            // 覆盖失败时底层可能已经拆掉旧 Tab，这里用回滚视图恢复。
            if (result.RollbackView is not null && result.Plugin is not null)
            {
                AddTab(result.Plugin, result.RollbackView, select: true);
            }

            ShowError("安装失败", AppendLogHint(result.Error ?? "未知错误。"));
        }
        else if (result.LoadFailed)
        {
            ShowMessage(
                "插件已安装，但加载失败",
                AppendLogHint(result.LoadError ?? "请查看插件日志。"),
                MessageBoxImage.Warning);
        }
        else if (result.View is not null && result.Plugin is not null)
        {
            AddTab(result.Plugin, result.View, select: true);
        }
        else
        {
            ShowMessage(
                "插件已安装",
                "插件当前为禁用状态，可在【插件管理】中启用。",
                MessageBoxImage.Information);
        }

        RaisePluginsChanged();
        return result;
    }

    public LoadResult EnablePlugin(string pluginId)
    {
        if (_disposed)
        {
            return new LoadResult { Success = false, Error = "插件系统已释放。" };
        }

        LoadResult result;
        try
        {
            result = _manager.Enable(pluginId);
        }
        catch (Exception ex)
        {
            string message = $"插件系统异常：{ex.GetType().Name}: {ex.Message}";
            ShowError("启用失败", AppendLogHint(message));
            RaisePluginsChanged();
            return new LoadResult { Success = false, Error = message };
        }

        if (!result.Success)
        {
            ShowError("启用失败", AppendLogHint(result.Error ?? "未知错误。"));
        }
        else if (result.View is not null && result.Plugin is not null)
        {
            AddTab(result.Plugin, result.View, select: true);
        }

        RaisePluginsChanged();
        return result;
    }

    public LoadResult DisablePlugin(string pluginId)
    {
        if (_disposed)
        {
            return new LoadResult { Success = false, Error = "插件系统已释放。" };
        }

        LoadResult result;
        try
        {
            result = _manager.Disable(pluginId);
        }
        catch (Exception ex)
        {
            string message = $"插件系统异常：{ex.GetType().Name}: {ex.Message}";
            ShowError("禁用失败", AppendLogHint(message));
            RaisePluginsChanged();
            return new LoadResult { Success = false, Error = message };
        }

        if (!result.Success)
        {
            ShowError("禁用失败", AppendLogHint(result.Error ?? "未知错误。"));
        }

        RaisePluginsChanged();
        return result;
    }

    public UninstallResult UninstallPlugin(string pluginId, bool askForConfirmation = true)
    {
        if (_disposed)
        {
            return new UninstallResult { Success = false, Error = "插件系统已释放。" };
        }

        InstalledPlugin? plugin = _manager.Find(pluginId);
        if (plugin is null)
        {
            ShowError("卸载失败", $"未找到插件：{pluginId}");
            return new UninstallResult { Success = false, Error = $"未找到插件：{pluginId}" };
        }

        if (askForConfirmation
            && !_confirm($"确定要卸载插件“{plugin.Name}”吗？\n\n卸载后插件文件会被删除，插件数据目录会保留。"))
        {
            return new UninstallResult { Success = false, Error = "已取消卸载" };
        }

        UninstallResult result;
        try
        {
            result = _manager.Uninstall(pluginId);
        }
        catch (Exception ex)
        {
            string message = $"插件系统异常：{ex.GetType().Name}: {ex.Message}";
            ShowError("卸载失败", AppendLogHint(message));
            RaisePluginsChanged();
            return new UninstallResult { Success = false, Error = message };
        }

        if (!result.Success)
        {
            ShowError("卸载失败", AppendLogHint(result.Error ?? "未知错误。"));
        }
        else if (result.PendingDelete)
        {
            ShowMessage(
                "插件已卸载",
                "插件文件当前被占用，已安排在下次启动时自动清理。",
                MessageBoxImage.Information);
        }

        RaisePluginsChanged();
        return result;
    }

    public string? GetTabPluginId(int index)
    {
        if (index < 0 || index >= _tabControl.Items.Count)
        {
            return null;
        }

        return _tabControl.Items[index] is TabItem { Tag: string pluginId } ? pluginId : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.PluginUnloading -= HandlePluginUnloading;
        _manager.Dispose();
    }

    private void HandlePluginUnloading(object? sender, PluginUnloadingEventArgs e)
    {
        TabItem? tab = FindTab(e.PluginId);
        if (tab is null)
        {
            return;
        }

        bool wasSelected = ReferenceEquals(_tabControl.SelectedItem, tab);
        int removedIndex = _tabControl.Items.IndexOf(tab);

        // 清空 Content / 解绑事件：Host 没有向插件视图注册事件，
        // 这里主动断开 Border->UserControl 引用，确保 ALC 可被回收。
        DetachTabContent(tab);
        _tabControl.Items.Remove(tab);

        if (wasSelected)
        {
            SelectAdjacent(removedIndex);
        }

        UpdateEmptyState();
    }

    private TabItem AddTab(InstalledPlugin plugin, UserControl view, bool select)
    {
        TabItem? existing = FindTab(plugin.Id);
        if (existing is not null)
        {
            existing.Header = BuildTabHeader(plugin);
            if (select)
            {
                _tabControl.SelectedItem = existing;
            }

            UpdateEmptyState();
            return existing;
        }

        var tab = new TabItem
        {
            Header = BuildTabHeader(plugin),
            Tag = plugin.Id,
            ToolTip = $"{plugin.Id}  v{plugin.Version}",
        };

        tab.Content = new Border
        {
            Background = CardBackgroundBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = view,
        };

        _tabControl.Items.Add(tab);

        if (select)
        {
            _tabControl.SelectedItem = tab;
        }

        UpdateEmptyState();
        return tab;
    }

    private void SelectAdjacent(int removedIndex)
    {
        int count = _tabControl.Items.Count;
        if (count == 0)
        {
            _tabControl.SelectedIndex = -1;
            return;
        }

        // 优先下一个；没有下一个则切上一个（即剩余列表的最后一个）。
        int targetIndex = removedIndex < count ? removedIndex : count - 1;
        _tabControl.SelectedIndex = targetIndex;
    }

    private void UpdateEmptyState()
    {
        bool hasTabs = _tabControl.Items.Count > 0;

        _tabControl.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        _emptyState.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;

        if (hasTabs && _tabControl.SelectedIndex < 0)
        {
            _tabControl.SelectedIndex = 0;
        }
    }

    private static void DetachTabContent(TabItem tab)
    {
        if (tab.Content is Border { } border)
        {
            border.Child = null;
        }

        tab.Content = null;
    }

    private TabItem? FindTab(string pluginId)
    {
        foreach (object item in _tabControl.Items)
        {
            if (item is TabItem { Tag: string tag } tab
                && string.Equals(tag, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                return tab;
            }
        }

        return null;
    }

    private static string BuildTabHeader(InstalledPlugin plugin) =>
        string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name;

    private void ShowError(string title, string message) =>
        _showMessage(title, message, MessageBoxImage.Error);

    private void ShowMessage(string title, string message, MessageBoxImage icon) =>
        _showMessage(title, message, icon);

    private void RaisePluginsChanged() => PluginsChanged?.Invoke(this, EventArgs.Empty);

    private string AppendLogHint(string message)
    {
        string logPath = _manager.LogFilePath;
        return string.IsNullOrWhiteSpace(logPath)
            ? message
            : $"{message}{Environment.NewLine}{Environment.NewLine}日志：{logPath}";
    }

    private string? SelectPackageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 FileKit 插件包",
            Filter = "FileKit 插件包 (*.fkpkg)|*.fkpkg|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        bool? result = _owner is null ? dialog.ShowDialog() : dialog.ShowDialog(_owner);
        return result == true ? dialog.FileName : null;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
