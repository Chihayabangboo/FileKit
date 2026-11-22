namespace FileKit.EffectManager;

/// <summary>
/// 特效来源：内置目录（不可卸载）或额外目录（开发者模式，可卸载）。
/// </summary>
public enum EffectOrigin
{
    Builtin,
    Dev,
}
