using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;

namespace DashTudo.Web.Services.Ai;

/// <summary>Claude (Anthropic) via SDK oficial. Pago; aceita PDF nativo.</summary>
public class AnthropicProvider(IOptions<AiOptions> options, ILogger<AnthropicProvider> logger) : IAiProvider
{
    private readonly AnthropicOptions _o = options.Value.Anthropic;
    private AnthropicClient? _client;

    public string Name => "Anthropic";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_o.ApiKey) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
    public int MaxPdfBytes => 30 * 1024 * 1024;

    private AnthropicClient Client => _client ??= string.IsNullOrWhiteSpace(_o.ApiKey)
        ? new AnthropicClient()
        : new AnthropicClient { ApiKey = _o.ApiKey };

    public async Task<AiCompletion> StreamAsync(AiPrompt prompt, Func<string, Task> onDelta, CancellationToken ct)
    {
        List<BetaContentBlockParam> content = [];
        if (prompt.Pdf is not null)
        {
            content.Add(new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = Convert.ToBase64String(prompt.Pdf) } });
        }
        content.Add(new BetaTextBlockParam { Text = prompt.UserText });

        var parameters = new MessageCreateParams
        {
            Model = _o.Model,
            MaxTokens = prompt.MaxOutputTokens,
            System = prompt.System,
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(_o.Effort) },
            // Se o classificador de segurança recusar um pedido legítimo, a API refaz a chamada no modelo de fallback recomendado.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages = [new() { Role = Role.User, Content = content }],
        };

        long input = 0, output = 0;
        var model = _o.Model;
        string? stopReason = null;
        try
        {
            await foreach (var ev in Client.Beta.Messages.CreateStreaming(parameters, ct))
            {
                if (ev.TryPickStart(out var start))
                {
                    model = start.Message.Model;
                    input = start.Message.Usage.InputTokens;
                }
                else if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    await onDelta(text.Text);
                }
                else if (ev.TryPickDelta(out var messageDelta))
                {
                    output = messageDelta.Usage.OutputTokens;
                    stopReason = messageDelta.Delta.StopReason?.ToString();
                }
            }
        }
        catch (AnthropicRateLimitException)
        {
            throw new AiException("Limite de uso da IA atingido no momento. Tente novamente em alguns minutos.");
        }
        catch (AnthropicApiException ex)
        {
            logger.LogError(ex, "Erro na API da Anthropic");
            throw new AiException($"A IA retornou um erro: {ex.Message}");
        }

        if (stopReason?.Contains("refusal", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new AiException("A IA recusou este pedido por questões de política de uso. Tente reformular.");
        }
        return new AiCompletion(model, input, output, stopReason?.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}
