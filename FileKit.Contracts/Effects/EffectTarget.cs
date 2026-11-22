namespace FileKit.Contracts.Effects;

/// <summary>
/// 特效作用的界面区域。
/// </summary>
public enum EffectTarget
{
    /// <summary>整个宿主窗口。</summary>
    Window,

    /// <summary>顶部标签栏区域。</summary>
    TabBar,

    /// <summary>内容表面区域。</summary>
    Surface,

    /// <summary>Overlay 层。</summary>
    Overlay,
}
