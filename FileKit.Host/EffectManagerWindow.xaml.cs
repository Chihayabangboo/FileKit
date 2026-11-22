using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FileKit.EffectManager;
using FileKit.Host.Services;

namespace FileKit.Host;

/// <summary>
/// 特效管理弹窗（仅开发者模式入口）：内置特效只读列表 + 额外特效可卸载列表 + 安装按钮。
/// 真实生命周期操作全部由 <see cref="EffectSessionCoordinator"/> 驱动，窗口只负责展示与刷新。
/// </summary>
public partial class EffectManagerWindow : Window
{
    private readonly EffectSessionCoordinator _coordinator;

    public EffectManagerWindow(EffectSessionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;

        InitializeComponent();

        _coordinator.EffectsChanged += HandleEffectsChanged;
        Loaded += HandleLoaded;
        Closed += HandleClosed;
    }

    private void HandleLoaded(object sender, RoutedEventArgs e)
    {
        RefreshRows();
    }

    private void HandleEffectsChanged(object? sender, EventArgs e)
    {
        RefreshRows();
    }

    private void HandleClosed(object? sender, EventArgs e)
    {
        _coordinator.EffectsChanged -= HandleEffectsChanged;
        Loaded -= HandleLoaded;
        Closed -= HandleClosed;
    }

    private void RefreshRows()
    {
        IReadOnlyList<EffectListItem> effects = _coordinator.Effects;

        List<EffectRowViewModel> builtin = effects
            .Where(effect => effect.Origin == EffectOrigin.Builtin)
            .Select(ToRow)
            .ToList();

        List<EffectRowViewModel> dev = effects
            .Where(effect => effect.Origin == EffectOrigin.Dev)
            .Select(ToRow)
            .ToList();

        BuiltinListView.ItemsSource = builtin;
        DevListView.ItemsSource = dev;

        SummaryText.Text =
            $"内置 {builtin.Count} 个 · 额外 {dev.Count} 个 · 已挂载 {_coordinator.AttachedCount} 个";

        EmptyBuiltinText.Visibility = builtin.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuiltinListView.Visibility = builtin.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        EmptyDevText.Visibility = dev.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DevListView.Visibility = dev.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static EffectRowViewModel ToRow(EffectListItem effect) => new()
    {
        Id = effect.EffectId,
        Name = string.IsNullOrWhiteSpace(effect.Name) ? effect.EffectId : effect.Name,
        Version = effect.Version,
        TargetText = effect.TargetText,
        StatusText = effect.StatusText,
        Error = effect.Error,
        CanUninstall = effect.CanUninstall,
    };

    private void InstallEffectButton_Click(object sender, RoutedEventArgs e)
    {
        _coordinator.InstallEffectInteractive();
        RefreshRows();
    }

    private void UninstallEffectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string effectId })
        {
            return;
        }

        _coordinator.UninstallEffectInteractive(effectId);
        RefreshRows();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>特效管理列表的一行（内置/额外共用）。</summary>
public sealed class EffectRowViewModel
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string TargetText { get; init; }

    public required string StatusText { get; init; }

    public string? Error { get; init; }

    public bool CanUninstall { get; init; }
}
