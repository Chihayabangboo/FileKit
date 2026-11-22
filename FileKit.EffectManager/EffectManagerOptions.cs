namespace FileKit.EffectManager;

/// <summary>
/// EffectManager 的构造参数。Host 手动 new，不使用第三方 DI 容器。
/// </summary>
public sealed class EffectManagerOptions
{
    /// <summary>Host 版本，必须是三段式（如 1.0.0）。</summary>
    public required string HostVersion { get; init; }

    /// <summary>内置特效根目录，如 &lt;app&gt;\effects\builtin。</summary>
    public required string BuiltinEffectsDirectory { get; init; }

    /// <summary>额外特效根目录，如 %LOCALAPPDATA%\FileKit\effects\dev（仅开发者模式使用）。</summary>
    public required string DevEffectsDirectory { get; init; }

    /// <summary>日志目录，如 %LOCALAPPDATA%\FileKit\logs。</summary>
    public required string LogDirectory { get; init; }
}
