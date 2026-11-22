using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKit.PluginManager.Internal;

/// <summary>
/// .fkpkg 根目录 manifest.json 的数据模型。
/// </summary>
internal sealed class PluginManifest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("minHostVersion")]
    public string? MinHostVersion { get; set; }

    [JsonPropertyName("entryAssembly")]
    public string? EntryAssembly { get; set; }

    [JsonPropertyName("entryType")]
    public string? EntryType { get; set; }
}

internal static class PluginManifestParser
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PluginManifest? Parse(byte[] json, out string? error)
    {
        error = null;
        try
        {
            string text = Encoding.UTF8.GetString(json).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "manifest.json 内容为空。";
                return null;
            }

            return JsonSerializer.Deserialize<PluginManifest>(text, ReadOptions);
        }
        catch (JsonException ex)
        {
            error = $"manifest.json 不是合法 JSON：{ex.Message}";
            return null;
        }
    }
}
