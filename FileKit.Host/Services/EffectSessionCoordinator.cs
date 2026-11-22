using System.Diagnostics;
using System.Windows;
using FileKit.Contracts.Effects;
using FileKit.EffectManager;
using EffectManagerService = FileKit.EffectManager.EffectManager;

namespace FileKit.Host.Services;

/// <summary>
/// 主窗口特效会话协调器：把 EffectManager 的扫描/加载结果接入 WindowEffectHost，
/// 负责启动加载、逐个 Attach、dev 覆盖 builtin、两阶段安装、卸载与异常隔离。
/// 生命周期顺序固定：Detach → host.CleanupEffect → runtime.Dispose（之后才替换目录/删除目录）。
/// 所有方法在 UI 线程调用；单个特效的异常只写 effects.log，不影响主程序与其他特效。
/// </summary>
public sealed class EffectSessionCoordinator : IDisposable
{
    private enum ItemStatus
    {
        Loaded,
        AttachFailed,
        LoadFailed,
    }

    private sealed class SessionItem
    {
        public required string EffectId { get; init; }

        public required string Name { get; set; }

        public required string Version { get; set; }

        public required EffectOrigin Origin { get; init; }

        public required EffectTarget Target { get; set; }

        public ItemStatus Status { get; set; } = ItemStatus.LoadFailed;

        public string? Error { get; set; }

        public bool Attached { get; set; }

        /// <summary>加载结果；Detach/Dispose 后置空以释放 ALC 引用，便于回收。</summary>
        public EffectLoadResult? Result { get; set; }
    }

    private readonly WindowEffectHost _host;
    private readonly EffectManagerService _manager;
    private bool _developerMode;
    private readonly Action<string, string, MessageBoxImage> _showMessage;
    private readonly Func<string, bool> _confirm;
    private readonly Func<string?> _pickPackage;
    private readonly List<SessionItem> _items = new();
    private bool _initialized;
    private bool _disposed;

    public EffectSessionCoordinator(
        WindowEffectHost host,
        EffectManagerService manager,
        bool developerMode,
        Window? owner = null,
        Action<string, string, MessageBoxImage>? showMessage = null,
        Func<string, bool>? confirm = null,
        Func<string?>? pickPackage = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _developerMode = developerMode;

        _showMessage = showMessage ?? ((title, message, icon) =>
        {
            if (owner is null)
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, icon);
            }
            else
            {
                MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
            }
        });

        _confirm = confirm ?? (message =>
        {
            MessageBoxResult result = owner is null
                ? MessageBox.Show(message, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(owner, message, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        });

        _pickPackage = pickPackage ?? (() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 FileKit 特效包",
                Filter = "FileKit 特效包 (*.fkeffect)|*.fkeffect|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            bool? result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            return result == true ? dialog.FileName : null;
        });
    }

    /// <summary>特效会话（内置 + 额外，含加载/挂载失败项）发生变化。</summary>
    public event EventHandler? EffectsChanged;

    public bool DeveloperMode => _developerMode;

    public bool IsInitialized => _initialized;

    public int AttachedCount => _items.Count(item => item.Attached);

    /// <summary>当前特效列表快照（顺序为 Attach 顺序；dev 覆盖 builtin 后只保留 dev 项）。</summary>
    public IReadOnlyList<EffectListItem> Effects
    {
        get
        {
            var snapshot = new List<EffectListItem>(_items.Count);
            foreach (SessionItem item in _items)
            {
                snapshot.Add(ToListItem(item));
            }

            return snapshot;
        }
    }

    /// <summary>
    /// 启动加载：内置特效总是加载；开发者模式再加载额外特效并按 effectId 覆盖（dev 覆盖 builtin）。
    /// 单个结果失败只记录并跳过。
    /// </summary>
    public void Initialize()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;

        if (_host.IsDisposed)
        {
            _manager.Log.Error("特效宿主已释放，跳过特效初始化。");
            RaiseEffectsChanged();
            return;
        }

        try
        {
            IReadOnlyList<EffectLoadResult> builtin = _manager.LoadBuiltinEffects();
            _manager.Log.Info($"内置特效加载：{builtin.Count} 个结果。");
            foreach (EffectLoadResult result in builtin)
            {
                AttachOrOverride(result);
            }

            if (_developerMode)
            {
                IReadOnlyList<EffectLoadResult> dev = _manager.LoadDevEffects();
                _manager.Log.Info($"额外特效加载：{dev.Count} 个结果。");
                foreach (EffectLoadResult result in dev)
                {
                    AttachOrOverride(result);
                }
            }
            else
            {
                _manager.Log.Info("非开发者模式：不扫描额外特效目录。");
            }
        }
        catch (Exception ex)
        {
            // 初始化整体异常也只记录，不影响主程序。
            _manager.Log.Error($"特效初始化异常：{ex.GetType().Name}: {ex.Message}");
        }

        RaiseEffectsChanged();
    }

    /// <summary>
    /// 界面勾选切换开发者模式：开启后扫描并挂载 dev 特效；关闭后卸载已挂载的 dev 特效并保留磁盘文件。
    /// 关闭时若某个内置特效曾被同 id 的 dev 特效覆盖，会重新加载该内置特效。
    /// </summary>
    public void SetDeveloperMode(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        if (_developerMode == enabled)
        {
            RaiseEffectsChanged();
            return;
        }

        _developerMode = enabled;

        try
        {
            if (enabled)
            {
                IReadOnlyList<EffectLoadResult> dev = _manager.LoadDevEffects();
                _manager.Log.Info($"开发者模式已开启（界面勾选）：额外特效 {dev.Count} 个。");
                foreach (EffectLoadResult result in dev)
                {
                    AttachOrOverride(result);
                }
            }
            else
            {
                var removedIds = new List<string>();
                foreach (SessionItem item in _items.Where(item => item.Origin == EffectOrigin.Dev).ToArray())
                {
                    removedIds.Add(item.EffectId);
                    Teardown(item);
                    _items.Remove(item);
                }

                _manager.Log.Info($"开发者模式已关闭（界面勾选）：卸载额外特效 {removedIds.Count} 个。");
                RestoreBuiltinOverrides(removedIds);
            }
        }
        catch (Exception ex)
        {
            _manager.Log.Error($"切换开发者模式异常（enabled={enabled}）：{ex.GetType().Name}: {ex.Message}");
        }

        RaiseEffectsChanged();
    }

    /// <summary>dev 特效被卸载后，重新加载那些“已没有同 id 挂载项”的内置特效（覆盖恢复）。</summary>
    private void RestoreBuiltinOverrides(List<string> removedDevIds)
    {
        if (removedDevIds.Count == 0)
        {
            return;
        }

        List<string> missing = removedDevIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(effectId => FindItem(effectId) is null)
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        IReadOnlyList<EffectLoadResult> builtinResults = _manager.LoadBuiltinEffects();
        foreach (EffectLoadResult result in builtinResults)
        {
            bool needed = missing.Any(effectId =>
                string.Equals(effectId, result.EffectId, StringComparison.OrdinalIgnoreCase));

            if (!needed)
            {
                // 已挂载的内置特效不需要重复实例，直接释放本次加载的运行时。
                result.Runtime?.Dispose();
                continue;
            }

            if (result.Success)
            {
                AttachResult(result);
                _manager.Log.Info($"已恢复内置特效挂载：{result.EffectId}");
            }
            else
            {
                _manager.Log.Warn($"恢复内置特效失败：{result.EffectId}: {result.Error}");
                result.Runtime?.Dispose();
            }
        }
    }

    /// <summary>
    /// 安装额外特效（两阶段）：校验（旧特效保持运行）→ 同 effectId 先拆旧 →
    /// Commit 目录替换 → LoadDevEffect → Attach。返回结果不弹窗，供 UI 与自测使用。
    /// </summary>
    public EffectOperationResult InstallEffect(string packagePath)
    {
        if (_disposed)
        {
            return Fail("特效系统已释放。");
        }

        if (!_developerMode)
        {
            return Fail("仅开发者模式可以安装额外特效。");
        }

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return Fail("未选择特效包。");
        }

        EffectPackageValidationResult validation;
        try
        {
            validation = _manager.ValidateEffectPackage(packagePath);
        }
        catch (Exception ex)
        {
            _manager.Log.Error($"特效包校验异常：{ex.GetType().Name}: {ex.Message}");
            return Fail($"特效包校验异常：{ex.GetType().Name}: {ex.Message}");
        }

        using (validation)
        {
            if (!validation.Success || validation.Package is null)
            {
                string error = validation.Error ?? "特效包校验失败。";
                _manager.Log.Warn($"安装被拒绝：{error}");
                return Fail(error);
            }

            string effectId = validation.Package.Manifest.EffectId;
            SessionItem? existing = FindItem(effectId);

            // 校验已通过，旧特效仍在运行；按约定顺序拆卸旧实例后再替换目录。
            if (existing is not null)
            {
                _manager.Log.Info($"安装前拆卸旧特效：{effectId} [{existing.Origin}]");
                Teardown(existing);
                _items.Remove(existing);
            }

            EffectInstallResult commit;
            try
            {
                commit = _manager.CommitDevEffectInstall(validation.Package);
            }
            catch (Exception ex)
            {
                _manager.Log.Error($"提交安装异常（{effectId}）：{ex.GetType().Name}: {ex.Message}");
                commit = new EffectInstallResult
                {
                    Success = false,
                    Error = $"安装异常：{ex.GetType().Name}: {ex.Message}",
                };
            }

            if (!commit.Success)
            {
                _manager.Log.Error($"安装失败（{effectId}）：{commit.Error}");
                if (existing is not null && existing.Origin == EffectOrigin.Dev)
                {
                    TryReloadDevEffect(effectId);
                }

                RaiseEffectsChanged();
                return Fail(commit.Error ?? "安装失败。");
            }

            EffectLoadResult? loaded = _manager.LoadDevEffect(effectId);
            if (loaded is null)
            {
                _manager.Log.Error($"安装成功但无法加载（{effectId}）：目录未找到。");
                RaiseEffectsChanged();
                return new EffectOperationResult
                {
                    Success = true,
                    LoadFailed = true,
                    PendingDelete = commit.PendingDelete,
                    Error = "安装成功但加载失败：未找到特效目录。",
                    EffectId = effectId,
                };
            }

            AttachResult(loaded);
            RaiseEffectsChanged();

            SessionItem? item = FindItem(effectId);
            bool loadFailed = item is null || item.Status != ItemStatus.Loaded;
            return new EffectOperationResult
            {
                Success = true,
                LoadFailed = loadFailed,
                PendingDelete = commit.PendingDelete,
                Error = loadFailed ? item?.Error ?? loaded.Error : null,
                EffectId = effectId,
            };
        }
    }

    /// <summary>
    /// 卸载额外特效：确认 → Detach → CleanupEffect → Runtime.Dispose →
    /// UninstallDevEffect（目录被占用时 pendingDelete）。内置特效拒绝卸载。
    /// </summary>
    public EffectOperationResult UninstallEffect(string effectId, bool askForConfirmation = true)
    {
        if (_disposed)
        {
            return Fail("特效系统已释放。");
        }

        if (!_developerMode)
        {
            return Fail("仅开发者模式可以卸载额外特效。");
        }

        SessionItem? item = FindItem(effectId);
        if (item is null)
        {
            return Fail($"未找到特效：{effectId}");
        }

        if (item.Origin == EffectOrigin.Builtin)
        {
            return Fail("内置特效不可卸载。");
        }

        if (askForConfirmation
            && !_confirm($"确定要卸载特效“{item.Name}”吗？\n\n卸载后特效文件会被删除，重启后不再加载。"))
        {
            return Fail("已取消卸载");
        }

        Teardown(item);
        // 先移除旧会话项；卸载失败时由 TryReloadDevEffect 重新加载并以新项恢复，
        // 避免同一 effectId 在列表中残留两条（旧项 Result 已释放、不可复用）。
        _items.Remove(item);

        EffectUninstallResult uninstall;
        try
        {
            uninstall = _manager.UninstallDevEffect(effectId);
        }
        catch (Exception ex)
        {
            _manager.Log.Error($"卸载异常（{effectId}）：{ex.GetType().Name}: {ex.Message}");
            TryReloadDevEffect(effectId);
            RaiseEffectsChanged();
            return Fail($"卸载异常：{ex.GetType().Name}: {ex.Message}");
        }

        if (!uninstall.Success)
        {
            _manager.Log.Error($"卸载失败（{effectId}）：{uninstall.Error}");
            TryReloadDevEffect(effectId);
            RaiseEffectsChanged();
            return Fail(uninstall.Error ?? "卸载失败。");
        }

        _items.Remove(item);
        RaiseEffectsChanged();

        return new EffectOperationResult
        {
            Success = true,
            PendingDelete = uninstall.PendingDelete,
            EffectId = effectId,
        };
    }

    /// <summary>UI 入口：选择 .fkeffect 文件并安装，结果用 MessageBox 提示。</summary>
    public void InstallEffectInteractive()
    {
        string? packagePath = _pickPackage();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }

        EffectOperationResult result = InstallEffect(packagePath);
        if (!result.Success)
        {
            ShowError("安装失败", AppendLogHint(result.Error ?? "未知错误。"));
            return;
        }

        if (result.LoadFailed)
        {
            ShowMessage("特效已安装，但加载失败", AppendLogHint(result.Error ?? "请查看特效日志。"), MessageBoxImage.Warning);
            return;
        }

        if (result.PendingDelete)
        {
            ShowMessage(
                "特效已安装",
                "旧版本特效文件当前被占用，已安排在下次启动时自动清理。",
                MessageBoxImage.Information);
            return;
        }

        ShowMessage("特效已安装", "特效已安装并挂载。", MessageBoxImage.Information);
    }

    /// <summary>UI 入口：卸载额外特效（默认二次确认），结果用 MessageBox 提示。</summary>
    public EffectOperationResult UninstallEffectInteractive(string effectId, bool askForConfirmation = true)
    {
        EffectOperationResult result = UninstallEffect(effectId, askForConfirmation);
        if (!result.Success)
        {
            if (!string.Equals(result.Error, "已取消卸载", StringComparison.Ordinal))
            {
                ShowError("卸载失败", AppendLogHint(result.Error ?? "未知错误。"));
            }

            return result;
        }

        if (result.PendingDelete)
        {
            ShowMessage(
                "特效已卸载",
                "特效文件当前被占用，已安排在下次启动时自动清理。",
                MessageBoxImage.Information);
        }

        return result;
    }

    /// <summary>窗口关闭时按序拆掉所有特效（Detach → 清理 → Unload ALC），不负责释放 manager/host。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (SessionItem item in _items.ToArray())
        {
            Teardown(item);
        }

        _items.Clear();
        EffectsChanged = null;
    }

    private void AttachOrOverride(EffectLoadResult result)
    {
        if (_host.IsDisposed || _disposed)
        {
            result.Runtime?.Dispose();
            return;
        }

        SessionItem? existing = FindItem(result.EffectId);
        if (existing is not null)
        {
            if (!result.Success)
            {
                // 新版本加载失败时保留旧特效，避免 dev 目录错误破坏内置特效。
                _manager.Log.Error(
                    $"特效 {result.EffectId} 的 {result.Origin} 版本加载失败，保留 {existing.Origin} 实例：{result.Error}");
                return;
            }

            _manager.Log.Info($"特效 {result.EffectId} 被 {result.Origin} 覆盖，先拆卸 {existing.Origin} 实例。");
            Teardown(existing);
            _items.Remove(existing);
        }

        AttachResult(result);
    }

    private void AttachResult(EffectLoadResult result)
    {
        var item = new SessionItem
        {
            EffectId = result.EffectId,
            Name = result.Name,
            Version = result.Version,
            Origin = result.Origin,
            Target = result.Manifest?.Target ?? EffectTarget.Window,
            Result = result,
        };

        _items.Add(item);

        if (!result.Success || result.Instance is null || result.Runtime is null)
        {
            item.Status = ItemStatus.LoadFailed;
            item.Error = result.Error ?? "特效加载失败。";
            item.Result = null;
            _manager.Log.Error($"特效加载失败（{result.EffectId} [{result.Origin}]）：{item.Error}");
            return;
        }

        try
        {
            IEffectHost binding = _host.CreateBinding(result.EffectId);
            result.Instance.Attach(binding);
            item.Attached = true;
            item.Status = ItemStatus.Loaded;
            _manager.Log.Info($"特效已挂载：{result.EffectId} [{result.Origin}] v{result.Version}");
        }
        catch (Exception ex)
        {
            // 单个特效 Attach 异常：清理其贡献、释放运行时，继续处理其他特效。
            item.Attached = false;
            item.Status = ItemStatus.AttachFailed;
            item.Error = $"{ex.GetType().Name}: {ex.Message}";
            _manager.Log.Error($"特效 Attach 失败（{result.EffectId} [{result.Origin}]）：{item.Error}");
            CleanupHostAndRuntime(result);
            item.Result = null;
        }
    }

    private void Teardown(SessionItem item)
    {
        EffectLoadResult? result = item.Result;

        if (result?.Instance is { } instance)
        {
            try
            {
                instance.Detach();
            }
            catch (Exception ex)
            {
                _manager.Log.Error($"特效 Detach 异常（{item.EffectId}）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            _host.CleanupEffect(item.EffectId);
        }
        catch (Exception ex)
        {
            _manager.Log.Error($"特效清理异常（{item.EffectId}）：{ex.GetType().Name}: {ex.Message}");
        }

        if (result?.Runtime is { } runtime)
        {
            try
            {
                runtime.Dispose();
            }
            catch (Exception ex)
            {
                _manager.Log.Error($"特效运行时释放异常（{item.EffectId}）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        item.Attached = false;
        item.Result = null;
    }

    private void CleanupHostAndRuntime(EffectLoadResult result)
    {
        try
        {
            _host.CleanupEffect(result.EffectId);
        }
        catch (Exception ex)
        {
            _manager.Log.Error($"特效清理异常（{result.EffectId}）：{ex.GetType().Name}: {ex.Message}");
        }

        if (result.Runtime is not { } runtime)
        {
            return;
        }

        try
        {
            runtime.Dispose();
        }
        catch (Exception ex)
        {
            _manager.Log.Error($"特效运行时释放异常（{result.EffectId}）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TryReloadDevEffect(string effectId)
    {
        try
        {
            EffectLoadResult? reloaded = _manager.LoadDevEffect(effectId);
            if (reloaded is null)
            {
                _manager.Log.Warn($"恢复挂载失败：{effectId}（目录不存在）。");
                return;
            }

            AttachResult(reloaded);
        }
        catch (Exception ex)
        {
            _manager.Log.Error($"恢复挂载异常（{effectId}）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private SessionItem? FindItem(string effectId) =>
        _items.FirstOrDefault(item =>
            string.Equals(item.EffectId, effectId, StringComparison.OrdinalIgnoreCase));

    private EffectListItem ToListItem(SessionItem item) => new()
    {
        EffectId = item.EffectId,
        Name = item.Name,
        Version = item.Version,
        Target = item.Target,
        TargetText = item.Target.ToString(),
        Origin = item.Origin,
        OriginText = item.Origin == EffectOrigin.Builtin ? "内置" : "额外",
        StatusText = item.Status switch
        {
            ItemStatus.Loaded => "已加载",
            ItemStatus.AttachFailed => "挂载失败",
            _ => "加载失败",
        },
        Error = item.Error,
        CanUninstall = _developerMode && item.Origin == EffectOrigin.Dev,
        IsAttached = item.Attached,
    };

    private void RaiseEffectsChanged()
    {
        try
        {
            EffectsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _manager.Log.Error($"EffectsChanged 处理异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ShowError(string title, string message) =>
        _showMessage(title, message, MessageBoxImage.Error);

    private void ShowMessage(string title, string message, MessageBoxImage icon) =>
        _showMessage(title, message, icon);

    private string AppendLogHint(string message)
    {
        string logPath = _manager.LogFilePath;
        return string.IsNullOrWhiteSpace(logPath)
            ? message
            : $"{message}{Environment.NewLine}{Environment.NewLine}日志：{logPath}";
    }

    private static EffectOperationResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>特效管理列表的一行（快照）。</summary>
public sealed class EffectListItem
{
    public required string EffectId { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required EffectTarget Target { get; init; }

    public required string TargetText { get; init; }

    public required EffectOrigin Origin { get; init; }

    public required string OriginText { get; init; }

    public required string StatusText { get; init; }

    public string? Error { get; init; }

    public bool CanUninstall { get; init; }

    public bool IsAttached { get; init; }
}

/// <summary>安装/卸载等会话操作结果，供 UI 与自测断言使用。</summary>
public sealed class EffectOperationResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    /// <summary>旧目录/备份被占用并改名为 pendingDelete，下次启动清理。</summary>
    public bool PendingDelete { get; init; }

    /// <summary>安装成功但加载/挂载失败（Success 仍为 true，安装本身已生效）。</summary>
    public bool LoadFailed { get; init; }

    public string? EffectId { get; init; }
}
