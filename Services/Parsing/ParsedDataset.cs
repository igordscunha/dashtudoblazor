using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashTudo.Web.Services.Parsing;

public enum ColumnType
{
    Text = 0,
    Number = 1,
    Date = 2,
}

public sealed class ColumnDef
{
    public string Name { get; set; } = "";
    public ColumnType Type { get; set; }
}

/// <summary>
/// Representação normalizada de qualquer upload. Planilhas preenchem <see cref="Columns"/>/<see cref="Rows"/>;
/// documentos (PDF/TXT) preenchem <see cref="Text"/>. Valores numéricos são guardados em formato invariante.
/// </summary>
public sealed class ParsedDataset
{
    public List<ColumnDef> Columns { get; set; } = [];
    public List<string?[]> Rows { get; set; } = [];
    public string? Text { get; set; }
    public int PageCount { get; set; }
    public List<string> Warnings { get; set; } = [];

    [JsonIgnore]
    public bool IsTabular => Columns.Count > 0;

    public int IndexOf(string column) => Columns.FindIndex(c => c.Name == column);

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public byte[] ToCompressedBytes()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
        {
            JsonSerializer.Serialize(gz, this, JsonOptions);
        }
        return ms.ToArray();
    }

    public static ParsedDataset FromCompressedBytes(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<ParsedDataset>(gz, JsonOptions) ?? new ParsedDataset();
    }

    /// <summary>Exporta as linhas (todas ou as primeiras <paramref name="maxRows"/>) como CSV.</summary>
    public string ToCsv(int? maxRows = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(',', Columns.Select(c => Escape(c.Name))));
        foreach (var row in maxRows is null ? Rows : Rows.Take(maxRows.Value))
        {
            sb.AppendLine(string.Join(',', row.Select(Escape)));
        }
        return sb.ToString();

        static string Escape(string? v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
        }
    }
}

public static class ValueParser
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
        "dd/MM/yyyy", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "d/M/yyyy", "dd-MM-yyyy", "yyyy/MM/dd", "MM/yyyy", "yyyy-MM",
    ];

    /// <summary>Aceita "1234.5", "1,234.5", "1.234,5", "1234,5", "R$ 10,00", "12%".</summary>
    public static bool TryParseNumber(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().Replace("R$", "").Replace("US$", "").Replace("$", "").Replace("%", "").Replace(" ", "").Replace(" ", "");
        if (s.Length == 0) return false;

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            // O separador que aparece por último é o decimal.
            return lastComma > lastDot
                ? double.TryParse(s, NumberStyles.Number, PtBr, out value)
                : double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }
        if (lastComma >= 0)
        {
            // "1,5" (decimal pt-BR) vs "1,234" (milhar en-US): vírgula única seguida de 3 dígitos é tratada como milhar.
            var isThousands = s.Count(c => c == ',') > 1 || (s.Length - lastComma - 1 == 3 && s.IndexOf(',') == lastComma && !s.StartsWith("0,"));
            return isThousands
                ? double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
                : double.TryParse(s, NumberStyles.Number, PtBr, out value);
        }
        if (s.Count(c => c == '.') > 1)
        {
            return double.TryParse(s, NumberStyles.Number, PtBr, out value);
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseDate(string? raw, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        // Evita interpretar números puros (ex.: "2024") como data.
        if (s.All(char.IsDigit)) return false;
        return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value);
    }

    public static string FormatNumber(double v) =>
        Math.Abs(v) >= 1000 || v == Math.Floor(v) ? v.ToString("#,##0.##", PtBr) : v.ToString("#,##0.####", PtBr);

    /// <summary>Infere o tipo de cada coluna olhando para até 2000 valores não vazios.</summary>
    public static void InferColumnTypes(ParsedDataset ds)
    {
        for (var c = 0; c < ds.Columns.Count; c++)
        {
            int total = 0, numbers = 0, dates = 0;
            foreach (var row in ds.Rows)
            {
                var v = c < row.Length ? row[c] : null;
                if (string.IsNullOrWhiteSpace(v)) continue;
                total++;
                if (TryParseNumber(v, out _)) numbers++;
                else if (TryParseDate(v, out _)) dates++;
                if (total >= 2000) break;
            }
            ds.Columns[c].Type = total == 0 ? ColumnType.Text
                : numbers >= total * 0.9 ? ColumnType.Number
                : dates >= total * 0.9 ? ColumnType.Date
                : ColumnType.Text;
        }

        // Normaliza números para o formato invariante, facilitando análises posteriores.
        for (var c = 0; c < ds.Columns.Count; c++)
        {
            if (ds.Columns[c].Type != ColumnType.Number) continue;
            foreach (var row in ds.Rows)
            {
                if (c < row.Length && TryParseNumber(row[c], out var n))
                    row[c] = n.ToString("R", CultureInfo.InvariantCulture);
            }
        }
    }
}
