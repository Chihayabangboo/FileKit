using FileKit.Contracts.Effects;

namespace FileKit.EffectManager;

/// <summary>
/// 校验通过的 manifest 只读快照，供 Host 与 UI 展示。
/// </summary>
public sealed class EffectManifestInfo
{
    public required string EffectId { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string MinHostVersion { get; init; }

    /// <summary>规范化后的相对路径，始终以 libs/ 开头且以 / 分隔。</summary>
    public required string EntryAssembly { get; init; }

    public required string EntryType { get; init; }

    public required EffectTarget Target { get; init; }
}
