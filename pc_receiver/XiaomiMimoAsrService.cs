using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace pc_receiver;

public sealed class XiaomiMimoAsrService
{
    private const string Endpoint = "https://api.xiaomimimo.com/v1/chat/completions";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public async Task<string> RecognizeAsync(string wavPath, string apiKey, string language, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("请先配置小米 MiMo API Key");
        }

        var audioBytes = await File.ReadAllBytesAsync(wavPath, cancellationToken);
        var audioBase64 = Convert.ToBase64String(audioBytes);
        if (Encoding.UTF8.GetByteCount(audioBase64) > 10 * 1024 * 1024)
        {
            throw new InvalidOperationException("音频过长，小米 MiMo API 要求 Base64 音频内容不超过 10MB");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.TryAddWithoutValidation("api-key", apiKey.Trim());
        request.Content = new StringContent(
            BuildRequestJson(audioBase64, NormalizeLanguage(language)),
            Encoding.UTF8,
            "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"小米 MiMo 识别失败: {(int)response.StatusCode} {response.ReasonPhrase} {TrimForStatus(body)}");
        }

        return ExtractText(body);
    }

    public static string NormalizeLanguage(string? language)
    {
        return language is "zh" or "en" ? language : "auto";
    }

    private static string BuildRequestJson(string audioBase64, string language)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", OnlineAsrCatalog.XiaomiMimoModelName);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "input_audio");
            writer.WriteStartObject("input_audio");
            writer.WriteString("data", $"data:audio/wav;base64,{audioBase64}");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("asr_options");
            writer.WriteString("language", language);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ExtractText(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (TryGetChoicesMessageContent(document.RootElement, out var text)
            || TryGetTopLevelText(document.RootElement, out text))
        {
            return text.Trim();
        }

        throw new InvalidOperationException("小米 MiMo 返回结果中没有识别文本");
    }

    private static bool TryGetChoicesMessageContent(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message)
                && TryExtractKnownText(message, out text))
            {
                return true;
            }

            if (choice.TryGetProperty("delta", out var delta)
                && TryExtractKnownText(delta, out text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetTopLevelText(JsonElement root, out string text)
    {
        text = string.Empty;
        foreach (var propertyName in new[] { "text", "content", "result", "output_text", "transcript", "asr_text" })
        {
            if (root.TryGetProperty(propertyName, out var element))
            {
                text = ExtractContent(element);
                return !string.IsNullOrWhiteSpace(text);
            }
        }

        return false;
    }

    private static bool TryExtractKnownText(JsonElement element, out string text)
    {
        text = string.Empty;
        foreach (var propertyName in new[] { "content", "text", "result", "output_text", "transcript", "asr_text" })
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            text = ExtractContent(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            return TryExtractKnownText(content, out var text) ? text : string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                builder.Append(item.GetString());
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object
                && TryExtractKnownText(item, out var nestedText))
            {
                builder.Append(nestedText);
            }
        }

        return builder.ToString();
    }

    private static string TrimForStatus(string body)
    {
        var normalized = body.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }
}
