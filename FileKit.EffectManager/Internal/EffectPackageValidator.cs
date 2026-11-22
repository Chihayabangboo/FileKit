using System.IO.Compression;
using FileKit.PluginManager.Internal;

namespace FileKit.EffectManager.Internal;

internal sealed class EffectValidationException : Exception
{
    public EffectValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 校验通过并解压到 staging 的特效包。Dispose 时清理 staging（若尚未被 Commit 移走）。
/// </summary>
internal sealed class ExtractedEffectPackage : IDisposable
{
    private int _disposed;

    public ExtractedEffectPackage(
        EffectManifestInfo manifest,
        string stagingDirectory,
        string entryAssemblyRelativePath)
    {
        Manifest = manifest;
        StagingDirectory = stagingDirectory;
        EntryAssemblyRelativePath = entryAssemblyRelativePath;
    }

    public EffectManifestInfo Manifest { get; }

    public string StagingDirectory { get; }

    public string EntryAssemblyRelativePath { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不抛；LoadDevEffects 启动时会统一清理 .staging。
        }
    }
}

/// <summary>
/// .fkeffect 校验 + 解压。校验顺序：ZIP/根 manifest → 必填字段/版本 → Zip Slip/布局 → 解压 → 入口存在。
/// 任一步失败都会删除临时目录并抛 <see cref="EffectValidationException"/>（由 EffectManager 转为 Error）。
/// </summary>
internal sealed class EffectPackageValidator
{
    private const long MaxManifestBytes = 4 * 1024 * 1024;

    private readonly string _hostVersion;
    private readonly EffectLog _log;

    public EffectPackageValidator(string hostVersion, EffectLog log)
    {
        _hostVersion = hostVersion;
        _log = log;
    }

    public ExtractedEffectPackage ValidateAndExtract(string? packagePath, string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new EffectValidationException("特效包路径为空。");
        }

        string fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath))
        {
            throw new EffectValidationException($"特效包不存在：{fullPackagePath}");
        }

        string stagingRootFull = Path.GetFullPath(stagingRoot);
        string stagingDirectory = Path.Combine(stagingRootFull, Guid.NewGuid().ToString("N"));

        try
        {
            using var archive = ZipFile.OpenRead(fullPackagePath);
            if (archive.Entries.Count == 0)
            {
                throw new EffectValidationException("特效包为空，缺少 manifest.json。");
            }

            ZipArchiveEntry manifestEntry = FindRootManifestEntry(archive);
            EffectManifestInfo manifest = ReadManifest(manifestEntry);

            List<(ZipArchiveEntry Entry, string RelativePath)> fileEntries =
                ValidateAndCollectFileEntries(archive, manifest.EntryAssembly, stagingDirectory);

            Directory.CreateDirectory(stagingDirectory);
            foreach ((ZipArchiveEntry entry, string relativePath) in fileEntries)
            {
                ExtractEntry(entry, stagingDirectory, relativePath);
            }

            string entryAssemblyPath = Path.GetFullPath(Path.Combine(
                stagingDirectory,
                manifest.EntryAssembly.Replace('/', Path.DirectorySeparatorChar)));

            string libsDirectory = Path.Combine(stagingDirectory, "libs");
            if (!PathRules.IsUnderDirectory(entryAssemblyPath, libsDirectory))
            {
                throw new EffectValidationException("manifest.entryAssembly 规范化后不在 libs/ 目录内。");
            }

            if (!File.Exists(entryAssemblyPath))
            {
                throw new EffectValidationException($"安装包中缺少入口程序集：{manifest.EntryAssembly}");
            }

            _log.Info($"特效包校验通过：{fullPackagePath} → {stagingDirectory}");
            return new ExtractedEffectPackage(manifest, stagingDirectory, manifest.EntryAssembly);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    private static ZipArchiveEntry FindRootManifestEntry(ZipArchive archive)
    {
        ZipArchiveEntry? found = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/').TrimEnd('/');
            if (!string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null)
            {
                throw new EffectValidationException("特效包根目录存在多个 manifest.json。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new EffectValidationException("manifest.json 不能是目录。");
            }

            found = entry;
        }

        if (found is null)
        {
            throw new EffectValidationException("特效包根目录缺少 manifest.json。");
        }

        return found;
    }

    private EffectManifestInfo ReadManifest(ZipArchiveEntry manifestEntry)
    {
        if (manifestEntry.Length > MaxManifestBytes)
        {
            throw new EffectValidationException("manifest.json 过大。");
        }

        using Stream stream = manifestEntry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        EffectManifest? manifest = EffectManifestParser.Parse(memory.ToArray(), out string? parseError);
        if (manifest is null)
        {
            throw new EffectValidationException(parseError ?? "manifest.json 解析失败。");
        }

        if (!EffectManifestValidator.TryValidate(manifest, _hostVersion, out string validationError, out EffectManifestInfo? info))
        {
            throw new EffectValidationException(validationError);
        }

        return info!;
    }

    private static List<(ZipArchiveEntry Entry, string RelativePath)> ValidateAndCollectFileEntries(
        ZipArchive archive,
        string entryAssemblyRelativePath,
        string stagingDirectory)
    {
        var collected = new List<(ZipArchiveEntry Entry, string RelativePath)>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool entryAssemblyFound = false;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string rawName = entry.FullName;
            bool isDirectory = string.IsNullOrEmpty(entry.Name)
                || rawName.EndsWith('/')
                || rawName.EndsWith('\\');

            string pathPart = rawName.TrimEnd('/', '\\');
            if (!PathRules.TryNormalizeRelativePath(pathPart, out string relativePath, out string pathError))
            {
                throw new EffectValidationException($"安装包含非法路径“{rawName}”：{pathError}");
            }

            if (string.Equals(relativePath, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                if (isDirectory)
                {
                    throw new EffectValidationException("manifest.json 不能是目录。");
                }

                // manifest 也要解压到特效目录根，供后续加载/展示时读取。
                string manifestDestinationPath = Path.GetFullPath(Path.Combine(
                    stagingDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathRules.IsUnderDirectory(manifestDestinationPath, stagingDirectory))
                {
                    throw new EffectValidationException($"安装包含非法路径（Zip Slip）：{rawName}");
                }

                collected.Add((entry, relativePath));
                continue;
            }

            if (isDirectory)
            {
                if (!string.Equals(relativePath, "libs", StringComparison.OrdinalIgnoreCase)
                    && !relativePath.StartsWith("libs/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new EffectValidationException(
                        $"安装包根目录只允许 manifest.json，其余内容必须位于 libs/ 目录内（发现目录：{relativePath}）。");
                }

                continue;
            }

            if (!relativePath.StartsWith("libs/", StringComparison.OrdinalIgnoreCase))
            {
                throw new EffectValidationException(
                    $"特效文件必须位于 libs/ 目录内（发现：{relativePath}）。");
            }

            string destinationPath = Path.GetFullPath(Path.Combine(
                stagingDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!PathRules.IsUnderDirectory(destinationPath, stagingDirectory))
            {
                throw new EffectValidationException($"安装包含非法路径（Zip Slip）：{rawName}");
            }

            if (!seenPaths.Add(relativePath))
            {
                throw new EffectValidationException($"安装包中存在重复文件：{relativePath}");
            }

            if (string.Equals(relativePath, entryAssemblyRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                entryAssemblyFound = true;
            }

            collected.Add((entry, relativePath));
        }

        if (!entryAssemblyFound)
        {
            throw new EffectValidationException(
                $"manifest.entryAssembly 指向的文件不在安装包中：{entryAssemblyRelativePath}");
        }

        return collected;
    }

    private static void ExtractEntry(
        ZipArchiveEntry entry,
        string stagingDirectory,
        string relativePath)
    {
        string destinationPath = Path.GetFullPath(Path.Combine(
            stagingDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using Stream input = entry.Open();
        using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        input.CopyTo(output);
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"清理特效临时目录失败（{directory}）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
