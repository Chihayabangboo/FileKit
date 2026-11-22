using System.Text;
using System.Text.Json;

namespace FileKit.EffectManager.Internal;

internal static class EffectManifestParser
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static EffectManifest? Parse(byte[] json, out string? error)
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

            return JsonSerializer.Deserialize<EffectManifest>(text, ReadOptions);
        }
        catch (JsonException ex)
        {
            error = $"manifest.json 不是合法 JSON：{ex.Message}";
            return null;
        }
    }
}
