namespace FileKit.EffectManager;

/// <summary>
/// 已通过校验并解压到 staging 的 .fkeffect 包。
/// Commit 之前 Dispose 会清理 staging；Commit 成功后目录已被移走，Dispose 安全。
/// </summary>
public sealed class EffectPackage : IDisposable
{
    private int _disposed;

    internal EffectPackage(EffectManifestInfo manifest, string stagingDirectory)
    {
        Manifest = manifest;
        StagingDirectory = stagingDirectory;
    }

    public EffectManifestInfo Manifest { get; }

    internal string StagingDirectory { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
        }
        catch
        {
            // staging 清理失败不影响安装流程；LoadDevEffects 启动时会统一清理。
        }
    }
}
