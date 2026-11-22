using System.Text.RegularExpressions;
using FileKit.Contracts.Effects;
using FileKit.PluginManager.Internal;

namespace FileKit.EffectManager.Internal;

/// <summary>
/// manifest 校验：必填字段、版本兼容、entryAssembly 布局、entryType 形态、target 大小写敏感映射。
/// 版本与路径规则复用 M2 的 VersionRules / PathRules（InternalsVisibleTo）。
/// </summary>
internal static class EffectManifestValidator
{
    private static readonly Regex EntryTypePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.+`]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryValidate(
        EffectManifest manifest,
        string hostVersion,
        out string error,
        out EffectManifestInfo? info)
    {
        error = string.Empty;
        info = null;

        if (string.IsNullOrWhiteSpace(manifest.EffectId))
        {
            error = "manifest 缺少必填字段：effectId。";
            return false;
        }

        if (!PathRules.IsSafePluginId(manifest.EffectId))
        {
            error = $"manifest.effectId 非法：{manifest.EffectId}（仅允许字母、数字、点、下划线、短横线，且不能以点结尾）。";
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
            error = $"特效要求 Host 版本 >= {manifest.MinHostVersion}，当前 Host 版本 {hostVersion}，无法安装。";
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

        if (!TryParseTarget(manifest.Target, out EffectTarget target))
        {
            error = $"manifest.target 非法：{manifest.Target}（必须大小写敏感精确匹配 Window / TabBar / Surface / Overlay）。";
            return false;
        }

        info = new EffectManifestInfo
        {
            EffectId = manifest.EffectId,
            Name = manifest.Name,
            Version = manifest.Version!,
            MinHostVersion = manifest.MinHostVersion,
            EntryAssembly = entryAssembly,
            EntryType = manifest.EntryType,
            Target = target,
        };

        return true;
    }

    /// <summary>target 必须大小写敏感精确匹配枚举名称（拒绝 "0" 等数字字符串与大小写变体）。</summary>
    public static bool TryParseTarget(string? value, out EffectTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Array.IndexOf(Enum.GetNames<EffectTarget>(), value) < 0)
        {
            return false;
        }

        return Enum.TryParse(value, ignoreCase: false, out target)
            && Enum.IsDefined(typeof(EffectTarget), target);
    }
}
