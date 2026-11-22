using System.Diagnostics;
using System.Text;

namespace FileKit.EffectManager;

/// <summary>
/// 极简文件日志（effects.log，&gt;1MB 轮转）。写日志失败绝不影响特效生命周期。
/// </summary>
public sealed class EffectLog
{
    private const long MaxLogBytes = 1_000_000;

    private readonly object _sync = new();
    private readonly string _logFilePath;

    public EffectLog(string logDirectory)
    {
        try
        {
            string directory = string.IsNullOrWhiteSpace(logDirectory)
                ? Path.Combine(Path.GetTempPath(), "FileKit", "logs")
                : Path.GetFullPath(logDirectory);

            Directory.CreateDirectory(directory);
            _logFilePath = Path.Combine(directory, "effects.log");
        }
        catch
        {
            _logFilePath = string.Empty;
        }
    }

    public string LogFilePath => _logFilePath;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        Debug.WriteLine($"[FileKit.EffectManager] {line}");

        if (string.IsNullOrEmpty(_logFilePath))
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Exists && fileInfo.Length > MaxLogBytes)
                {
                    File.Delete(_logFilePath);
                }

                File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志失败不能影响特效生命周期。
            }
        }
    }
}
