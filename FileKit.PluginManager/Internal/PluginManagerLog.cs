using System.Diagnostics;
using System.Text;

namespace FileKit.PluginManager.Internal;

/// <summary>
/// 极简文件日志。加载失败等“只记录不弹窗”的场景写入这里，供 Host/人工排查。
/// </summary>
internal sealed class PluginManagerLog
{
    private const long MaxLogBytes = 1_000_000;

    private readonly object _sync = new();
    private readonly string _logFilePath;

    public PluginManagerLog(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            _logFilePath = Path.Combine(logDirectory, "plugin-manager.log");
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
        Debug.WriteLine($"[FileKit.PluginManager] {line}");

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
                // 日志失败不能影响插件生命周期。
            }
        }
    }
}
