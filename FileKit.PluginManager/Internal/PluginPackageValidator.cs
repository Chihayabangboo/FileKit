using System.IO.Compression;

namespace FileKit.PluginManager.Internal;

internal sealed class PluginValidationException : Exception
{
    public PluginValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 通过校验并解压到临时目录的插件包。Dispose 时清理解压目录（若尚未被移动）。
/// </summary>
internal sealed class ValidatedPackage : IDisposable
{
    public ValidatedPackage(
        PluginManifest manifest,
        string stagingDirectory,
        string entryAssemblyPath)
    {
        Manifest = manifest;
        StagingDirectory = stagingDirectory;
        EntryAssemblyPath = entryAssemblyPath;
    }

    public PluginManifest Manifest { get; }

    public string StagingDirectory { get; }

    public string EntryAssemblyPath { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不再抛异常；启动时会统一清理 .staging。
        }
    }
}

/// <summary>
/// .fkpkg 校验 + 解压。校验顺序：ZIP/根 manifest → 必填字段/版本 → Zip Slip/布局 → 解压 → 入口存在。
/// 任一步失败都会回滚删除临时目录。
/// </summary>
internal sealed class PluginPackageValidator
{
    private const long MaxManifestBytes = 4 * 1024 * 1024;

    private readonly string _hostVersion;
    private readonly PluginManagerLog _log;

    public PluginPackageValidator(string hostVersion, PluginManagerLog log)
    {
        _hostVersion = hostVersion;
        _log = log;
    }

    public ValidatedPackage ValidateAndExtract(string packagePath, string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new PluginValidationException("插件包路径为空。");
        }

        string fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath))
        {
            throw new PluginValidationException($"插件包不存在：{fullPackagePath}");
        }

        string stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = ZipFile.OpenRead(fullPackagePath);
            if (archive.Entries.Count == 0)
            {
                throw new PluginValidationException("插件包为空，缺少 manifest.json。");
            }

            ZipArchiveEntry manifestEntry = FindRootManifestEntry(archive);
            PluginManifest manifest = ReadManifest(manifestEntry);
            if (!PluginManifestValidator.TryValidate(
                    manifest,
                    _hostVersion,
                    out string validationError,
                    out string normalizedEntryAssembly))
            {
                throw new PluginValidationException(validationError);
            }

            List<(ZipArchiveEntry Entry, string RelativePath)> fileEntries =
                ValidateAndCollectFileEntries(archive, normalizedEntryAssembly, stagingDirectory);

            Directory.CreateDirectory(stagingDirectory);
            foreach ((ZipArchiveEntry entry, string relativePath) in fileEntries)
            {
                ExtractEntry(entry, stagingDirectory, relativePath);
            }

            string entryAssemblyPath = Path.GetFullPath(Path.Combine(
                stagingDirectory,
                normalizedEntryAssembly.Replace('/', Path.DirectorySeparatorChar)));

            string libsDirectory = Path.Combine(stagingDirectory, "libs");
            if (!PathRules.IsUnderDirectory(entryAssemblyPath, libsDirectory))
            {
                throw new PluginValidationException("manifest.entryAssembly 规范化后不在 libs/ 目录内。");
            }

            if (!File.Exists(entryAssemblyPath))
            {
                throw new PluginValidationException($"安装包中缺少入口程序集：{normalizedEntryAssembly}");
            }

            _log.Info($"插件包校验通过：{fullPackagePath} → {stagingDirectory}");
            return new ValidatedPackage(manifest, stagingDirectory, entryAssemblyPath);
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
                throw new PluginValidationException("插件包根目录存在多个 manifest.json。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new PluginValidationException("manifest.json 不能是目录。");
            }

            found = entry;
        }

        if (found is null)
        {
            throw new PluginValidationException("插件包根目录缺少 manifest.json。");
        }

        return found;
    }

    private static PluginManifest ReadManifest(ZipArchiveEntry manifestEntry)
    {
        if (manifestEntry.Length > MaxManifestBytes)
        {
            throw new PluginValidationException("manifest.json 过大。");
        }

        using Stream stream = manifestEntry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        PluginManifest? manifest = PluginManifestParser.Parse(memory.ToArray(), out string? parseError);
        if (manifest is null)
        {
            throw new PluginValidationException(parseError ?? "manifest.json 解析失败。");
        }

        return manifest;
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
                throw new PluginValidationException($"安装包含非法路径“{rawName}”：{pathError}");
            }

            if (string.Equals(relativePath, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                if (isDirectory)
                {
                    throw new PluginValidationException("manifest.json 不能是目录。");
                }

                // manifest 也要解压到插件目录根，供后续加载/展示时读取。
                string manifestDestinationPath = Path.GetFullPath(Path.Combine(
                    stagingDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathRules.IsUnderDirectory(manifestDestinationPath, stagingDirectory))
                {
                    throw new PluginValidationException($"安装包含非法路径（Zip Slip）：{rawName}");
                }

                collected.Add((entry, relativePath));
                continue;
            }

            if (isDirectory)
            {
                if (!string.Equals(relativePath, "libs", StringComparison.OrdinalIgnoreCase)
                    && !relativePath.StartsWith("libs/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PluginValidationException(
                        $"安装包根目录只允许 manifest.json，其余内容必须位于 libs/ 目录内（发现目录：{relativePath}）。");
                }

                continue;
            }

            if (!relativePath.StartsWith("libs/", StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginValidationException(
                    $"插件文件必须位于 libs/ 目录内（发现：{relativePath}）。");
            }

            string destinationPath = Path.GetFullPath(Path.Combine(
                stagingDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!PathRules.IsUnderDirectory(destinationPath, stagingDirectory))
            {
                throw new PluginValidationException($"安装包含非法路径（Zip Slip）：{rawName}");
            }

            if (!seenPaths.Add(relativePath))
            {
                throw new PluginValidationException($"安装包中存在重复文件：{relativePath}");
            }

            if (string.Equals(relativePath, entryAssemblyRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                entryAssemblyFound = true;
            }

            collected.Add((entry, relativePath));
        }

        if (!entryAssemblyFound)
        {
            throw new PluginValidationException(
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
            _log.Warn($"清理临时目录失败（{directory}）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
