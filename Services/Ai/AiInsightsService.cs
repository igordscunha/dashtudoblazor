using System.Globalization;
using System.Text;
using DashTudo.Web.Data;
using DashTudo.Web.Services.Analysis;
using DashTudo.Web.Services.Parsing;
using Microsoft.Extensions.Options;

namespace DashTudo.Web.Services.Ai;

public sealed record AiReportRequest(Dataset Dataset, ParsedDataset Data, byte[]? OriginalFile, IReadOnlyList<ColumnProfile> Profiles, ReportKind Kind, string? Question);

public sealed record AiReportResult(string Markdown, string Model, long InputTokens, long OutputTokens, string? Warning);

/// <summary>Monta o prompt (independente de provedor) e delega a geração ao provedor escolhido em Ai:Provider.</summary>
public class AiInsightsService(IEnumerable<IAiProvider> providers, IOptions<AiOptions> options)
{
    private const int MaxDocumentChars = 1_000_000;

    private const string SystemPrompt = """
        Você é o analista de dados do DashTudo, uma plataforma onde pessoas enviam planilhas, textos e PDFs para
        entender seus dados. Escreva sempre em português do Brasil, em Markdown (títulos, listas e tabelas quando
        ajudarem). O leitor é alguém de negócio, não necessariamente técnico: seja claro e direto, use números
        concretos tirados dos dados para sustentar cada afirmação e diga explicitamente quando algo é hipótese ou
        quando os dados não permitem concluir. Não invente valores que não estejam nos dados. Se o perfil das
        colunas trouxer estatísticas calculadas sobre todas as linhas, prefira-as a cálculos feitos sobre amostras.
        """;

    private readonly AiOptions _options = options.Value;

    private IAiProvider Provider =>
        providers.FirstOrDefault(p => p.Name.Equals(_options.Provider, StringComparison.OrdinalIgnoreCase))
        ?? throw new AiException($"Provedor de IA '{_options.Provider}' desconhecido. Use Gemini, OpenAICompatible ou Anthropic.");

    public bool IsConfigured => providers.Any(p => p.Name.Equals(_options.Provider, StringComparison.OrdinalIgnoreCase) && p.IsConfigured);
    public string ProviderName => _options.Provider;
    public int DailyLimit => _options.DailyLimitPerUser;

    public static string KindLabel(ReportKind kind) => kind switch
    {
        ReportKind.Summary => "Resumo executivo",
        ReportKind.Insights => "Insights e anomalias",
        ReportKind.FullReport => "Relatório completo",
        _ => "Pergunta",
    };

    public async Task<AiReportResult> GenerateAsync(AiReportRequest request, Func<string, Task> onDelta, CancellationToken ct = default)
    {
        var provider = Provider;
        if (!provider.IsConfigured)
            throw new AiException($"A IA ({provider.Name}) não está configurada. Defina a chave de API no servidor (veja o README).");

        var (text, pdf, warning) = BuildUserContent(request, provider.MaxPdfBytes);
        var sb = new StringBuilder();
        AiCompletion completion;
        try
        {
            completion = await provider.StreamAsync(new AiPrompt(SystemPrompt, text, pdf, _options.MaxOutputTokens), async delta =>
            {
                sb.Append(delta);
                await onDelta(delta);
            }, ct);
        }
        catch (HttpRequestException)
        {
            throw new AiException("Não foi possível conectar à IA. Verifique a conexão do servidor.");
        }

        var markdown = sb.ToString();
        if (string.IsNullOrWhiteSpace(markdown)) throw new AiException("A IA não retornou conteúdo. Tente novamente.");
        if (completion.Truncated) markdown += "\n\n> ⚠️ O relatório atingiu o tamanho máximo e pode estar incompleto.";
        return new AiReportResult(markdown, completion.Model, completion.InputTokens, completion.OutputTokens, warning);
    }

    private (string Text, byte[]? Pdf, string? Warning) BuildUserContent(AiReportRequest r, int maxPdfBytes)
    {
        var task = TaskInstruction(r.Kind, r.Question);
        var sb = new StringBuilder();
        sb.AppendLine($"# Dataset: {r.Dataset.Name}");
        if (!string.IsNullOrWhiteSpace(r.Dataset.Description)) sb.AppendLine($"Descrição dada pelo usuário: {r.Dataset.Description}");
        sb.AppendLine($"Arquivo original: {r.Dataset.OriginalFileName}");

        if (r.Data.IsTabular)
        {
            var (csv, sampledRows) = BuildCsv(r.Data);
            sb.AppendLine($"Linhas: {r.Data.Rows.Count:N0} | Colunas: {r.Data.Columns.Count}");
            sb.AppendLine();
            sb.AppendLine("## Perfil das colunas (calculado sobre TODAS as linhas)");
            sb.AppendLine(ProfileTable(r.Profiles));
            sb.AppendLine();
            sb.AppendLine(sampledRows is null
                ? "## Dados completos (CSV)"
                : $"## Dados (AMOSTRA: primeiras {sampledRows:N0} de {r.Data.Rows.Count:N0} linhas — o arquivo completo é grande demais para enviar inteiro)");
            sb.AppendLine("<dados>");
            sb.AppendLine(csv);
            sb.AppendLine("</dados>");
            sb.AppendLine();
            sb.AppendLine("## Tarefa");
            sb.AppendLine(task);
            var warning = sampledRows is null ? null
                : $"A IA recebeu as primeiras {sampledRows:N0} de {r.Data.Rows.Count:N0} linhas (o arquivo é grande); as estatísticas por coluna foram calculadas sobre todas as linhas.";
            return (sb.ToString(), null, warning);
        }

        // PDFs vão no formato nativo quando o provedor suporta (a IA enxerga tabelas, gráficos e páginas escaneadas).
        if (r.Dataset.FileType == "pdf" && r.OriginalFile is { Length: > 0 } pdf && pdf.Length <= maxPdfBytes)
        {
            sb.AppendLine($"Páginas: {r.Data.PageCount}");
            sb.AppendLine("O documento PDF está anexado.");
            sb.AppendLine();
            sb.AppendLine("## Tarefa");
            sb.AppendLine(task);
            return (sb.ToString(), pdf, null);
        }

        var text = r.Data.Text ?? "";
        string? docWarning = null;
        if (text.Length > MaxDocumentChars)
        {
            text = text[..MaxDocumentChars];
            docWarning = $"O documento é muito longo; a IA analisou apenas os primeiros {MaxDocumentChars:N0} caracteres.";
        }
        sb.AppendLine();
        sb.AppendLine(docWarning is null ? "## Documento" : "## Documento (TRUNCADO — apenas o início)");
        sb.AppendLine("<documento>");
        sb.AppendLine(text);
        sb.AppendLine("</documento>");
        sb.AppendLine();
        sb.AppendLine("## Tarefa");
        sb.AppendLine(task);
        return (sb.ToString(), null, docWarning);
    }

    /// <returns>CSV e, se for amostra, quantas linhas couberam.</returns>
    private (string Csv, int? SampledRows) BuildCsv(ParsedDataset data)
    {
        var full = data.ToCsv();
        if (full.Length <= _options.MaxDataChars) return (full, null);

        var cut = Math.Max(full.LastIndexOf('\n', _options.MaxDataChars), 0);
        var csv = full[..cut];
        return (csv, csv.Count(c => c == '\n')); // descontado o cabeçalho, a última linha não tem '\n'
    }

    private static string ProfileTable(IReadOnlyList<ColumnProfile> profiles)
    {
        static string N(double? v) => v is null ? "" : v.Value.ToString("0.####", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("| coluna | tipo | preenchidos | vazios | distintos | soma | média | mediana | desvio | mín | máx | valores mais frequentes |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var p in profiles)
        {
            var min = p.Type == ColumnType.Date ? p.MinDate?.ToString("yyyy-MM-dd") : N(p.Min);
            var max = p.Type == ColumnType.Date ? p.MaxDate?.ToString("yyyy-MM-dd") : N(p.Max);
            var top = string.Join("; ", p.TopValues.Select(t => $"{t.Value} ({t.Count})")).Replace("|", "/");
            sb.AppendLine($"| {p.Name.Replace("|", "/")} | {p.Type} | {p.NonEmpty} | {p.Empty} | {p.Distinct} | {N(p.Sum)} | {N(p.Mean)} | {N(p.Median)} | {N(p.StdDev)} | {min} | {max} | {top} |");
        }
        return sb.ToString();
    }

    private static string TaskInstruction(ReportKind kind, string? question) => kind switch
    {
        ReportKind.Summary =>
            "Escreva um resumo executivo de no máximo ~250 palavras: do que tratam os dados, os 3 a 5 números ou fatos mais importantes e uma conclusão prática.",
        ReportKind.Insights =>
            "Liste os insights mais relevantes: tendências, padrões, concentrações (ex.: poucos itens respondendo pela maior parte do total), sazonalidade, outliers e anomalias, e problemas de qualidade dos dados. Para cada insight, mostre o número que o sustenta e por que ele importa. Termine com 3 sugestões de gráficos que valeriam a pena montar no DashTudo (eixo X, eixo Y e tipo).",
        ReportKind.FullReport =>
            "Produza um relatório completo, pronto para ser compartilhado, com as seções: 1) Resumo executivo; 2) Sobre os dados (origem, período, qualidade e limitações); 3) Análise detalhada, organizada pelas dimensões mais relevantes; 4) Tendências e anomalias; 5) Recomendações acionáveis; 6) Próximos passos e perguntas em aberto.",
        _ => $"Responda à pergunta do usuário com base nos dados. Se os dados não forem suficientes para responder, diga isso e explique o que faltaria.\n\nPergunta: {question}",
    };
}
