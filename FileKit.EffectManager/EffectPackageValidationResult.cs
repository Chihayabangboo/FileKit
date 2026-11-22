namespace FileKit.EffectManager;

/// <summary>
/// 第一步安装校验结果：只校验 + 解压到 staging，对已安装特效零副作用。
/// </summary>
public sealed class EffectPackageValidationResult : IDisposable
{
    private int _disposed;

    private EffectPackageValidationResult(bool success, string? error, EffectPackage? package)
    {
        Success = success;
        Error = error;
        Package = package;
    }

    public bool Success { get; }

    public string? Error { get; }

    public EffectPackage? Package { get; }

    internal static EffectPackageValidationResult Ok(EffectPackage package) =>
        new(true, null, package);

    internal static EffectPackageValidationResult Fail(string error) =>
        new(false, error, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Package?.Dispose();
    }
}
