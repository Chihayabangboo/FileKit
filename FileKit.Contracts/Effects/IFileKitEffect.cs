namespace FileKit.Contracts.Effects;

/// <summary>
/// FileKit 特效入口契约。Host 负责加载、挂载与生命周期管理。
/// Attach/Detach 由 Host 在 UI 线程调用，且不得执行长耗时同步操作（耗时初始化请在特效内部 async 完成）。
/// </summary>
public interface IFileKitEffect : IDisposable
{
    /// <summary>特效 ID，必须与 manifest.effectId 一致。</summary>
    string EffectId { get; }

    /// <summary>特效作用区域，必须与 manifest.target 一致。</summary>
    EffectTarget Target { get; }

    /// <summary>挂载特效。只能通过 <see cref="IEffectHost"/> 影响宿主。</summary>
    void Attach(IEffectHost host);

    /// <summary>卸载特效，撤销 Attach 期间注册的 Overlay 与材质覆盖。</summary>
    void Detach();
}
