using System.Globalization;
using DashTudo.Web.Services.Parsing;

namespace DashTudo.Web.Services.Analysis;

public sealed class ColumnProfile
{
    public string Name { get; init; } = "";
    public ColumnType Type { get; init; }
    public int NonEmpty { get; init; }
    public int Empty { get; init; }
    public int Distinct { get; init; }

    public double? Sum { get; init; }
    public double? Mean { get; init; }
    public double? Median { get; init; }
    public double? StdDev { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }

    public DateTime? MinDate { get; init; }
    public DateTime? MaxDate { get; init; }

    public List<(string Value, int Count)> TopValues { get; init; } = [];
}

public enum Aggregation
{
    Sum,
    Average,
    Count,
    Min,
    Max,
}

public enum ChartKind
{
    Bar,
    HorizontalBar,
    Line,
    Area,
    Pie,
    Doughnut,
}

public enum DateGrouping
{
    None,
    Day,
    Month,
    Year,
}

public sealed class ChartSettings
{
    public string Title { get; set; } = "Meu Gráfico";
    public ChartKind Kind { get; set; } = ChartKind.Bar;
    public string? XColumn { get; set; }
    /// <summary>Colunas numéricas a plotar (uma série por coluna). Vazio + Count = contagem de linhas.</summary>
    public List<string> YColumns { get; set; } = [];
    public Aggregation Aggregation { get; set; } = Aggregation.Sum;
    public DateGrouping DateGrouping { get; set; } = DateGrouping.Month;
    /// <summary>"label", "value-desc", "value-asc".</summary>
    public string Sort { get; set; } = "label";
    public int MaxCategories { get; set; } = 30;
}

public sealed record ChartSeries(string Label, double[] Values);
public sealed record ChartData(string[] Labels, List<ChartSeries> Series, int OtherCategoriesGrouped);

public class DataAnalysisService
{
    public static bool TryNumber(string? v, out double n) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n);

    /// <summary>Para ordenação: valores não numéricos ficam no início.</summary>
    public static double NumberOrMin(string? v) => TryNumber(v, out var n) ? n : double.MinValue;

    public List<ColumnProfile> Profile(ParsedDataset ds)
    {
        var result = new List<ColumnProfile>(ds.Columns.Count);
        for (var c = 0; c < ds.Columns.Count; c++)
        {
            var col = ds.Columns[c];
            var counts = new Dictionary<string, int>();
            var numbers = new List<double>();
            DateTime? minDate = null, maxDate = null;
            var empty = 0;

            foreach (var row in ds.Rows)
            {
                var v = row[c];
                if (string.IsNullOrWhiteSpace(v))
                {
                    empty++;
                    continue;
                }
                counts[v] = counts.GetValueOrDefault(v) + 1;
                if (col.Type == ColumnType.Number && TryNumber(v, out var n)) numbers.Add(n);
                else if (col.Type == ColumnType.Date && ValueParser.TryParseDate(v, out var d))
                {
                    if (minDate is null || d < minDate) minDate = d;
                    if (maxDate is null || d > maxDate) maxDate = d;
                }
            }

            double? sum = null, mean = null, median = null, std = null, min = null, max = null;
            if (numbers.Count > 0)
            {
                numbers.Sort();
                sum = numbers.Sum();
                mean = sum / numbers.Count;
                median = numbers.Count % 2 == 1
                    ? numbers[numbers.Count / 2]
                    : (numbers[numbers.Count / 2 - 1] + numbers[numbers.Count / 2]) / 2;
                var m = mean.Value;
                std = Math.Sqrt(numbers.Sum(x => (x - m) * (x - m)) / numbers.Count);
                min = numbers[0];
                max = numbers[^1];
            }

            result.Add(new ColumnProfile
            {
                Name = col.Name,
                Type = col.Type,
                NonEmpty = ds.Rows.Count - empty,
                Empty = empty,
                Distinct = counts.Count,
                Sum = sum,
                Mean = mean,
                Median = median,
                StdDev = std,
                Min = min,
                Max = max,
                MinDate = minDate,
                MaxDate = maxDate,
                TopValues = col.Type == ColumnType.Number
                    ? []
                    : counts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => (kv.Key, kv.Value)).ToList(),
            });
        }
        return result;
    }

    /// <summary>Insights automáticos (sem IA), calculados sobre todas as linhas.</summary>
    public List<string> QuickInsights(ParsedDataset ds, IReadOnlyList<ColumnProfile> profiles)
    {
        var insights = new List<string>
        {
            $"📊 {ds.Rows.Count:N0} registros e {ds.Columns.Count} colunas",
            $"🔢 {profiles.Count(p => p.Type == ColumnType.Number)} colunas numéricas, {profiles.Count(p => p.Type == ColumnType.Date)} de data e {profiles.Count(p => p.Type == ColumnType.Text)} de texto",
        };

        var emptyCols = profiles.Where(p => p.Empty > 0).OrderByDescending(p => p.Empty).Take(3).ToList();
        if (emptyCols.Count > 0)
        {
            insights.Add("⚠️ Valores ausentes em: " + string.Join(", ", emptyCols.Select(p => $"{p.Name} ({p.Empty * 100.0 / ds.Rows.Count:0.#}%)")));
        }

        foreach (var p in profiles.Where(p => p.Type == ColumnType.Number && p.Sum is not null).Take(4))
        {
            insights.Add($"💰 {p.Name}: total {ValueParser.FormatNumber(p.Sum!.Value)}, média {ValueParser.FormatNumber(p.Mean!.Value)}, de {ValueParser.FormatNumber(p.Min!.Value)} a {ValueParser.FormatNumber(p.Max!.Value)}");
        }

        foreach (var p in profiles.Where(p => p.Type == ColumnType.Date && p.MinDate is not null).Take(2))
        {
            insights.Add($"📅 {p.Name}: de {p.MinDate:dd/MM/yyyy} a {p.MaxDate:dd/MM/yyyy}");
        }

        foreach (var p in profiles.Where(p => p.Type == ColumnType.Text && p.TopValues.Count > 0 && p.Distinct < ds.Rows.Count).Take(2))
        {
            var top = p.TopValues[0];
            insights.Add($"🏷️ {p.Name}: {p.Distinct:N0} valores distintos; mais frequente \"{top.Value}\" ({top.Count:N0}x)");
        }

        return insights;
    }

    public ChartData BuildChart(ParsedDataset ds, ChartSettings s)
    {
        var xIdx = s.XColumn is null ? -1 : ds.IndexOf(s.XColumn);
        if (xIdx < 0) return new ChartData([], [], 0);

        var yIdx = s.YColumns.Select(ds.IndexOf).Where(i => i >= 0 && ds.Columns[i].Type == ColumnType.Number).ToList();
        var countOnly = s.Aggregation == Aggregation.Count || yIdx.Count == 0;
        var xType = ds.Columns[xIdx].Type;

        // chave -> acumuladores por série
        var groups = new Dictionary<string, Acc[]>();
        var seriesCount = countOnly ? 1 : yIdx.Count;
        foreach (var row in ds.Rows)
        {
            var key = GroupKey(row[xIdx], xType, s.DateGrouping);
            if (key is null) continue;
            if (!groups.TryGetValue(key, out var accs))
            {
                accs = Enumerable.Range(0, seriesCount).Select(_ => new Acc()).ToArray();
                groups[key] = accs;
            }
            if (countOnly)
            {
                accs[0].Add(1);
                continue;
            }
            for (var i = 0; i < yIdx.Count; i++)
            {
                if (TryNumber(row[yIdx[i]], out var n)) accs[i].Add(n);
            }
        }

        var agg = countOnly ? Aggregation.Sum : s.Aggregation;
        var entries = groups.Select(g => (Key: g.Key, Values: g.Value.Select(a => a.Result(agg)).ToArray())).ToList();

        entries = s.Sort switch
        {
            "value-desc" => entries.OrderByDescending(e => e.Values[0]).ToList(),
            "value-asc" => entries.OrderBy(e => e.Values[0]).ToList(),
            _ when xType is ColumnType.Number => entries.OrderBy(e => TryNumber(e.Key, out var n) ? n : double.MaxValue).ToList(),
            _ => entries.OrderBy(e => e.Key, StringComparer.CurrentCulture).ToList(),
        };

        // Muitas categorias: mantém as maiores e agrupa o resto em "Outros" (exceto séries temporais/linhas).
        var others = 0;
        var max = Math.Clamp(s.MaxCategories, 2, 500);
        if (entries.Count > max)
        {
            var isSequential = s.Kind is ChartKind.Line or ChartKind.Area && s.Sort == "label";
            if (isSequential)
            {
                others = entries.Count - max;
                entries = entries.TakeLast(max).ToList();
            }
            else
            {
                var keep = entries.OrderByDescending(e => e.Values[0]).Take(max - 1).Select(e => e.Key).ToHashSet();
                var rest = entries.Where(e => !keep.Contains(e.Key)).ToList();
                others = rest.Count;
                var otherValues = new double[seriesCount];
                for (var i = 0; i < seriesCount; i++)
                {
                    otherValues[i] = agg is Aggregation.Average ? rest.Average(e => e.Values[i])
                        : agg is Aggregation.Min ? rest.Min(e => e.Values[i])
                        : agg is Aggregation.Max ? rest.Max(e => e.Values[i])
                        : rest.Sum(e => e.Values[i]);
                }
                entries = entries.Where(e => keep.Contains(e.Key)).ToList();
                entries.Add(("Outros", otherValues));
            }
        }

        var labels = entries.Select(e => e.Key).ToArray();
        var series = countOnly
            ? [new ChartSeries("Quantidade", entries.Select(e => e.Values[0]).ToArray())]
            : yIdx.Select((col, i) => new ChartSeries(ds.Columns[col].Name, entries.Select(e => Math.Round(e.Values[i], 4)).ToArray())).ToList();

        return new ChartData(labels, series, others);
    }

    private static string? GroupKey(string? raw, ColumnType type, DateGrouping grouping)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (type == ColumnType.Date && grouping != DateGrouping.None && ValueParser.TryParseDate(raw, out var d))
        {
            return grouping switch
            {
                DateGrouping.Day => d.ToString("yyyy-MM-dd"),
                DateGrouping.Month => d.ToString("yyyy-MM"),
                _ => d.ToString("yyyy"),
            };
        }
        return raw.Trim();
    }

    private sealed class Acc
    {
        private double _sum;
        private int _count;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;

        public void Add(double v)
        {
            _sum += v;
            _count++;
            if (v < _min) _min = v;
            if (v > _max) _max = v;
        }

        public double Result(Aggregation agg) => _count == 0 ? 0 : agg switch
        {
            Aggregation.Average => _sum / _count,
            Aggregation.Count => _count,
            Aggregation.Min => _min,
            Aggregation.Max => _max,
            _ => _sum,
        };
    }

    /// <summary>Sugere uma configuração inicial razoável para o gráfico.</summary>
    public ChartSettings SuggestChart(ParsedDataset ds, string title)
    {
        var date = ds.Columns.FirstOrDefault(c => c.Type == ColumnType.Date);
        var text = ds.Columns.FirstOrDefault(c => c.Type == ColumnType.Text);
        var number = ds.Columns.FirstOrDefault(c => c.Type == ColumnType.Number);
        var x = date ?? text ?? ds.Columns.FirstOrDefault();
        return new ChartSettings
        {
            Title = title,
            Kind = date is not null ? ChartKind.Line : ChartKind.Bar,
            XColumn = x?.Name,
            YColumns = number is not null && number != x ? [number.Name] : [],
            Aggregation = number is not null ? Aggregation.Sum : Aggregation.Count,
            Sort = date is not null ? "label" : "value-desc",
        };
    }
}
