using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace DashTudo.Web.Services.Ai;

/// <summary>
/// Qualquer API no formato /chat/completions (Groq, OpenRouter, Together, DeepSeek, Ollama local...).
/// PDFs são enviados como texto extraído.
/// </summary>
public class OpenAICompatibleProvider(HttpClient http, IOptions<AiOptions> options, ILogger<OpenAICompatibleProvider> logger) : IAiProvider
{
    private readonly OpenAICompatibleOptions _o = options.Value.OpenAICompatible;

    public string Name => "OpenAICompatible";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_o.BaseUrl) && !string.IsNullOrWhiteSpace(_o.Model);
    public int MaxPdfBytes => 0;

    public async Task<AiCompletion> StreamAsync(AiPrompt prompt, Func<string, Task> onDelta, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _o.Model,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = prompt.MaxOutputTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = prompt.System },
                new JsonObject { ["role"] = "user", ["content"] = prompt.UserText }),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_o.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(_o.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await Sse.EnsureSuccessAsync(response, Name, logger, ct);

        long input = 0, output = 0;
        var model = _o.Model;
        string? finishReason = null;
        await foreach (var data in Sse.ReadDataLinesAsync(response, ct))
        {
            if (data == "[DONE]") break;
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model = m.GetString()!;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var p)) input = p.GetInt64();
                if (usage.TryGetProperty("completion_tokens", out var c)) output = c.GetInt64();
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finishReason = fr.GetString();
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
            {
                await onDelta(text);
            }
        }

        if (finishReason == "content_filter") throw new AiException("A IA recusou este pedido. Tente reformular.");
        return new AiCompletion(model, input, output, finishReason == "length");
    }
}
