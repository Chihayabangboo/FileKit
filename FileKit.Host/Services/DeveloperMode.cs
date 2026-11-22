using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKit.Host.Services;

/// <summary>
/// 开发者模式判定与本地配置：
/// 1. 命令行参数 <c>--developer-mode</c> 优先级最高（强制开发者模式，覆盖本地勾选状态）；
/// 2. 否则读取可写数据目录下的 <c>developer.json</c>，内容 <c>{ "enabled": true }</c>（界面勾选状态）；
/// 3. 文件不存在 / JSON 非法 / IO 异常 / enabled 缺失或非 bool 一律视为 false；任何情况不抛异常。
/// </summary>
public static class DeveloperMode
{
    public const string CommandLineSwitch = "--developer-mode";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 本地开发者配置路径：数据根目录（默认 %LOCALAPPDATA%\FileKit，受限环境为可写回退目录）下的 developer.json。
    /// </summary>
    public static string ResolveDeveloperConfigPath()
    {
        try
        {
            return Path.Combine(HostPaths.FileKitRoot, "developer.json");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Could not resolve the developer config path: {ex.GetType().Name}: {ex.Message}");
            return "developer.json";
        }
    }

    /// <summary>命令行是否显式包含 <c>--developer-mode</c>（强制开发者模式）。</summary>
    public static bool IsCommandLineSwitchPresent(IEnumerable<string>? commandLineArgs)
    {
        if (commandLineArgs is null)
        {
            return false;
        }

        foreach (string argument in commandLineArgs)
        {
            if (string.Equals(argument, CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>读取本地勾选配置（不看命令行）。任何异常/格式错误一律 false。</summary>
    public static bool ReadLocalConfig() => ReadConfigEnabled(ResolveDeveloperConfigPath());

    /// <summary>保存本地勾选配置（界面复选框状态）。任何异常只记录，不抛出。</summary>
    public static void SaveLocalConfig(bool enabled)
    {
        string path = ResolveDeveloperConfigPath();
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var config = new DeveloperConfig { Enabled = enabled };
            string json = JsonSerializer.Serialize(config, WriteOptions);
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
            Debug.WriteLine($"[FileKit] Developer mode config saved: enabled={enabled} → {path}");
        }
        catch (Exception ex)
        {
            // 本地配置写入失败不影响功能；切换在当前会话内仍然生效。
            Debug.WriteLine($"[FileKit] Could not save the developer config: {ex.GetType().Name}: {ex.Message}");
            try
            {
                if (File.Exists(path + ".tmp"))
                {
                    File.Delete(path + ".tmp");
                }
            }
            catch
            {
                // 忽略清理失败。
            }
        }
    }

    /// <summary>判定是否启用开发者模式（命令行参数优先于配置文件）。</summary>
    public static bool IsEnabled(IEnumerable<string>? commandLineArgs, string? configPath = null)
    {
        try
        {
            if (IsCommandLineSwitchPresent(commandLineArgs))
            {
                return true;
            }

            string path = string.IsNullOrWhiteSpace(configPath) ? ResolveDeveloperConfigPath() : configPath!;
            return ReadConfigEnabled(path);
        }
        catch (Exception ex)
        {
            // 开发者模式判定绝不允许影响主程序启动。
            Debug.WriteLine($"[FileKit] Developer mode detection failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool ReadConfigEnabled(string configPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                return false;
            }

            string text = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            DeveloperConfig? config = JsonSerializer.Deserialize<DeveloperConfig>(text, ReadOptions);
            return config?.Enabled == true;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[FileKit] developer.json is not valid JSON, treated as disabled: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            Debug.WriteLine($"[FileKit] developer.json could not be read, treated as disabled: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>developer.json 的数据模型，仅使用 enabled 字段。</summary>
    private sealed class DeveloperConfig
    {
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }
    }
}
