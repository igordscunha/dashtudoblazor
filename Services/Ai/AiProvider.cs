namespace DashTudo.Web.Services.Ai;

/// <summary>Seção "Ai" do appsettings.json. Troque <see cref="Provider"/> para mudar de IA sem alterar código.</summary>
public sealed class AiOptions
{
    /// <summary>"Gemini" (padrão, tem camada gratuita), "OpenAICompatible" (Groq, OpenRouter, Ollama...) ou "Anthropic".</summary>
    public string Provider { get; set; } = "Gemini";
    public int MaxOutputTokens { get; set; } = 16000;
    /// <summary>Limite de caracteres de CSV enviados ao modelo; acima disso envia-se uma amostra (e o usuário é avisado).</summary>
    public int MaxDataChars { get; set; } = 300_000;
    /// <summary>Relatórios por usuário por dia (0 = sem limite). Protege sua cota/conta contra abuso.</summary>
    public int DailyLimitPerUser { get; set; } = 30;

    public GeminiOptions Gemini { get; set; } = new();
    public OpenAICompatibleOptions OpenAICompatible { get; set; } = new();
    public AnthropicOptions Anthropic { get; set; } = new();
}

public sealed class GeminiOptions
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gemini-3.8-flash";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
}

public sealed class OpenAICompatibleOptions
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "";
    /// <summary>Ex.: https://api.groq.com/openai/v1, https://openrouter.ai/api/v1, http://localhost:11434/v1 (Ollama).</summary>
    public string BaseUrl { get; set; } = "";
}

public sealed class AnthropicOptions
{
    /// <summary>Se vazio, usa a variável de ambiente ANTHROPIC_API_KEY.</summary>
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>low | medium | high | xhigh | max</summary>
    public string Effort { get; set; } = "medium";
}

/// <param name="Pdf">PDF original, enviado só a provedores com <see cref="IAiProvider.MaxPdfBytes"/> &gt; 0.</param>
public sealed record AiPrompt(string System, string UserText, byte[]? Pdf, int MaxOutputTokens);

public sealed record AiCompletion(string Model, long InputTokens, long OutputTokens, bool Truncated);

public class AiException(string message) : Exception(message);

public interface IAiProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    /// <summary>Tamanho máximo de PDF aceito no formato nativo (0 = não suporta; o texto extraído é usado).</summary>
    int MaxPdfBytes { get; }

    /// <summary>Gera a resposta em streaming, chamando <paramref name="onDelta"/> a cada trecho de texto.</summary>
    Task<AiCompletion> StreamAsync(AiPrompt prompt, Func<string, Task> onDelta, CancellationToken ct);
}

/// <summary>Utilitário para ler respostas Server-Sent Events (usado por Gemini e OpenAI-compatível).</summary>
internal static class Sse
{
    public static async IAsyncEnumerable<string> ReadDataLinesAsync(HttpResponseMessage response, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = line[5..].Trim();
                if (data.Length > 0) yield return data;
            }
        }
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string provider, ILogger logger, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        logger.LogError("{Provider} retornou {Status}: {Body}", provider, (int)response.StatusCode, body);
        throw (int)response.StatusCode switch
        {
            429 => new AiException("Limite de uso da IA atingido no momento (cota gratuita?). Tente novamente em alguns minutos."),
            401 or 403 => new AiException("A chave de API da IA é inválida ou não tem permissão. Verifique a configuração do servidor."),
            404 => new AiException("O modelo de IA configurado não foi encontrado. Verifique o nome do modelo no appsettings.json."),
            _ => new AiException($"A IA retornou um erro ({(int)response.StatusCode})."),
        };
    }
}
