using FileKit.Contracts.Effects;

namespace FileKit.EffectManager;

/// <summary>
/// 单个特效目录的扫描/加载结果。加载失败时 Instance/Runtime 为 null，Error 非空；
/// 失败只记录并跳过，绝不向外抛异常。
/// </summary>
public sealed class EffectLoadResult
{
    public required string EffectId { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required EffectOrigin Origin { get; init; }

    public string? DirectoryPath { get; init; }

    /// <summary>校验通过的 manifest 快照；加载失败且 manifest 无效时为 null。</summary>
    public EffectManifestInfo? Manifest { get; init; }

    public IFileKitEffect? Instance { get; init; }

    /// <summary>ALC 生命周期句柄；Host 在 Detach + 清理后调用 Dispose 卸载 ALC。</summary>
    public EffectRuntime? Runtime { get; init; }

    public string? Error { get; init; }

    public bool Success => Instance is not null && Error is null;
}
