using System.Text.RegularExpressions;

namespace FileKit.PluginManager.Internal;

/// <summary>
/// 路径安全规则：Zip Slip 防御、相对路径规范化、插件 ID 白名单与目录包含判断。
/// </summary>
internal static class PathRules
{
    private static readonly Regex PluginIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 把 ZIP entry / manifest 中的相对路径规范化为以 / 分隔的形式。
    /// 拒绝绝对路径、盘符、空段、"."、".."，以及 Windows 非法文件名字符。
    /// </summary>
    public static bool TryNormalizeRelativePath(
        string? rawPath,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "路径为空。";
            return false;
        }

        string candidate = rawPath.Trim().Replace('\\', '/');
        if (candidate.StartsWith('/'))
        {
            error = "路径不能是绝对路径。";
            return false;
        }

        if (candidate.Contains(':'))
        {
            error = "路径不能包含盘符或冒号。";
            return false;
        }

        if (Path.IsPathRooted(candidate))
        {
            error = "路径不能是绝对路径。";
            return false;
        }

        string[] segments = candidate.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                error = "路径包含空段。";
                return false;
            }

            if (segment == "." || segment == "..")
            {
                error = "路径不能包含 . 或 .. 段（疑似 Zip Slip）。";
                return false;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                error = "路径段不能以空格或点结尾。";
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "路径包含非法字符。";
                return false;
            }
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>
    /// path 是否严格位于 directory 内部（或与 directory 相等）。
    /// 用于 libs/ 白名单与 plugins/ 根目录保护。
    /// </summary>
    public static bool IsUnderDirectory(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(
            fullDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 插件 ID 必须可以安全地作为 plugins/ 下的目录名。
    /// </summary>
    public static bool IsSafePluginId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return false;
        }

        if (!PluginIdPattern.IsMatch(id))
        {
            return false;
        }

        if (id.EndsWith('.') || id.EndsWith(' '))
        {
            return false;
        }

        string firstSegment = id.Split('.')[0];
        return !ReservedDeviceNames.Contains(firstSegment);
    }

    /// <summary>
    /// 规范化用户/Host 传入的路径；相对路径按当前工作目录补齐。
    /// </summary>
    public static string NormalizeAbsolutePath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{parameterName} 不能为空。", parameterName);
        }

        return Path.GetFullPath(path);
    }
}
