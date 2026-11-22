namespace FileKit.Contracts.Effects;

/// <summary>
/// 宿主支持的窗口背景材质类型。
/// </summary>
public enum BackdropType
{
    /// <summary>不使用原生背景材质，回退为宿主纯色背景。</summary>
    None,

    /// <summary>Windows 11 Mica 材质。</summary>
    Mica,

    /// <summary>Windows 11 Acrylic 材质。</summary>
    Acrylic,
}
