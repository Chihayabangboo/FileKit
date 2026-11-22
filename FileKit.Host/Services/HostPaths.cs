using System.Diagnostics;
using System.IO;

namespace FileKit.Host.Services;

/// <summary>
/// FileKit 数据根目录解析：优先 <c>%LOCALAPPDATA%\FileKit</c>；
/// 该路径不可写（受限/只读环境）时依次回退到 exe 目录的 <c>FileKitData</c>、<c>%TEMP%\FileKitData</c>。
/// 仅调整数据存放位置，不改变 M2 插件契约、PluginManager 逻辑与 M1 材质逻辑。
/// </summary>
internal static class HostPaths
{
    private static readonly object Sync = new();
    private static string? _fileKitRoot;

    /// <summary>当前可写的数据根目录（含 plugins / effects / logs / data 等子目录）。</summary>
    public static string FileKitRoot
    {
        get
        {
            lock (Sync)
            {
                return _fileKitRoot ??= Resolve();
            }
        }
    }

    /// <summary>
    /// 确保进程临时目录可写。受限环境（%TEMP% 不可写）下 WPF 在加载 GridView 光标等资源时
    /// 会抛 UnauthorizedAccessException（表现为打开插件管理弹窗时闪退），这里把 TEMP/TMP
    /// 进程级重定向到数据根下的 Temp 目录；默认 TEMP 可写时不做任何改动。
    /// </summary>
    public static void EnsureWritableTemp()
    {
        try
        {
            string current = Path.GetTempPath();
            if (IsDirectoryWritable(current))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Could not probe default temp path: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            string tempRoot = Path.Combine(FileKitRoot, "Temp");
            Directory.CreateDirectory(tempRoot);
            Environment.SetEnvironmentVariable("TEMP", tempRoot, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("TMP", tempRoot, EnvironmentVariableTarget.Process);
            Debug.WriteLine($"[FileKit] Process TEMP redirected to '{tempRoot}'.");
        }
        catch (Exception ex)
        {
            // 重定向失败不阻塞启动；WPF 若在此环境仍无法加载光标，弹窗会被上层 catch 记录而不是崩溃。
            Debug.WriteLine($"[FileKit] Could not redirect temp path: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probePath = Path.Combine(directory, ".filekit-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Resolve()
    {
        var candidates = new List<(string Path, string Description)>();

        try
        {
            candidates.Add((
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FileKit"),
                "%LOCALAPPDATA%"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Cannot resolve LocalApplicationData: {ex.GetType().Name}: {ex.Message}");
        }

        candidates.Add((Path.Combine(AppContext.BaseDirectory, "FileKitData"), "exe 目录"));

        try
        {
            candidates.Add((Path.Combine(Path.GetTempPath(), "FileKitData"), "%TEMP%"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Cannot resolve temp path: {ex.GetType().Name}: {ex.Message}");
        }

        foreach ((string path, string description) in candidates)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(path);
                Debug.WriteLine($"[FileKit] Data root '{path}' ({description}) is writable.");
                return path;
            }
            catch (Exception ex)
            {
                // 受限环境（Low IL / 只读目录 / 权限策略）继续尝试下一个候选。
                Debug.WriteLine(
                    $"[FileKit] Data root '{path}' ({description}) is not writable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 理论上不会走到这里（%TEMP% 通常可写）；让上层抛出可诊断的异常。
        return Path.Combine(AppContext.BaseDirectory, "FileKitData");
    }
}
