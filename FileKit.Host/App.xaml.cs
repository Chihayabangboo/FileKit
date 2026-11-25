using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FileKit.Host.Services;

namespace FileKit.Host;

/// <summary>
/// Application 入口。除创建主窗口外，还负责把启动期/未处理异常落盘，
/// 避免 WinExe 在无控制台环境下“双击无反应”而无法定位原因。
/// </summary>
public partial class App : Application
{
    /// <summary>启动失败日志（优先写 exe 目录，其次 %TEMP%，最后 %LOCALAPPDATA%）。</summary>
    public static string StartupErrorLogPath { get; private set; } = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 必须在创建任何 WPF 窗口/列表控件前完成：受限环境下 WPF 写 %TEMP%\WPF 光标缓存会失败。
        HostPaths.EnsureWritableTemp();

        // 全局文本渲染：Grayscale 消除亚像素彩边，Display 提升小字号清晰度。
        // 注意：Application 不是 DependencyObject，TextOptions 无法直接设在 this 上；
        // 因此在这里对创建出的主窗口设置，并由 FontEnforcer 对所有窗口/插件控件持续强制。
        FontEnforcer.Install();

        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += HandleDomainUnhandledException;
        DispatcherUnhandledException += HandleDispatcherUnhandledException;

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            TextOptions.SetTextRenderingMode(window, TextRenderingMode.Grayscale);
            TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            window.Show();
        }
        catch (Exception ex)
        {
            // 启动期异常：落盘完整堆栈，然后优雅退出（不再像裸奔一样静默崩溃）。
            WriteStartupFailure("启动失败", ex);
            Shutdown(1);
        }
    }

    private void HandleDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteStartupFailure("未处理异常（AppDomain）", exception);
        }
    }

    private void HandleDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteStartupFailure("未处理异常（Dispatcher）", e.Exception);
    }

    private static void WriteStartupFailure(string title, Exception exception)
    {
        string message;
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {title}");
            builder.AppendLine($"OS: {Environment.OSVersion}");
            builder.AppendLine($"Runtime: {Environment.Version}");
            builder.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
            builder.AppendLine($"CurrentDirectory: {Directory.GetCurrentDirectory()}");
            builder.AppendLine($"Args: {string.Join(' ', Environment.GetCommandLineArgs())}");
            builder.AppendLine($"Exception: {exception}");
            message = builder.ToString();
        }
        catch (Exception formattingException)
        {
            message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {title}: {exception.Message}{Environment.NewLine}"
                      + $"（整理异常信息失败：{formattingException.Message}）";
        }

        Debug.WriteLine(message);

        foreach (string candidate in EnumerateLogPaths())
        {
            try
            {
                string? directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(candidate, message + Environment.NewLine, new UTF8Encoding(false));
                StartupErrorLogPath = candidate;
                return;
            }
            catch
            {
                // 换下一个候选路径。
            }
        }
    }

    private static IEnumerable<string> EnumerateLogPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "startup-error.log");

        string temp = Path.GetTempPath();
        if (!string.IsNullOrEmpty(temp))
        {
            yield return Path.Combine(temp, "FileKit.startup-error.log");
        }

        string? localAppData = TryGetLocalAppDataPath();
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "FileKit", "logs", "startup-error.log");
        }
    }

    private static string? TryGetLocalAppDataPath()
    {
        try
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        catch
        {
            return null;
        }
    }
}
