using System.Text.RegularExpressions;

namespace FileKit.PluginManager.Internal;

/// <summary>
/// 三段式版本号（major.minor.patch）的解析与比较。
/// </summary>
internal static class VersionRules
{
    private static readonly Regex ThreePartPattern = new(
        @"^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsThreePart(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ThreePartPattern.IsMatch(value);

    public static Version? Parse(string? value) =>
        IsThreePart(value) ? Version.Parse(value!) : null;

    /// <summary>
    /// 把 AssemblyVersion（通常是 1.0.0.0 四段）规整成三段式字符串。
    /// </summary>
    public static string NormalizeAssemblyVersion(Version? version)
    {
        if (version is null)
        {
            return "0.0.0";
        }

        int build = version.Build < 0 ? 0 : version.Build;
        return $"{version.Major}.{version.Minor}.{build}";
    }

    public static bool IsHostCompatible(string hostVersion, string minHostVersion)
    {
        Version? host = Parse(hostVersion);
        Version? min = Parse(minHostVersion);
        return host is not null && min is not null && host >= min;
    }
}
