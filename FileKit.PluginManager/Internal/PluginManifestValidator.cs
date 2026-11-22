using System.Text.RegularExpressions;

namespace FileKit.PluginManager.Internal;

internal static class PluginManifestValidator
{
    private static readonly Regex EntryTypePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.+`]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 校验 manifest 必填字段与版本兼容性。minHostVersion 缺失/格式非法一律视为版本校验失败。
    /// </summary>
    public static bool TryValidate(
        PluginManifest manifest,
        string hostVersion,
        out string error,
        out string normalizedEntryAssembly)
    {
        error = string.Empty;
        normalizedEntryAssembly = string.Empty;

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            error = "manifest 缺少必填字段：id。";
            return false;
        }

        if (!PathRules.IsSafePluginId(manifest.Id))
        {
            error = $"manifest.id 非法：{manifest.Id}（仅允许字母、数字、点、下划线、短横线，且不能以点结尾）。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            error = "manifest 缺少必填字段：name。";
            return false;
        }

        if (!VersionRules.IsThreePart(manifest.Version))
        {
            error = "manifest.version 必须是三段式版本号（如 1.0.0）。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.MinHostVersion))
        {
            error = "manifest 缺少必填字段：minHostVersion，版本校验失败。";
            return false;
        }

        if (!VersionRules.IsThreePart(manifest.MinHostVersion))
        {
            error = $"manifest.minHostVersion 格式非法（{manifest.MinHostVersion}），版本校验失败，必须为三段式版本号（如 1.0.0）。";
            return false;
        }

        if (!VersionRules.IsHostCompatible(hostVersion, manifest.MinHostVersion))
        {
            error = $"插件要求 Host 版本 >= {manifest.MinHostVersion}，当前 Host 版本 {hostVersion}，无法安装。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            error = "manifest 缺少必填字段：entryAssembly。";
            return false;
        }

        if (!PathRules.TryNormalizeRelativePath(manifest.EntryAssembly, out string entryAssembly, out string pathError))
        {
            error = $"manifest.entryAssembly 非法：{pathError}";
            return false;
        }

        if (!entryAssembly.StartsWith("libs/", StringComparison.OrdinalIgnoreCase))
        {
            error = "manifest.entryAssembly 必须位于 libs/ 目录内。";
            return false;
        }

        if (!entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = "manifest.entryAssembly 必须是 .dll 文件。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType) || !EntryTypePattern.IsMatch(manifest.EntryType))
        {
            error = $"manifest.entryType 非法：{manifest.EntryType}。";
            return false;
        }

        normalizedEntryAssembly = entryAssembly;
        return true;
    }
}
