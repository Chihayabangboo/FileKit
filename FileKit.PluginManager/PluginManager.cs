using System.Reflection;
using System.Windows.Controls;
using FileKit.Contracts;
using FileKit.PluginManager.Internal;

namespace FileKit.PluginManager;

/// <summary>
/// 插件系统门面：安装包校验、版本兼容、ALC 生命周期、重复安装、卸载与 pendingDelete、
/// plugins.json 持久化。所有方法必须在 UI 线程同步调用。
/// </summary>
public sealed class PluginManager : IDisposable
{
    private const int FileOperationRetryCount = 3;
    private const int FileOperationRetryDelayMilliseconds = 50;

    private readonly object _sync = new();
    private readonly List<InstalledPlugin> _plugins = new();
    private readonly PluginManagerLog _log;
    private readonly PluginStateStore _stateStore;
    private readonly PluginPackageValidator _packageValidator;
    private readonly string _hostVersion;
    private readonly string _pluginsRoot;
    private readonly string _dataRoot;
    private readonly string _stagingRoot;
    private bool _disposed;

    public PluginManager(PluginManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!VersionRules.IsThreePart(options.HostVersion))
        {
            throw new ArgumentException(
                "HostVersion 必须是三段式版本号（如 1.0.0）。",
                nameof(options));
        }

        _hostVersion = options.HostVersion;
        _pluginsRoot = PathRules.NormalizeAbsolutePath(options.PluginsDirectory, nameof(options.PluginsDirectory));
        _dataRoot = PathRules.NormalizeAbsolutePath(options.DataDirectory, nameof(options.DataDirectory));
        string stateFilePath = PathRules.NormalizeAbsolutePath(options.StateFilePath, nameof(options.StateFilePath));
        string logDirectory = PathRules.NormalizeAbsolutePath(options.LogDirectory, nameof(options.LogDirectory));
        _stagingRoot = Path.Combine(_pluginsRoot, ".staging");

        Directory.CreateDirectory(_pluginsRoot);
        Directory.CreateDirectory(_dataRoot);

        _log = new PluginManagerLog(logDirectory);
        _stateStore = new PluginStateStore(stateFilePath, _pluginsRoot, _log);
        _packageValidator = new PluginPackageValidator(_hostVersion, _log);

        _log.Info($"PluginManager 创建：HostVersion={_hostVersion}, PluginsRoot={_pluginsRoot}");
    }

    public string HostVersion => _hostVersion;

    /// <summary>插件系统日志文件路径，便于 Host 在错误提示中展示。</summary>
    public string LogFilePath => _log.LogFilePath;

    /// <summary>当前已安装插件快照。</summary>
    public IReadOnlyList<InstalledPlugin> Plugins
    {
        get
        {
            lock (_sync)
            {
                return _plugins.ToList();
            }
        }
    }

    /// <summary>插件运行时即将被卸载（Host 必须在此同步拆 UI）。</summary>
    public event EventHandler<PluginUnloadingEventArgs>? PluginUnloading;

    /// <summary>
    /// 启动流程：清理 pendingDelete 残留 → 读取 plugins.json → 加载所有 enabled=true 的插件。
    /// 单个插件加载失败只记录并跳过，不修改 enabled、不抛异常。
    /// </summary>
    public StartupResult Initialize()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _log.Info("PluginManager.Initialize 开始。");

            var errors = new List<string>();
            try
            {
                CleanupLeftovers(errors);
            }
            catch (Exception ex)
            {
                string message = $"启动清理失败：{ex.GetType().Name}: {ex.Message}";
                _log.Error(message);
                errors.Add(message);
            }

            List<PluginStateRecord> records;
            try
            {
                records = _stateStore.Load();
            }
            catch (Exception ex)
            {
                string message = $"读取插件状态失败：{ex.GetType().Name}: {ex.Message}";
                _log.Error(message);
                errors.Add(message);
                records = new List<PluginStateRecord>();
            }

            _plugins.Clear();
            foreach (PluginStateRecord record in records)
            {
                string name = ResolveDisplayName(record.DirectoryPath!, record.Id!);
                _plugins.Add(new InstalledPlugin(
                    record.Id!,
                    name,
                    record.Version!,
                    record.DirectoryPath!,
                    record.Enabled));
            }

            var loaded = new List<LoadedPlugin>(_plugins.Count);
            foreach (InstalledPlugin plugin in _plugins)
            {
                if (!plugin.Enabled)
                {
                    continue;
                }

                if (!Directory.Exists(plugin.DirectoryPath))
                {
                    string message = $"{plugin.Id}: 插件目录不存在（{plugin.DirectoryPath}）。";
                    _log.Warn(message);
                    errors.Add(message);
                    continue;
                }

                LoadResult result = LoadCore(plugin);
                if (result.Success && result.View is not null)
                {
                    loaded.Add(new LoadedPlugin(plugin, result.View));
                    continue;
                }

                string loadError = $"{plugin.Id}: 加载失败：{result.Error}";
                _log.Warn(loadError);
                errors.Add(loadError);
            }

            _log.Info($"PluginManager.Initialize 完成：成功 {loaded.Count} 个，错误 {errors.Count} 个。");
            return new StartupResult(loaded, errors);
        }
    }

    /// <summary>
    /// 安装 .fkpkg。先完整校验/解压，再按重复安装状态机覆盖；失败不产生残留。
    /// </summary>
    public InstallResult Install(string packagePath)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            ValidatedPackage? package;
            try
            {
                package = _packageValidator.ValidateAndExtract(packagePath, _stagingRoot);
            }
            catch (PluginValidationException ex)
            {
                _log.Warn($"安装校验失败：{ex.Message}");
                return new InstallResult { Success = false, Error = ex.Message };
            }
            catch (Exception ex)
            {
                _log.Error($"安装校验异常：{ex.GetType().Name}: {ex.Message}");
                return new InstallResult
                {
                    Success = false,
                    Error = $"安装包校验失败：{ex.Message}",
                };
            }

            try
            {
                return InstallValidated(package);
            }
            finally
            {
                package.Dispose();
            }
        }
    }

    /// <summary>启用插件：重建 ALC → 加载 → 初始化 → 生成视图 → 状态置 true。</summary>
    public LoadResult Enable(string pluginId)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            InstalledPlugin? plugin = FindCore(pluginId);
            if (plugin is null)
            {
                return LoadResult.Fail(null, $"未找到插件：{pluginId}");
            }

            if (plugin.Runtime is not null)
            {
                return LoadResult.Fail(plugin, "插件已处于启用状态。");
            }

            LoadResult result = LoadCore(plugin);
            if (!result.Success)
            {
                _log.Warn($"插件启用失败：{plugin.Id}: {result.Error}");
                return result;
            }

            plugin.Enabled = true;
            SaveState();
            _log.Info($"插件已启用：{plugin.Id}");
            return result;
        }
    }

    /// <summary>
    /// 禁用插件：触发 UI 拆卸钩子 → Dispose → Unload → enabled=false。
    /// </summary>
    public LoadResult Disable(string pluginId)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            InstalledPlugin? plugin = FindCore(pluginId);
            if (plugin is null)
            {
                return LoadResult.Fail(null, $"未找到插件：{pluginId}");
            }

            try
            {
                if (plugin.Runtime is not null)
                {
                    UnloadRuntime(plugin, raiseUnloadingEvent: true);
                }

                plugin.Enabled = false;
                SaveState();
                _log.Info($"插件已禁用：{plugin.Id}");
                return LoadResult.Ok(plugin);
            }
            catch (Exception ex)
            {
                _log.Error($"禁用插件失败（{pluginId}）：{ex.GetType().Name}: {ex.Message}");
                return LoadResult.Fail(plugin, $"禁用插件失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 卸载插件：先禁用/卸载运行时，再删除目录；被占用时改名 pendingDelete，
    /// 改名也失败则提示“卸载失败，请重启后再试”并保留禁用记录。
    /// </summary>
    public UninstallResult Uninstall(string pluginId)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            InstalledPlugin? plugin = FindCore(pluginId);
            if (plugin is null)
            {
                return new UninstallResult { Success = false, Error = $"未找到插件：{pluginId}" };
            }

            if (plugin.Runtime is not null)
            {
                UnloadRuntime(plugin, raiseUnloadingEvent: true);
            }

            plugin.Enabled = false;
            SaveState();

            string directory = plugin.DirectoryPath;
            if (!Directory.Exists(directory))
            {
                RemoveRecord(plugin);
                SaveState();
                _log.Info($"插件目录已不存在，记录已清理：{plugin.Id}");
                return new UninstallResult { Success = true };
            }

            try
            {
                DeleteDirectoryWithRetry(directory);
                RemoveRecord(plugin);
                SaveState();
                _log.Info($"插件已卸载：{plugin.Id}");
                return new UninstallResult { Success = true };
            }
            catch (Exception deleteEx)
            {
                _log.Warn($"插件目录删除失败（{plugin.Id}）：{deleteEx.GetType().Name}: {deleteEx.Message}");

                try
                {
                    string pendingPath = CreatePendingDeletePath(plugin.Id);
                    MoveDirectoryWithRetry(directory, pendingPath);
                    RemoveRecord(plugin);
                    SaveState();
                    _log.Info($"插件目录已改名为待清理：{pendingPath}");
                    return new UninstallResult { Success = true, PendingDelete = true };
                }
                catch (Exception renameEx)
                {
                    _log.Error($"插件目录重命名失败（{plugin.Id}）：{renameEx.GetType().Name}: {renameEx.Message}");
                    return new UninstallResult
                    {
                        Success = false,
                        Error = "卸载失败，请重启后再试。",
                    };
                }
            }
        }
    }

    public InstalledPlugin? Find(string pluginId)
    {
        lock (_sync)
        {
            return FindCore(pluginId);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (InstalledPlugin plugin in _plugins)
            {
                if (plugin.Runtime is null)
                {
                    continue;
                }

                UnloadRuntime(plugin, raiseUnloadingEvent: false);
            }

            _log.Info("PluginManager 已释放。");
        }
    }

    private InstallResult InstallValidated(ValidatedPackage package)
    {
        PluginManifest manifest = package.Manifest;
        string pluginId = manifest.Id!;
        string targetDirectory = Path.Combine(_pluginsRoot, pluginId);
        InstalledPlugin? existing = FindCore(pluginId);
        bool wasEnabled = existing?.Enabled ?? true;
        bool wasNewInstall = existing is null;
        UserControl? rollbackView = null;
        string? backupDirectory = null;

        if (existing?.Runtime is not null)
        {
            UnloadRuntime(existing, raiseUnloadingEvent: true);
        }

        try
        {
            // 无记录的孤儿目录：改名待清理，避免与正式安装目录冲突。
            if (wasNewInstall && Directory.Exists(targetDirectory))
            {
                string orphanPendingPath = CreatePendingDeletePath(pluginId);
                MoveDirectoryWithRetry(targetDirectory, orphanPendingPath);
                _log.Warn($"发现无记录的同名插件目录，已改名为待清理：{orphanPendingPath}");
            }

            if (Directory.Exists(targetDirectory))
            {
                backupDirectory = Path.Combine(_pluginsRoot, $"{pluginId}_backup_{Guid.NewGuid():N}");
                MoveDirectoryWithRetry(targetDirectory, backupDirectory);
            }

            MoveDirectoryWithRetry(package.StagingDirectory, targetDirectory);
        }
        catch (Exception swapEx)
        {
            _log.Error($"覆盖插件目录失败（{pluginId}）：{swapEx.GetType().Name}: {swapEx.Message}");

            try
            {
                if (!Directory.Exists(targetDirectory)
                    && backupDirectory is not null
                    && Directory.Exists(backupDirectory))
                {
                    MoveDirectoryWithRetry(backupDirectory!, targetDirectory);
                }
            }
            catch (Exception rollbackEx)
            {
                _log.Error($"目录回滚失败（{pluginId}）：{rollbackEx.GetType().Name}: {rollbackEx.Message}");
            }

            if (existing is not null)
            {
                LoadResult reload = LoadCore(existing);
                if (reload.Success)
                {
                    rollbackView = reload.View;
                }
                else
                {
                    _log.Error($"覆盖失败后重新加载旧插件失败（{pluginId}）：{reload.Error}");
                }
            }

            return new InstallResult
            {
                Success = false,
                Error = $"覆盖插件目录失败：{swapEx.Message}",
                Plugin = existing,
                RollbackView = rollbackView,
            };
        }

        if (backupDirectory is not null)
        {
            TryDeleteBackupDirectory(backupDirectory, pluginId);
        }

        if (existing is null)
        {
            existing = new InstalledPlugin(
                pluginId,
                manifest.Name!,
                manifest.Version!,
                targetDirectory,
                wasEnabled);
            _plugins.Add(existing);
        }
        else
        {
            existing.Name = manifest.Name!;
            existing.Version = manifest.Version!;
            existing.DirectoryPath = targetDirectory;
            existing.Enabled = wasEnabled;
        }

        SaveState();

        if (!wasEnabled)
        {
            _log.Info($"插件已安装（保持禁用）：{pluginId} v{manifest.Version}");
            return new InstallResult
            {
                Success = true,
                Plugin = existing,
                WasNewInstall = wasNewInstall,
            };
        }

        LoadResult loadResult = LoadCore(existing);
        if (!loadResult.Success)
        {
            _log.Warn($"插件安装成功但加载失败：{pluginId}: {loadResult.Error}");
            return new InstallResult
            {
                Success = true,
                Plugin = existing,
                LoadFailed = true,
                LoadError = loadResult.Error,
                WasNewInstall = wasNewInstall,
            };
        }

        _log.Info($"插件已安装并加载：{pluginId} v{manifest.Version}");
        return new InstallResult
        {
            Success = true,
            Plugin = existing,
            View = loadResult.View,
            WasNewInstall = wasNewInstall,
        };
    }

    private LoadResult LoadCore(InstalledPlugin plugin)
    {
        if (plugin.Runtime is not null)
        {
            return LoadResult.Fail(plugin, "插件已处于启用状态。");
        }

        if (!Directory.Exists(plugin.DirectoryPath))
        {
            return LoadResult.Fail(plugin, $"插件目录不存在：{plugin.DirectoryPath}");
        }

        PluginLoadContext? loadContext = null;
        IFileKitPlugin? instance = null;

        try
        {
            PluginManifest? manifest = ReadManifestFromDirectory(plugin.DirectoryPath, out string? manifestError);
            if (manifest is null)
            {
                return LoadResult.Fail(plugin, manifestError ?? "读取 manifest.json 失败。");
            }

            if (!PluginManifestValidator.TryValidate(
                    manifest,
                    _hostVersion,
                    out string validationError,
                    out string entryAssemblyRelativePath))
            {
                return LoadResult.Fail(plugin, $"manifest 校验失败：{validationError}");
            }

            string libsRoot = Path.Combine(plugin.DirectoryPath, "libs");
            string entryAssemblyPath = Path.GetFullPath(Path.Combine(
                plugin.DirectoryPath,
                entryAssemblyRelativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!PathRules.IsUnderDirectory(entryAssemblyPath, libsRoot))
            {
                return LoadResult.Fail(plugin, "manifest.entryAssembly 不在 libs/ 目录内。");
            }

            if (!File.Exists(entryAssemblyPath))
            {
                return LoadResult.Fail(plugin, $"入口程序集不存在：{entryAssemblyPath}");
            }

            loadContext = new PluginLoadContext(plugin.Id, entryAssemblyPath, libsRoot);
            Assembly assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);

            Type? entryType = assembly.GetType(manifest.EntryType!, throwOnError: false, ignoreCase: false);
            if (entryType is null)
            {
                throw new InvalidOperationException($"找不到入口类型：{manifest.EntryType}");
            }

            if (!typeof(IFileKitPlugin).IsAssignableFrom(entryType))
            {
                throw new InvalidOperationException($"入口类型未实现 IFileKitPlugin：{manifest.EntryType}");
            }

            if (entryType.IsAbstract || entryType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException(
                    $"入口类型必须是可实例化的公开类型且带公共无参构造函数：{manifest.EntryType}");
            }

            instance = Activator.CreateInstance(entryType) as IFileKitPlugin
                ?? throw new InvalidOperationException($"无法创建插件实例：{manifest.EntryType}");

            var pluginContext = new PluginContext(
                _hostVersion,
                plugin.Id,
                Path.Combine(_dataRoot, plugin.Id));

            instance.Initialize(pluginContext);
            UserControl view = instance.CreateMainView()
                ?? throw new InvalidOperationException("插件 CreateMainView() 返回了 null。");

            plugin.Runtime = new PluginRuntime(loadContext, instance, _log);
            _log.Info($"插件加载成功：{plugin.Id} v{manifest.Version}");
            return LoadResult.Ok(plugin, view);
        }
        catch (Exception ex)
        {
            string message = ex is InvalidOperationException
                ? ex.Message
                : $"{ex.GetType().Name}: {ex.Message}";

            _log.Error($"插件加载失败（{plugin.Id}）：{message}");

            if (instance is not null)
            {
                try
                {
                    instance.Dispose();
                }
                catch
                {
                    // 清理阶段忽略二次异常。
                }
            }

            if (loadContext is not null)
            {
                try
                {
                    loadContext.Unload();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                catch
                {
                    // 清理阶段忽略二次异常。
                }
            }

            return LoadResult.Fail(plugin, message);
        }
    }

    private void CleanupLeftovers(List<string> errors)
    {
        string[] pendingDirectories;
        try
        {
            pendingDirectories = Directory.GetDirectories(
                _pluginsRoot,
                "*_pending_delete_*",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            string message = $"扫描 pendingDelete 目录失败：{ex.Message}";
            _log.Warn(message);
            errors.Add(message);
            return;
        }

        foreach (string directory in pendingDirectories)
        {
            try
            {
                DeleteDirectoryWithRetry(directory);
                _log.Info($"已清理待删除目录：{directory}");
            }
            catch (Exception ex)
            {
                string message = $"待删除目录清理失败（{Path.GetFileName(directory)}）：{ex.Message}";
                _log.Warn(message);
                errors.Add(message);
            }
        }

        if (Directory.Exists(_stagingRoot))
        {
            try
            {
                DeleteDirectoryWithRetry(_stagingRoot);
                _log.Info("已清理残留的临时安装目录。");
            }
            catch (Exception ex)
            {
                string message = $"临时安装目录清理失败：{ex.Message}";
                _log.Warn(message);
                errors.Add(message);
            }
        }

        try
        {
            foreach (string backupDirectory in Directory.GetDirectories(
                         _pluginsRoot,
                         "*_backup_*",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    DeleteDirectoryWithRetry(backupDirectory);
                    _log.Info($"已清理残留备份目录：{backupDirectory}");
                }
                catch (Exception ex)
                {
                    _log.Warn($"备份目录清理失败（{Path.GetFileName(backupDirectory)}）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"扫描备份目录失败：{ex.Message}");
        }
    }

    private void TryDeleteBackupDirectory(string backupDirectory, string pluginId)
    {
        try
        {
            DeleteDirectoryWithRetry(backupDirectory);
        }
        catch (Exception deleteEx)
        {
            _log.Warn($"删除备份目录失败（{backupDirectory}）：{deleteEx.GetType().Name}: {deleteEx.Message}");
            try
            {
                string pendingPath = CreatePendingDeletePath(pluginId);
                MoveDirectoryWithRetry(backupDirectory, pendingPath);
                _log.Warn($"备份目录已改名为待清理：{pendingPath}");
            }
            catch (Exception renameEx)
            {
                _log.Error($"备份目录清理失败（{backupDirectory}）：{renameEx.GetType().Name}: {renameEx.Message}");
            }
        }
    }

    private void RaisePluginUnloading(InstalledPlugin plugin)
    {
        EventHandler<PluginUnloadingEventArgs>? handler = PluginUnloading;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new PluginUnloadingEventArgs(plugin));
        }
        catch (Exception ex)
        {
            _log.Error($"PluginUnloading 事件处理异常（{plugin.Id}）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 触发 UI 拆卸钩子 → Dispose 实例 → Unload ALC → 强制回收，尽量释放程序集文件句柄。
    /// 用独立方法拆出运行时引用，确保后续 GC 能真正回收到 ALC。
    /// </summary>
    private void UnloadRuntime(InstalledPlugin plugin, bool raiseUnloadingEvent)
    {
        if (plugin.Runtime is null)
        {
            return;
        }

        if (raiseUnloadingEvent)
        {
            RaisePluginUnloading(plugin);
        }

        DetachAndDisposeRuntime(plugin);

        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch
        {
            // 回收失败不影响卸载流程。
        }
    }

    private static void DetachAndDisposeRuntime(InstalledPlugin plugin)
    {
        PluginRuntime? runtime = plugin.Runtime;
        plugin.Runtime = null;
        runtime?.Dispose();
    }

    private InstalledPlugin? FindCore(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        return _plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveRecord(InstalledPlugin plugin)
    {
        _plugins.Remove(plugin);
    }

    private void SaveState()
    {
        var records = new List<PluginStateRecord>(_plugins.Count);
        foreach (InstalledPlugin plugin in _plugins)
        {
            records.Add(new PluginStateRecord
            {
                Id = plugin.Id,
                Version = plugin.Version,
                DirectoryPath = plugin.DirectoryPath,
                Enabled = plugin.Enabled,
            });
        }

        _stateStore.Save(records);
    }

    private string ResolveDisplayName(string directory, string fallback)
    {
        try
        {
            PluginManifest? manifest = ReadManifestFromDirectory(directory, out _);
            if (!string.IsNullOrWhiteSpace(manifest?.Name))
            {
                return manifest!.Name!;
            }
        }
        catch
        {
            // 名称解析失败时回退到插件 ID。
        }

        return fallback;
    }

    private PluginManifest? ReadManifestFromDirectory(string directory, out string? error)
    {
        error = null;
        try
        {
            string manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                error = "插件目录缺少 manifest.json。";
                return null;
            }

            PluginManifest? manifest = PluginManifestParser.Parse(File.ReadAllBytes(manifestPath), out string? parseError);
            if (manifest is null)
            {
                error = parseError ?? "manifest.json 解析失败。";
                return null;
            }

            return manifest;
        }
        catch (Exception ex)
        {
            error = $"读取 manifest.json 失败：{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private string CreatePendingDeletePath(string pluginId)
    {
        string baseName = $"{pluginId}_pending_delete_{DateTime.Now:yyyyMMddHHmmssfff}";
        string candidate = Path.Combine(_pluginsRoot, baseName);
        int suffix = 1;
        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(_pluginsRoot, $"{baseName}_{suffix++}");
        }

        return candidate;
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < FileOperationRetryCount - 1)
            {
                Thread.Sleep(FileOperationRetryDelayMilliseconds);
            }
            catch (UnauthorizedAccessException) when (attempt < FileOperationRetryCount - 1)
            {
                Thread.Sleep(FileOperationRetryDelayMilliseconds);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string directory)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < FileOperationRetryCount - 1)
            {
                Thread.Sleep(FileOperationRetryDelayMilliseconds);
            }
            catch (UnauthorizedAccessException) when (attempt < FileOperationRetryCount - 1)
            {
                Thread.Sleep(FileOperationRetryDelayMilliseconds);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
