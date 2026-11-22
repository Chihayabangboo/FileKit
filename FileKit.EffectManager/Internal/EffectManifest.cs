using System.Text.Json.Serialization;

namespace FileKit.EffectManager.Internal;

/// <summary>
/// .fkeffect 根目录 manifest.json 的数据模型（target 以字符串承载，校验时精确映射）。
/// </summary>
internal sealed class EffectManifest
{
    [JsonPropertyName("effectId")]
    public string? EffectId { get; set; }

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

    [JsonPropertyName("target")]
    public string? Target { get; set; }
}
