using System.Reflection;
using FileKit.Contracts.Effects;
using FileKit.EffectManager.Internal;
using FileKit.PluginManager.Internal;

namespace FileKit.EffectManager;

/// <summary>
/// 特效系统门面：扫描、manifest/包校验、ALC 加载、两阶段安装（staging → 目录交换）、
/// 卸载与 pendingDelete、启动清理。只负责加载/实例化；Attach/Detach 与 UI 由 Host 负责。
/// 无 effects.json：目录存在即已安装。所有公共方法除参数校验外不向外抛异常。
/// 所有方法预期在 UI 线程同步调用（内部仍有锁保护）。
/// </summary>
public sealed class EffectManager : IDisposable
{
    private const int FileOperationRetryCount = 3;
    private const int FileOperationRetryDelayMilliseconds = 50;

    private readonly object _sync = new();
    private readonly HashSet<EffectRuntime> _runtimes = new();
    private readonly EffectLog _log;
    private readonly string _hostVersion;
    private readonly string _builtinRoot;
    private readonly string _devRoot;
    private readonly string _stagingRoot;
    private bool _disposed;

    public EffectManager(EffectManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!VersionRules.IsThreePart(options.HostVersion))
        {
            throw new ArgumentException(
                "HostVersion 必须是三段式版本号（如 1.0.0）。",
                nameof(options));
        }

        _hostVersion = options.HostVersion;
        _builtinRoot = PathRules.NormalizeAbsolutePath(
            options.BuiltinEffectsDirectory,
            nameof(options.BuiltinEffectsDirectory));
        _devRoot = PathRules.NormalizeAbsolutePath(
            options.DevEffectsDirectory,
            nameof(options.DevEffectsDirectory));
        _stagingRoot = Path.Combine(_devRoot, ".staging");

        // 构造阶段只规范化路径，不创建、不扫描 dev 目录（非开发者模式绝不触碰）。
        _log = new EffectLog(options.LogDirectory);
        _log.Info($"EffectManager 创建：HostVersion={_hostVersion}, Builtin={_builtinRoot}, Dev={_devRoot}");
    }

    public string LogFilePath => _log.LogFilePath;

    public EffectLog Log => _log;

    /// <summary>
    /// 第一步安装校验：只校验 + 解压到 staging，对已安装特效零副作用。
    /// 失败返回 Success=false + Error，不抛异常。
    /// </summary>
    public EffectPackageValidationResult ValidateEffectPackage(string packagePath)
    {
        ThrowIfDisposed();

        try
        {
            var validator = new EffectPackageValidator(_hostVersion, _log);
            ExtractedEffectPackage? extracted = null;
            try
            {
                extracted = validator.ValidateAndExtract(packagePath, _stagingRoot);
                var package = new EffectPackage(extracted.Manifest, extracted.StagingDirectory);
                extracted = null; // 所有权转移给 EffectPackage
                return EffectPackageValidationResult.Ok(package);
            }
            finally
            {
                extracted?.Dispose();
            }
        }
        catch (EffectValidationException ex)
        {
            _log.Warn($"特效包校验失败：{ex.Message}");
            return EffectPackageValidationResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error($"特效包校验异常：{ex.GetType().Name}: {ex.Message}");
            return EffectPackageValidationResult.Fail($"特效包校验失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 第二步安装：把 staging 目录交换到 dev\{effectId}（旧目录先备份，成功后再删除备份）。
    /// 调用前 Host 必须已 Detach + 释放旧运行时。失败回滚旧目录，不抛异常。
    /// </summary>
    public EffectInstallResult CommitDevEffectInstall(EffectPackage package)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(package);

        lock (_sync)
        {
            EffectManifestInfo manifest = package.Manifest;
            string effectId = manifest.EffectId;
            string stagingDirectory = package.StagingDirectory;

            if (!PathRules.IsSafePluginId(effectId))
            {
                return new EffectInstallResult
                {
                    Success = false,
                    Error = $"manifest.effectId 非法：{effectId}",
                    Manifest = manifest,
                };
            }

            try
            {
                if (!Directory.Exists(stagingDirectory))
                {
                    return new EffectInstallResult
                    {
                        Success = false,
                        Error = "安装暂存目录不存在（可能已提交或已被清理）。",
                        Manifest = manifest,
                    };
                }

                Directory.CreateDirectory(_devRoot);

                string targetDirectory = Path.Combine(_devRoot, effectId);
                string? backupDirectory = null;
                bool pendingDelete = false;

                if (Directory.Exists(targetDirectory))
                {
                    backupDirectory = CreateBackupPath(effectId);
                    MoveDirectoryWithRetry(targetDirectory, backupDirectory);
                }

                try
                {
                    MoveDirectoryWithRetry(stagingDirectory, targetDirectory);
                }
                catch (Exception moveEx)
                {
                    _log.Error($"覆盖特效目录失败（{effectId}）：{moveEx.GetType().Name}: {moveEx.Message}");

                    try
                    {
                        if (!Directory.Exists(targetDirectory)
                            && backupDirectory is not null
                            && Directory.Exists(backupDirectory))
                        {
                            MoveDirectoryWithRetry(backupDirectory, targetDirectory);
                            backupDirectory = null;
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        _log.Error($"特效目录回滚失败（{effectId}）：{rollbackEx.GetType().Name}: {rollbackEx.Message}");
                    }

                    return new EffectInstallResult
                    {
                        Success = false,
                        Error = $"覆盖特效目录失败：{moveEx.Message}",
                        Manifest = manifest,
                    };
                }

                if (backupDirectory is not null)
                {
                    if (!TryDeleteDirectory(backupDirectory))
                    {
                        try
                        {
                            string pendingPath = CreatePendingDeletePath(effectId);
                            MoveDirectoryWithRetry(backupDirectory, pendingPath);
                            pendingDelete = true;
                            _log.Warn($"特效备份目录已改名为待清理：{pendingPath}");
                        }
                        catch (Exception renameEx)
                        {
                            _log.Error($"特效备份目录清理失败（{backupDirectory}）：{renameEx.GetType().Name}: {renameEx.Message}");
                        }
                    }
                }

                _log.Info($"特效已安装：{effectId} → {targetDirectory}");
                return new EffectInstallResult
                {
                    Success = true,
                    Manifest = manifest,
                    PendingDelete = pendingDelete,
                };
            }
            catch (Exception ex)
            {
                _log.Error($"安装特效失败（{effectId}）：{ex.GetType().Name}: {ex.Message}");
                return new EffectInstallResult
                {
                    Success = false,
                    Error = $"安装特效失败：{ex.GetType().Name}: {ex.Message}",
                    Manifest = manifest,
                };
            }
        }
    }

    /// <summary>扫描并加载内置特效目录（不存在时返回空列表，不抛异常）。</summary>
    public IReadOnlyList<EffectLoadResult> LoadBuiltinEffects()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            if (!Directory.Exists(_builtinRoot))
            {
                _log.Warn($"内置特效目录不存在，跳过：{_builtinRoot}");
                return Array.Empty<EffectLoadResult>();
            }

            return LoadFromRoot(_builtinRoot, EffectOrigin.Builtin);
        }
    }

    /// <summary>
    /// 清理 dev 目录残留（pendingDelete/.staging/backup）后扫描并加载额外特效目录。
    /// 仅在开发者模式由 Host 调用；目录不存在时返回空列表，不创建目录。
    /// </summary>
    public IReadOnlyList<EffectLoadResult> LoadDevEffects()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            CleanupDevLeftovers();

            if (!Directory.Exists(_devRoot))
            {
                _log.Info($"额外特效目录不存在，跳过：{_devRoot}");
                return Array.Empty<EffectLoadResult>();
            }

            return LoadFromRoot(_devRoot, EffectOrigin.Dev);
        }
    }

    /// <summary>
    /// 加载单个已安装的额外特效（安装/重装后使用）。目录不存在或 id 非法时返回 null。
    /// </summary>
    public EffectLoadResult? LoadDevEffect(string effectId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(effectId))
        {
            return null;
        }

        if (!PathRules.IsSafePluginId(effectId))
        {
            _log.Warn($"LoadDevEffect 忽略非法特效 ID：{effectId}");
            return null;
        }

        lock (_sync)
        {
            string directory = Path.Combine(_devRoot, effectId);
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return LoadSingleDirectory(directory, EffectOrigin.Dev);
        }
    }

    /// <summary>
    /// 卸载额外特效：先释放仍跟踪的运行时，再删除 dev\{effectId}；被占用时改名 pendingDelete。
    /// 内置特效、越界/非法 ID 一律拒绝。
    /// </summary>
    public EffectUninstallResult UninstallDevEffect(string effectId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(effectId))
        {
            return new EffectUninstallResult { Success = false, Error = "特效 ID 为空。" };
        }

        if (!PathRules.IsSafePluginId(effectId))
        {
            return new EffectUninstallResult { Success = false, Error = $"特效 ID 非法：{effectId}" };
        }

        lock (_sync)
        {
            string builtinDirectory = Path.Combine(_builtinRoot, effectId);
            string targetDirectory = Path.Combine(_devRoot, effectId);

            if (!PathRules.IsUnderDirectory(targetDirectory, _devRoot))
            {
                return new EffectUninstallResult { Success = false, Error = $"特效目录越界：{effectId}" };
            }

            if (!Directory.Exists(targetDirectory))
            {
                if (Directory.Exists(builtinDirectory))
                {
                    return new EffectUninstallResult { Success = false, Error = $"内置特效不可卸载：{effectId}" };
                }

                return new EffectUninstallResult { Success = false, Error = $"未找到额外特效：{effectId}" };
            }

            // 防御：调用方未释放运行时也不能泄漏（正常流程 Host 已提前 Detach + Dispose）。
            ReleaseRuntimesFor(effectId);

            try
            {
                DeleteDirectoryWithRetry(targetDirectory);
                _log.Info($"特效已卸载：{effectId}");
                return new EffectUninstallResult { Success = true };
            }
            catch (Exception deleteEx)
            {
                _log.Warn($"特效目录删除失败（{effectId}）：{deleteEx.GetType().Name}: {deleteEx.Message}");

                try
                {
                    string pendingPath = CreatePendingDeletePath(effectId);
                    MoveDirectoryWithRetry(targetDirectory, pendingPath);
                    _log.Info($"特效目录已改名为待清理：{pendingPath}");
                    return new EffectUninstallResult { Success = true, PendingDelete = true };
                }
                catch (Exception renameEx)
                {
                    _log.Error($"特效目录重命名失败（{effectId}）：{renameEx.GetType().Name}: {renameEx.Message}");
                    return new EffectUninstallResult { Success = false, Error = "卸载失败，请重启后再试。" };
                }
            }
        }
    }

    public void Dispose()
    {
        List<EffectRuntime> runtimes;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runtimes = _runtimes.ToList();
            _runtimes.Clear();
        }

        foreach (EffectRuntime runtime in runtimes)
        {
            try
            {
                runtime.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error($"释放特效运行时异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        _log.Info("EffectManager 已释放。");
    }

    private IReadOnlyList<EffectLoadResult> LoadFromRoot(string root, EffectOrigin origin)
    {
        var results = new List<EffectLoadResult>();

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _log.Error($"扫描特效目录失败（{root}）：{ex.GetType().Name}: {ex.Message}");
            return results;
        }

        foreach (string directory in directories)
        {
            string directoryName = Path.GetFileName(directory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));

            if (IsIgnoredDirectoryName(directoryName))
            {
                continue;
            }

            try
            {
                results.Add(LoadSingleDirectory(directory, origin));
            }
            catch (Exception ex)
            {
                _log.Error($"加载特效目录异常（{directoryName}）：{ex.GetType().Name}: {ex.Message}");
                results.Add(CreateFailureResult(
                    directoryName,
                    directoryName,
                    string.Empty,
                    origin,
                    directory,
                    null,
                    $"加载异常：{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return results
            .OrderBy(result => result.EffectId, StringComparer.Ordinal)
            .ToList();
    }

    private EffectLoadResult LoadSingleDirectory(string directory, EffectOrigin origin)
    {
        string directoryName = Path.GetFileName(directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));

        EffectManifest? manifest = null;
        EffectManifestInfo? info = null;
        string effectId = directoryName;
        string name = directoryName;
        string version = string.Empty;

        PluginLoadContext? loadContext = null;
        IFileKitEffect? instance = null;

        try
        {
            string manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return CreateFailureResult(
                    effectId,
                    name,
                    version,
                    origin,
                    directory,
                    null,
                    "特效目录缺少 manifest.json。");
            }

            manifest = EffectManifestParser.Parse(File.ReadAllBytes(manifestPath), out string? parseError);
            if (manifest is null)
            {
                return CreateFailureResult(
                    effectId,
                    name,
                    version,
                    origin,
                    directory,
                    null,
                    parseError ?? "manifest.json 解析失败。");
            }

            effectId = string.IsNullOrWhiteSpace(manifest.EffectId) ? directoryName : manifest.EffectId!;
            name = string.IsNullOrWhiteSpace(manifest.Name) ? directoryName : manifest.Name!;
            version = manifest.Version ?? string.Empty;

            if (!EffectManifestValidator.TryValidate(manifest, _hostVersion, out string validationError, out info))
            {
                return CreateFailureResult(
                    effectId,
                    name,
                    version,
                    origin,
                    directory,
                    null,
                    $"manifest 校验失败：{validationError}");
            }

            effectId = info!.EffectId;
            name = info.Name;
            version = info.Version;

            if (!string.Equals(directoryName, effectId, StringComparison.OrdinalIgnoreCase))
            {
                return CreateFailureResult(
                    effectId,
                    name,
                    version,
                    origin,
                    directory,
                    info,
                    $"特效目录名与 manifest.effectId 不一致：{directoryName} != {effectId}");
            }

            string libsRoot = Path.Combine(directory, "libs");
            string entryAssemblyPath = Path.GetFullPath(Path.Combine(
                directory,
                info.EntryAssembly.Replace('/', Path.DirectorySeparatorChar)));

            if (!PathRules.IsUnderDirectory(entryAssemblyPath, libsRoot))
            {
                return CreateFailureResult(
                    effectId,
                    name,
                    version,
                    origin,
                    directory,
                    info,
                    "manifest.entryAssembly 不在 libs/ 目录内。");
            }

            if (!File.Exists(entryAssemblyPath))
            {
                return CreateFailureResult(
                    effectId,
                    name,
                    version,
                    origin,
                    directory,
                    info,
                    $"入口程序集不存在：{entryAssemblyPath}");
            }

            // 复用 M2 PluginLoadContext（其 ALC 名称固定为 FileKit.Plugin.{pluginId}），
            // 因此本程序集实际 ALC 名为 FileKit.Plugin.effect:{effectId}。
            loadContext = new PluginLoadContext("effect:" + effectId, entryAssemblyPath, libsRoot);
            Assembly assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);

            Type? entryType = assembly.GetType(info.EntryType, throwOnError: false, ignoreCase: false);
            if (entryType is null)
            {
                throw new InvalidOperationException($"找不到入口类型：{info.EntryType}");
            }

            if (!typeof(IFileKitEffect).IsAssignableFrom(entryType))
            {
                throw new InvalidOperationException($"入口类型未实现 IFileKitEffect：{info.EntryType}");
            }

            if (!entryType.IsVisible
                || entryType.IsAbstract
                || entryType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException(
                    $"入口类型必须是可实例化的公开类型且带公共无参构造函数：{info.EntryType}");
            }

            instance = Activator.CreateInstance(entryType) as IFileKitEffect
                ?? throw new InvalidOperationException($"无法创建特效实例：{info.EntryType}");

            if (!string.Equals(instance.EffectId, info.EffectId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"实例 EffectId 与 manifest 不一致：{instance.EffectId} != {info.EffectId}");
            }

            if (instance.Target != info.Target)
            {
                throw new InvalidOperationException(
                    $"实例 Target 与 manifest 不一致：{instance.Target} != {info.Target}");
            }

            var runtime = new EffectRuntime(effectId, instance, loadContext, _log, OnRuntimeDisposed);
            instance = null;
            loadContext = null;

            lock (_sync)
            {
                _runtimes.Add(runtime);
            }

            _log.Info($"特效加载成功：{effectId} [{origin}] v{info.Version}");
            return new EffectLoadResult
            {
                EffectId = effectId,
                Name = name,
                Version = version,
                Origin = origin,
                DirectoryPath = directory,
                Manifest = info,
                Instance = runtime.Instance,
                Runtime = runtime,
            };
        }
        catch (Exception ex)
        {
            string message = ex is InvalidOperationException
                ? ex.Message
                : $"{ex.GetType().Name}: {ex.Message}";

            _log.Error($"特效加载失败（{directoryName}，{origin}）：{message}");

            if (instance is not null)
            {
                try
                {
                    instance.Dispose();
                }
                catch (Exception disposeEx)
                {
                    _log.Warn($"加载失败后释放特效实例异常：{disposeEx.GetType().Name}: {disposeEx.Message}");
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

            return CreateFailureResult(effectId, name, version, origin, directory, info, message);
        }
    }

    private static EffectLoadResult CreateFailureResult(
        string effectId,
        string name,
        string version,
        EffectOrigin origin,
        string? directoryPath,
        EffectManifestInfo? manifest,
        string error) =>
        new()
        {
            EffectId = string.IsNullOrWhiteSpace(effectId) ? "(unknown)" : effectId,
            Name = string.IsNullOrWhiteSpace(name) ? effectId : name,
            Version = version,
            Origin = origin,
            DirectoryPath = directoryPath,
            Manifest = manifest,
            Error = error,
        };

    private void CleanupDevLeftovers()
    {
        if (!Directory.Exists(_devRoot))
        {
            return;
        }

        try
        {
            foreach (string directory in Directory.GetDirectories(
                         _devRoot,
                         "*_pending_delete_*",
                         SearchOption.TopDirectoryOnly))
            {
                TryCleanupDirectory(directory);
            }

            foreach (string directory in Directory.GetDirectories(
                         _devRoot,
                         "*_backup_*",
                         SearchOption.TopDirectoryOnly))
            {
                TryCleanupDirectory(directory);
            }

            string stagingDirectory = Path.Combine(_devRoot, ".staging");
            if (Directory.Exists(stagingDirectory))
            {
                TryCleanupDirectory(stagingDirectory);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"清理额外特效残留失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TryCleanupDirectory(string directory)
    {
        if (!PathRules.IsUnderDirectory(directory, _devRoot))
        {
            _log.Warn($"跳过越界的残留目录：{directory}");
            return;
        }

        try
        {
            DeleteDirectoryWithRetry(directory);
            _log.Info($"已清理特效残留目录：{directory}");
        }
        catch (Exception ex)
        {
            _log.Warn($"特效残留目录清理失败（{directory}）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ReleaseRuntimesFor(string effectId)
    {
        List<EffectRuntime> matches;
        lock (_sync)
        {
            matches = _runtimes
                .Where(runtime => string.Equals(runtime.EffectId, effectId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (EffectRuntime runtime in matches)
        {
            try
            {
                runtime.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error($"释放特效运行时异常（{effectId}）：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void OnRuntimeDisposed(EffectRuntime runtime)
    {
        lock (_sync)
        {
            _runtimes.Remove(runtime);
        }
    }

    private static bool IsIgnoredDirectoryName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        if (name.StartsWith('.'))
        {
            return true;
        }

        return name.Contains("_pending_delete_", StringComparison.OrdinalIgnoreCase)
            || name.Contains("_backup_", StringComparison.OrdinalIgnoreCase);
    }

    private string CreatePendingDeletePath(string effectId)
    {
        string baseName = $"{effectId}_pending_delete_{DateTime.Now:yyyyMMddHHmmssfff}";
        string candidate = Path.Combine(_devRoot, baseName);
        int suffix = 1;
        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(_devRoot, $"{baseName}_{suffix++}");
        }

        return candidate;
    }

    private string CreateBackupPath(string effectId) =>
        Path.Combine(_devRoot, $"{effectId}_backup_{Guid.NewGuid():N}");

    private bool TryDeleteDirectory(string directory)
    {
        try
        {
            DeleteDirectoryWithRetry(directory);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"删除特效目录失败（{directory}）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
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
