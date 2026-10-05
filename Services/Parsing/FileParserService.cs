using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using DashTudo.Web.Data;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DashTudo.Web.Services.Parsing;

public sealed class UploadOptions
{
    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;
    public int MaxRows { get; set; } = 200_000;
    public int MaxColumns { get; set; } = 200;
}

public sealed record ParseResult(ParsedDataset Data, DatasetKind Kind, string FileType, byte[]? OriginalFile);

public class FileParseException(string message) : Exception(message);

public class FileParserService(IOptions<UploadOptions> options)
{
    public static readonly string[] SupportedExtensions = [".csv", ".tsv", ".txt", ".md", ".xlsx", ".xlsm", ".pdf"];
    public static string AcceptAttribute => string.Join(',', SupportedExtensions);

    private readonly UploadOptions _options = options.Value;

    public ParseResult Parse(byte[] bytes, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".csv" or ".tsv" => new ParseResult(ParseDelimited(DecodeText(bytes)), DatasetKind.Tabular, ext.TrimStart('.'), null),
            ".xlsx" or ".xlsm" => new ParseResult(ParseExcel(bytes), DatasetKind.Tabular, "xlsx", null),
            ".pdf" => new ParseResult(ParsePdf(bytes), DatasetKind.Document, "pdf", bytes),
            ".txt" or ".md" => ParseFreeText(DecodeText(bytes), ext.TrimStart('.')),
            _ => throw new FileParseException($"Formato '{ext}' não suportado. Use: {string.Join(", ", SupportedExtensions)}."),
        };
    }

    /// <summary>Texto colado pelo usuário: vira tabela se parecer CSV/TSV, senão documento.</summary>
    public ParseResult ParsePastedText(string text) => ParseFreeText(text, "manual");

    private ParseResult ParseFreeText(string text, string fileType)
    {
        if (LooksTabular(text))
        {
            return new ParseResult(ParseDelimited(text), DatasetKind.Tabular, fileType, null);
        }
        if (string.IsNullOrWhiteSpace(text)) throw new FileParseException("O conteúdo está vazio.");
        return new ParseResult(new ParsedDataset { Text = text.Trim(), PageCount = 1 }, DatasetKind.Document, fileType, null);
    }

    private static bool LooksTabular(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20).ToList();
        if (lines.Count < 2) return false;
        foreach (var sep in new[] { '\t', ';', ',', '|' })
        {
            var counts = lines.Select(l => l.Count(c => c == sep)).ToList();
            if (counts[0] >= 1 && counts.Count(c => c == counts[0]) >= lines.Count * 0.8) return true;
        }
        return false;
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            // UTF-8 estrito; se falhar, cai para Windows-1252/Latin1 (comum em CSV exportado pelo Excel em pt-BR).
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private ParsedDataset ParseDelimited(string text)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            DetectDelimiter = true,
            DetectDelimiterValues = [",", ";", "\t", "|"],
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
        };

        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, config);

        var records = new List<string?[]>();
        string[]? header = null;
        var truncated = false;
        while (csv.Read())
        {
            var record = csv.Parser.Record ?? [];
            if (record.All(string.IsNullOrWhiteSpace)) continue;
            if (header is null)
            {
                header = record;
                continue;
            }
            if (records.Count >= _options.MaxRows)
            {
                truncated = true;
                break;
            }
            records.Add(record.Select(v => string.IsNullOrWhiteSpace(v) ? null : v).ToArray());
        }

        if (header is null) throw new FileParseException("Não foi possível identificar o cabeçalho do arquivo.");
        var ds = BuildTabular(header, records);
        if (truncated) ds.Warnings.Add($"O arquivo tem mais de {_options.MaxRows:N0} linhas; apenas as primeiras {_options.MaxRows:N0} foram importadas.");
        return ds;
    }

    private ParsedDataset ParseExcel(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(ms);
        }
        catch (Exception ex)
        {
            throw new FileParseException($"Não foi possível abrir a planilha: {ex.Message}");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault(ws => ws.RangeUsed() is not null)
                ?? throw new FileParseException("A planilha está vazia.");
            var range = sheet.RangeUsed()!;
            var firstRow = range.FirstRow().RowNumber();
            var lastRow = range.LastRow().RowNumber();
            var firstCol = range.FirstColumn().ColumnNumber();
            var lastCol = Math.Min(range.LastColumn().ColumnNumber(), firstCol + _options.MaxColumns - 1);

            var header = Enumerable.Range(firstCol, lastCol - firstCol + 1)
                .Select(c => sheet.Cell(firstRow, c).GetFormattedString())
                .ToArray();

            var records = new List<string?[]>();
            var truncated = false;
            for (var r = firstRow + 1; r <= lastRow; r++)
            {
                if (records.Count >= _options.MaxRows)
                {
                    truncated = true;
                    break;
                }
                var row = new string?[header.Length];
                var any = false;
                for (var c = firstCol; c <= lastCol; c++)
                {
                    var value = CellToString(sheet.Cell(r, c));
                    row[c - firstCol] = value;
                    any |= value is not null;
                }
                if (any) records.Add(row);
            }

            var ds = BuildTabular(header, records);
            if (workbook.Worksheets.Count > 1) ds.Warnings.Add($"A planilha possui {workbook.Worksheets.Count} abas; foi importada a aba \"{sheet.Name}\".");
            if (truncated) ds.Warnings.Add($"Apenas as primeiras {_options.MaxRows:N0} linhas foram importadas.");
            return ds;
        }
    }

    private static string? CellToString(IXLCell cell)
    {
        var v = cell.Value;
        if (v.IsBlank) return null;
        if (v.IsNumber) return v.GetNumber().ToString("R", CultureInfo.InvariantCulture);
        if (v.IsDateTime)
        {
            var d = v.GetDateTime();
            return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss");
        }
        if (v.IsBoolean) return v.GetBoolean() ? "true" : "false";
        if (v.IsError) return null;
        var s = cell.GetFormattedString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private ParsedDataset BuildTabular(string[] header, List<string?[]> records)
    {
        var width = Math.Min(Math.Max(header.Length, records.Count == 0 ? 0 : records.Max(r => r.Length)), _options.MaxColumns);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<ColumnDef>(width);
        for (var i = 0; i < width; i++)
        {
            var name = i < header.Length && !string.IsNullOrWhiteSpace(header[i]) ? header[i].Trim() : $"Coluna {i + 1}";
            var unique = name;
            for (var n = 2; !used.Add(unique); n++) unique = $"{name} ({n})";
            columns.Add(new ColumnDef { Name = unique });
        }

        var rows = records.Select(r =>
        {
            if (r.Length == width) return r;
            var copy = new string?[width];
            Array.Copy(r, copy, Math.Min(r.Length, width));
            return copy;
        }).ToList();

        if (rows.Count == 0) throw new FileParseException("O arquivo não contém linhas de dados além do cabeçalho.");

        var ds = new ParsedDataset { Columns = columns, Rows = rows };
        ValueParser.InferColumnTypes(ds);
        return ds;
    }

    private static ParsedDataset ParsePdf(byte[] bytes)
    {
        try
        {
            using var doc = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in doc.GetPages())
            {
                sb.AppendLine($"--- Página {page.Number} ---");
                sb.AppendLine(ContentOrderTextExtractor.GetText(page));
            }
            var ds = new ParsedDataset { Text = sb.ToString().Trim(), PageCount = doc.NumberOfPages };
            if (ds.Text.Replace("--- Página", "").Trim().Length < 50 * doc.NumberOfPages / 2)
            {
                ds.Warnings.Add("Pouco texto foi extraído (o PDF pode ser escaneado). A análise por IA usará o PDF original, que suporta imagens.");
            }
            return ds;
        }
        catch (Exception ex) when (ex is not FileParseException)
        {
            throw new FileParseException($"Não foi possível ler o PDF: {ex.Message}");
        }
    }
}
