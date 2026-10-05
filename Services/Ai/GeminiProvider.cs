using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace DashTudo.Web.Services.Ai;

/// <summary>Google Gemini via REST (streamGenerateContent + SSE). Aceita PDF nativo (inline_data).</summary>
public class GeminiProvider(HttpClient http, IOptions<AiOptions> options, ILogger<GeminiProvider> logger) : IAiProvider
{
    private readonly GeminiOptions _o = options.Value.Gemini;

    public string Name => "Gemini";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_o.ApiKey);
    // Requisições inline do Gemini são limitadas a ~20 MB (o base64 aumenta ~33%).
    public int MaxPdfBytes => 14 * 1024 * 1024;

    public async Task<AiCompletion> StreamAsync(AiPrompt prompt, Func<string, Task> onDelta, CancellationToken ct)
    {
        var parts = new JsonArray();
        if (prompt.Pdf is not null)
        {
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject { ["mime_type"] = "application/pdf", ["data"] = Convert.ToBase64String(prompt.Pdf) },
            });
        }
        parts.Add(new JsonObject { ["text"] = prompt.UserText });

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt.System }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = prompt.MaxOutputTokens },
        };

        var url = $"{_o.BaseUrl.TrimEnd('/')}/models/{Uri.EscapeDataString(_o.Model)}:streamGenerateContent?alt=sse";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", _o.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await Sse.EnsureSuccessAsync(response, Name, logger, ct);

        long input = 0, output = 0;
        var model = _o.Model;
        string? finishReason = null;
        await foreach (var data in Sse.ReadDataLinesAsync(response, ct))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("modelVersion", out var mv)) model = mv.GetString() ?? model;
            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                if (usage.TryGetProperty("promptTokenCount", out var p)) input = p.GetInt64();
                if (usage.TryGetProperty("candidatesTokenCount", out var c)) output = c.GetInt64();
            }
            if (root.TryGetProperty("promptFeedback", out var fb) && fb.TryGetProperty("blockReason", out var br))
            {
                throw new AiException($"A IA bloqueou este pedido ({br.GetString()}). Tente reformular.");
            }
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0) continue;

            var candidate = candidates[0];
            if (candidate.TryGetProperty("finishReason", out var fr)) finishReason = fr.GetString();
            if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var ps))
            {
                foreach (var part in ps.EnumerateArray())
                {
                    // Partes de "pensamento" (thought=true) não são exibidas.
                    if (part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) continue;
                    if (part.TryGetProperty("text", out var t) && t.GetString() is { Length: > 0 } text) await onDelta(text);
                }
            }
        }

        if (finishReason is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "RECITATION")
        {
            throw new AiException($"A IA interrompeu a resposta ({finishReason}). Tente reformular o pedido.");
        }
        return new AiCompletion(model, input, output, finishReason == "MAX_TOKENS");
    }
}
