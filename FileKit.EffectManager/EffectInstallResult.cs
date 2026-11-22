namespace FileKit.EffectManager;

/// <summary>
/// 额外特效安装结果。PendingDelete=true 表示旧目录/备份被占用已改名，下次启动清理。
/// </summary>
public sealed class EffectInstallResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public EffectManifestInfo? Manifest { get; init; }

    public bool PendingDelete { get; init; }
}
