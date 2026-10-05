using Microsoft.AspNetCore.Identity;

namespace DashTudo.Web.Data;

public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly? BirthDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Dataset> Datasets { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public enum DatasetKind
{
    Tabular = 0,
    Document = 1,
}

/// <summary>Metadados de um upload. O conteúdo fica em <see cref="DatasetContent"/> para manter as listagens leves.</summary>
public class Dataset
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string OriginalFileName { get; set; } = "";
    /// <summary>csv, xlsx, pdf, txt, manual...</summary>
    public string FileType { get; set; } = "";
    public DatasetKind Kind { get; set; }
    public long SizeBytes { get; set; }
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public bool IsFavorite { get; set; }

    /// <summary>Configuração do gráfico salva pelo usuário (JSON de <c>ChartSettings</c>).</summary>
    public string? ChartSettingsJson { get; set; }
    /// <summary>Rótulos personalizados das colunas (JSON de dicionário).</summary>
    public string? ColumnLabelsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DatasetContent? Content { get; set; }
    public List<AiReport> Reports { get; set; } = [];
}

public class DatasetContent
{
    public int DatasetId { get; set; }
    public Dataset? Dataset { get; set; }

    /// <summary>JSON (gzip) de <c>ParsedDataset</c>.</summary>
    public byte[] ParsedData { get; set; } = [];
    /// <summary>Arquivo original (apenas PDFs, para envio nativo à IA).</summary>
    public byte[]? OriginalFile { get; set; }
}

public enum ReportKind
{
    Summary = 0,
    Insights = 1,
    FullReport = 2,
    Question = 3,
}

public class AiReport
{
    public int Id { get; set; }
    public int DatasetId { get; set; }
    public Dataset? Dataset { get; set; }

    public ReportKind Kind { get; set; }
    public string? Question { get; set; }
    public string ContentMarkdown { get; set; } = "";
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
