using System.Windows;

namespace FileKit.Contracts.Effects;

/// <summary>
/// 宿主提供给特效的运行时接口。特效只能通过本接口影响窗口外观，禁止直接调用系统 API 修改窗口背景。
/// </summary>
public interface IEffectHost
{
    /// <summary>当前 Host 主窗口句柄。</summary>
    nint WindowHandle { get; }

    /// <summary>
    /// 入栈覆盖材质。同一 effectId 重复调用视为更新并置顶；出栈后恢复下一层材质。
    /// </summary>
    void PushBackdrop(string effectId, BackdropType type);

    /// <summary>
    /// 出栈恢复材质。栈空时由宿主自动恢复 M1 默认材质/纯色降级。
    /// 未知 effectId 会被忽略并记录日志，不影响其他特效。
    /// </summary>
    void PopBackdrop(string effectId);

    /// <summary>
    /// 注册 Overlay。宿主保证在 UI 线程执行；同一特效重复注册同一元素会被忽略。
    /// zIndex 仅在 Overlay 层内部排序。
    /// </summary>
    void RegisterOverlay(FrameworkElement element, int zIndex);

    /// <summary>注销 Overlay。宿主保证在 UI 线程执行；未注册的元素会被忽略。</summary>
    void UnregisterOverlay(FrameworkElement element);
}
