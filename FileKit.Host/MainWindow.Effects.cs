using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using FileKit.EffectManager;
using FileKit.Host.Services;
using EffectManagerService = FileKit.EffectManager.EffectManager;

namespace FileKit.Host;

/// <summary>
/// 主窗口的特效系统接线：开发者模式判定、EffectManager/WindowEffectHost/EffectSessionCoordinator
/// 的创建与启动加载、特效管理弹窗（仅开发者模式入口）、窗口关闭时的按序释放。
/// </summary>
public partial class MainWindow
{
    private bool _developerMode;
    private bool _developerModeForcedByCommandLine;
    private bool _syncingDeveloperModeUi;
    private WindowEffectHost? _effectHost;
    private EffectManagerService? _effectManager;
    private EffectSessionCoordinator? _effectCoordinator;
    private EffectManagerWindow? _effectManagerWindow;

    private void InitializeEffectSystem()
    {
        try
        {
            // 模式优先级：命令行 --developer-mode（强制） > 本地勾选配置 developer.json > 默认普通模式。
            string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            _developerModeForcedByCommandLine = DeveloperMode.IsCommandLineSwitchPresent(arguments);
            _developerMode = _developerModeForcedByCommandLine || DeveloperMode.ReadLocalConfig();
            SyncDeveloperModeUi();

            Debug.WriteLine(
                $"[FileKit] Developer mode = {_developerMode} (forcedByCommandLine={_developerModeForcedByCommandLine})");

            string fileKitRoot = HostPaths.FileKitRoot;

            var options = new EffectManagerOptions
            {
                HostVersion = GetHostVersion(),
                BuiltinEffectsDirectory = Path.Combine(AppContext.BaseDirectory, "effects", "builtin"),
                DevEffectsDirectory = Path.Combine(fileKitRoot, "effects", "dev"),
                LogDirectory = Path.Combine(fileKitRoot, "logs"),
            };

            // EffectManager 构造只规范化路径 + 打开日志，不扫描 dev 目录（非开发者模式零触碰）。
            _effectManager = new EffectManagerService(options);

            // M1 的材质逻辑在构造函数中更早注册，SourceInitialized 时先执行；
            // 这里在其后创建特效宿主，此时 hwnd 已可用。
            SourceInitialized += HandleEffectSourceInitialized;
            Loaded += HandleEffectsLoaded;
            Closed += HandleEffectSystemClosed;
        }
        catch (Exception ex)
        {
            // 特效系统初始化失败只记录，绝不影响主程序与插件系统。
            _developerMode = false;
            Debug.WriteLine($"[FileKit] Effect system initialization failed: {ex.GetType().Name}: {ex.Message}");
            try
            {
                ManageEffectsButton.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // 控件尚未加载时忽略。
            }
        }
    }

    /// <summary>把当前模式同步到界面（复选框与【特效管理】按钮），不触发切换逻辑。</summary>
    private void SyncDeveloperModeUi()
    {
        _syncingDeveloperModeUi = true;
        try
        {
            DeveloperModeCheckBox.IsChecked = _developerMode;
            DeveloperModeCheckBox.IsEnabled = !_developerModeForcedByCommandLine;
            DeveloperModeCheckBox.ToolTip = _developerModeForcedByCommandLine
                ? "开发者模式已由命令行 --developer-mode 强制开启"
                : "勾选后启用开发者模式（可加载/安装额外特效）";
            ManageEffectsButton.Visibility = _developerMode ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _syncingDeveloperModeUi = false;
        }
    }

    private void DeveloperModeCheckBox_Checked(object sender, RoutedEventArgs e) =>
        ApplyDeveloperModeFromCheckBox(true);

    private void DeveloperModeCheckBox_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyDeveloperModeFromCheckBox(false);

    /// <summary>界面勾选切换开发者模式：持久化配置并即时加载/卸载 dev 特效。</summary>
    private void ApplyDeveloperModeFromCheckBox(bool enabled)
    {
        if (_syncingDeveloperModeUi)
        {
            return;
        }

        if (_developerModeForcedByCommandLine)
        {
            // 命令行强制模式：忽略界面取消，恢复到强制状态。
            SyncDeveloperModeUi();
            return;
        }

        if (_developerMode == enabled)
        {
            return;
        }

        try
        {
            _developerMode = enabled;
            DeveloperMode.SaveLocalConfig(enabled);
            ManageEffectsButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            _effectManager?.Log.Info($"开发者模式切换：{(enabled ? "开启" : "关闭")}（界面勾选）。");
            _effectCoordinator?.SetDeveloperMode(enabled);
            Debug.WriteLine($"[FileKit] Developer mode toggled from UI: {enabled}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Developer mode toggle failed: {ex.GetType().Name}: {ex.Message}");
            _effectManager?.Log.Error($"开发者模式切换失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void HandleEffectSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            if (_effectHost is not null)
            {
                return;
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _effectHost = new WindowEffectHost(hwnd, Dispatcher, EffectOverlayLayer, this);

            if (_effectManager is not null)
            {
                // 所有者窗口用于 MessageBox；环境限制下也不阻塞启动。
                _effectCoordinator = new EffectSessionCoordinator(
                    _effectHost,
                    _effectManager,
                    _developerMode,
                    this);
            }

            Debug.WriteLine($"[FileKit] Effect host created (hwnd={hwnd}, developerMode={_developerMode}).");
        }
        catch (Exception ex)
        {
            // 特效宿主创建失败只记录，不影响主程序与插件系统。
            Debug.WriteLine($"[FileKit] Effect host creation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void HandleEffectsLoaded(object sender, RoutedEventArgs e)
    {
        InitializeEffects();
    }

    /// <summary>
    /// 启动加载：内置特效总是加载；仅开发者模式加载额外特效并处理 dev 覆盖 builtin。
    /// </summary>
    private void InitializeEffects()
    {
        try
        {
            if (_effectCoordinator is null)
            {
                Debug.WriteLine("[FileKit] Effect coordinator unavailable; effects are not loaded.");
                _effectManager?.Log.Warn("特效宿主或协调器未创建，跳过特效加载。");
                return;
            }

            _effectCoordinator.Initialize();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Effect initialization failed: {ex.GetType().Name}: {ex.Message}");
            _effectManager?.Log.Error($"特效初始化异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ManageEffectsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_developerMode || _effectCoordinator is null)
        {
            Debug.WriteLine("[FileKit] Manage effects requested outside developer mode; ignored.");
            return;
        }

        if (_effectManagerWindow is { IsVisible: true })
        {
            _effectManagerWindow.Activate();
            return;
        }

        try
        {
            _effectManagerWindow = new EffectManagerWindow(_effectCoordinator)
            {
                Owner = this,
            };
            _effectManagerWindow.Closed += HandleEffectManagerWindowClosed;
            _effectManagerWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            // 弹窗失败不影响主程序。
            Debug.WriteLine($"[FileKit] Effect manager window failed: {ex.GetType().Name}: {ex.Message}");
            _effectManagerWindow = null;
        }
    }

    private void HandleEffectManagerWindowClosed(object? sender, EventArgs e)
    {
        if (_effectManagerWindow is not null)
        {
            _effectManagerWindow.Closed -= HandleEffectManagerWindowClosed;
        }

        _effectManagerWindow = null;
    }

    private void HandleEffectSystemClosed(object? sender, EventArgs e)
    {
        Closed -= HandleEffectSystemClosed;
        SourceInitialized -= HandleEffectSourceInitialized;
        Loaded -= HandleEffectsLoaded;

        try
        {
            _effectManagerWindow?.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Effect manager window close failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _effectManagerWindow = null;
        }

        // 释放顺序：Detach/清理/Unload（coordinator）→ 恢复 M1 默认材质并移除 Overlay 层（host）→ 兜底释放 manager。
        try
        {
            _effectCoordinator?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Effect coordinator dispose failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _effectCoordinator = null;
        }

        try
        {
            _effectHost?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Effect host dispose failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _effectHost = null;
        }

        try
        {
            _effectManager?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Effect manager dispose failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _effectManager = null;
        }
    }
}
