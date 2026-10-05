using System.Text.Json;
using DashTudo.Web.Data;
using DashTudo.Web.Services.Analysis;
using DashTudo.Web.Services.Parsing;
using Microsoft.EntityFrameworkCore;

namespace DashTudo.Web.Services;

/// <summary>
/// Acesso aos datasets do usuário. Todas as consultas filtram por <c>userId</c>, então um usuário nunca
/// enxerga dados de outro. Usa <see cref="IDbContextFactory{TContext}"/> (recomendado no Blazor Server,
/// onde um circuito vive por muito tempo).
/// </summary>
public class DatasetService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<Dataset> CreateAsync(string userId, string name, string originalFileName, long sizeBytes, ParseResult parsed)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var dataset = new Dataset
        {
            UserId = userId,
            Name = name,
            OriginalFileName = originalFileName,
            FileType = parsed.FileType,
            Kind = parsed.Kind,
            SizeBytes = sizeBytes,
            RowCount = parsed.Data.Rows.Count,
            ColumnCount = parsed.Data.Columns.Count,
            Content = new DatasetContent
            {
                ParsedData = parsed.Data.ToCompressedBytes(),
                OriginalFile = parsed.OriginalFile,
            },
        };
        db.Datasets.Add(dataset);
        await db.SaveChangesAsync();
        dataset.Content = null;
        return dataset;
    }

    public async Task<List<Dataset>> ListAsync(string userId, string? search = null, DatasetKind? kind = null, bool favoritesOnly = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Datasets.AsNoTracking().Where(d => d.UserId == userId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(d => d.Name.Contains(s) || d.OriginalFileName.Contains(s) || (d.Description != null && d.Description.Contains(s)));
        }
        if (kind is not null) q = q.Where(d => d.Kind == kind);
        if (favoritesOnly) q = q.Where(d => d.IsFavorite);
        return await q
            .OrderByDescending(d => d.IsFavorite).ThenByDescending(d => d.UpdatedAt)
            .Select(d => new Dataset
            {
                Id = d.Id,
                Name = d.Name,
                Description = d.Description,
                OriginalFileName = d.OriginalFileName,
                FileType = d.FileType,
                Kind = d.Kind,
                SizeBytes = d.SizeBytes,
                RowCount = d.RowCount,
                ColumnCount = d.ColumnCount,
                IsFavorite = d.IsFavorite,
                CreatedAt = d.CreatedAt,
                UpdatedAt = d.UpdatedAt,
                Reports = d.Reports.Select(r => new AiReport { Id = r.Id }).ToList(),
            })
            .ToListAsync();
    }

    public async Task<Dataset?> GetAsync(string userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId);
    }

    public async Task<(ParsedDataset Data, byte[]? OriginalFile)?> LoadContentAsync(string userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var content = await db.DatasetContents.AsNoTracking()
            .Where(c => c.DatasetId == id && c.Dataset!.UserId == userId)
            .FirstOrDefaultAsync();
        return content is null ? null : (ParsedDataset.FromCompressedBytes(content.ParsedData), content.OriginalFile);
    }

    public async Task UpdateDetailsAsync(string userId, int id, string name, string? description)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Datasets.Where(d => d.Id == id && d.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Name, name)
                .SetProperty(d => d.Description, description)
                .SetProperty(d => d.UpdatedAt, DateTime.UtcNow));
    }

    public async Task SaveChartSettingsAsync(string userId, int id, ChartSettings settings, Dictionary<string, string> columnLabels)
    {
        var chartJson = JsonSerializer.Serialize(settings);
        var labelsJson = JsonSerializer.Serialize(columnLabels);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Datasets.Where(d => d.Id == id && d.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ChartSettingsJson, chartJson)
                .SetProperty(d => d.ColumnLabelsJson, labelsJson)
                .SetProperty(d => d.UpdatedAt, DateTime.UtcNow));
    }

    public async Task<bool> ToggleFavoriteAsync(string userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ds = await db.Datasets.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId);
        if (ds is null) return false;
        ds.IsFavorite = !ds.IsFavorite;
        await db.SaveChangesAsync();
        return ds.IsFavorite;
    }

    public async Task DeleteAsync(string userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Datasets.Where(d => d.Id == id && d.UserId == userId).ExecuteDeleteAsync();
    }

    public async Task<List<AiReport>> ListReportsAsync(string userId, int datasetId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AiReports.AsNoTracking()
            .Where(r => r.DatasetId == datasetId && r.Dataset!.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<AiReport> AddReportAsync(string userId, AiReport report)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Datasets.AnyAsync(d => d.Id == report.DatasetId && d.UserId == userId))
            throw new InvalidOperationException("Dataset não encontrado.");
        db.AiReports.Add(report);
        await db.Datasets.Where(d => d.Id == report.DatasetId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return report;
    }

    public async Task DeleteReportAsync(string userId, int reportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.AiReports.Where(r => r.Id == reportId && r.Dataset!.UserId == userId).ExecuteDeleteAsync();
    }

    public async Task<int> CountReportsTodayAsync(string userId)
    {
        var since = DateTime.UtcNow.Date;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AiReports.CountAsync(r => r.Dataset!.UserId == userId && r.CreatedAt >= since);
    }

    public async Task<(int Datasets, int Reports, long Bytes)> GetUserStatsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var datasets = await db.Datasets.Where(d => d.UserId == userId).CountAsync();
        var bytes = datasets == 0 ? 0 : await db.Datasets.Where(d => d.UserId == userId).SumAsync(d => d.SizeBytes);
        var reports = await db.AiReports.Where(r => r.Dataset!.UserId == userId).CountAsync();
        return (datasets, reports, bytes);
    }

    public static ChartSettings? ReadChartSettings(Dataset d) =>
        string.IsNullOrEmpty(d.ChartSettingsJson) ? null : JsonSerializer.Deserialize<ChartSettings>(d.ChartSettingsJson);

    public static Dictionary<string, string> ReadColumnLabels(Dataset d) =>
        string.IsNullOrEmpty(d.ColumnLabelsJson) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(d.ColumnLabelsJson) ?? [];
}
