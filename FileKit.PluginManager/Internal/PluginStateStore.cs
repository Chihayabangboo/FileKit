using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKit.PluginManager.Internal;

/// <summary>
/// plugins.json 中一条记录的持久化模型，字段名与规范一致。
/// </summary>
internal sealed class PluginStateRecord
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("directoryPath")]
    public string? DirectoryPath { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>
/// %LOCALAPPDATA%\FileKit\plugins.json 的读写。损坏时重置为空列表且不抛异常。
/// </summary>
internal sealed class PluginStateStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _stateFilePath;
    private readonly string _pluginsRoot;
    private readonly PluginManagerLog _log;

    public PluginStateStore(string stateFilePath, string pluginsRoot, PluginManagerLog log)
    {
        _stateFilePath = stateFilePath;
        _pluginsRoot = pluginsRoot;
        _log = log;
    }

    public List<PluginStateRecord> Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_stateFilePath))
                {
                    return new List<PluginStateRecord>();
                }

                string text = File.ReadAllText(_stateFilePath);
                if (string.IsNullOrWhiteSpace(text))
                {
                    ResetToEmpty("plugins.json 内容为空，已重置。");
                    return new List<PluginStateRecord>();
                }

                List<PluginStateRecord>? records;
                try
                {
                    records = JsonSerializer.Deserialize<List<PluginStateRecord>>(text, ReadOptions);
                }
                catch (JsonException ex)
                {
                    ResetToEmpty($"plugins.json 损坏，已重置为空列表：{ex.Message}");
                    return new List<PluginStateRecord>();
                }

                if (records is null)
                {
                    ResetToEmpty("plugins.json 内容为 null，已重置为空列表。");
                    return new List<PluginStateRecord>();
                }

                var valid = new List<PluginStateRecord>();
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (PluginStateRecord record in records)
                {
                    if (record is null)
                    {
                        _log.Warn("跳过 plugins.json 中的 null 记录。");
                        continue;
                    }

                    if (!TryNormalize(record, out string reason))
                    {
                        _log.Warn($"跳过无效插件记录：{reason}");
                        continue;
                    }

                    if (!seenIds.Add(record.Id!))
                    {
                        _log.Warn($"跳过重复插件记录：{record.Id}");
                        continue;
                    }

                    valid.Add(record);
                }

                return valid;
            }
            catch (Exception ex)
            {
                _log.Error($"读取 plugins.json 失败：{ex.GetType().Name}: {ex.Message}");
                return new List<PluginStateRecord>();
            }
        }
    }

    public void Save(IReadOnlyList<PluginStateRecord> records)
    {
        lock (_sync)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var list = new List<PluginStateRecord>(records.Count);
                foreach (PluginStateRecord record in records)
                {
                    list.Add(new PluginStateRecord
                    {
                        Id = record.Id,
                        Version = record.Version,
                        DirectoryPath = record.DirectoryPath,
                        Enabled = record.Enabled,
                    });
                }

                string json = JsonSerializer.Serialize(list, WriteOptions);
                string tempPath = _stateFilePath + ".tmp";
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                File.Move(tempPath, _stateFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.Error($"保存 plugins.json 失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ResetToEmpty(string reason)
    {
        _log.Warn(reason);
        Save(Array.Empty<PluginStateRecord>());
    }

    private bool TryNormalize(PluginStateRecord record, out string reason)
    {
        reason = string.Empty;

        if (!PathRules.IsSafePluginId(record.Id))
        {
            reason = $"id 非法（{record.Id}）。";
            return false;
        }

        if (!VersionRules.IsThreePart(record.Version))
        {
            reason = $"version 非法（{record.Version}）。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(record.DirectoryPath) || !Path.IsPathRooted(record.DirectoryPath))
        {
            reason = $"directoryPath 不是绝对路径（{record.DirectoryPath}）。";
            return false;
        }

        string fullDirectory = Path.GetFullPath(record.DirectoryPath);
        if (!PathRules.IsUnderDirectory(fullDirectory, _pluginsRoot))
        {
            reason = $"directoryPath 不在 plugins 目录内（{record.DirectoryPath}）。";
            return false;
        }

        record.DirectoryPath = fullDirectory;
        return true;
    }
}
