namespace FileKit.EffectManager;

/// <summary>
/// 额外特效卸载结果。PendingDelete=true 表示目录被占用已改名，下次启动清理。
/// </summary>
public sealed class EffectUninstallResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public bool PendingDelete { get; init; }
}
