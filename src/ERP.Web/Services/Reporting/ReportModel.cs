using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERP.Web.Services.Reporting;

public enum FieldKind { Text, Number, Money, Qty, Percent, Date }

/// <summary>Một trường dữ liệu của nguồn dữ liệu báo cáo (chỉ các trường khai báo ở đây mới dùng được – không có truy vấn tự do)</summary>
public record Field(string Key, string Title, FieldKind Kind)
{
    public bool IsNumeric => Kind is FieldKind.Number or FieldKind.Money or FieldKind.Qty or FieldKind.Percent;
    /// <summary>Có thể cộng dồn (Percent là tỷ lệ nên không cộng)</summary>
    public bool Additive => Kind is FieldKind.Number or FieldKind.Money or FieldKind.Qty;
}

public enum Agg { Sum, Count, CountDistinct, Avg, Min, Max, Ratio }

public class MeasureDef
{
    public string Field { get; set; } = "";
    public Agg Agg { get; set; } = Agg.Sum;
    /// <summary>Mẫu số của chỉ số tỷ lệ (Agg = Ratio): Σ Field / Σ Field2 × 100</summary>
    public string? Field2 { get; set; }
    public string? Title { get; set; }
}

public class FilterDef
{
    public string Field { get; set; } = "";
    /// <summary>eq, ne, contains, notcontains, startswith, in, gt, ge, lt, le, empty, notempty</summary>
    public string Op { get; set; } = "eq";
    public string? Value { get; set; }
}

public class SortDef
{
    public string Key { get; set; } = "";
    public bool Desc { get; set; }
}

/// <summary>Định nghĩa một báo cáo động (lưu dạng JSON)</summary>
public class ReportDef
{
    public string Dataset { get; set; } = "";
    /// <summary>all, today, thisMonth, lastMonth, thisQuarter, lastQuarter, thisYear, lastYear, last30, last90, last365, custom</summary>
    public string DatePreset { get; set; } = "all";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    /// <summary>grouped = tổng hợp theo nhóm / chéo; detail = liệt kê chi tiết</summary>
    public string Mode { get; set; } = "grouped";
    public List<string> Rows { get; set; } = new();
    public string? PivotColumn { get; set; }
    public List<MeasureDef> Measures { get; set; } = new();
    public List<string> Columns { get; set; } = new();
    public List<FilterDef> Filters { get; set; } = new();
    public List<SortDef> Sort { get; set; } = new();
    public int? Top { get; set; }
    /// <summary>none, bar, line, pie</summary>
    public string Chart { get; set; } = "none";
    public bool ShowTotals { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static ReportDef? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ReportDef>(json, Json); } catch { return null; }
    }
    public ReportDef Clone() => FromJson(ToJson())!;
}

public class ResultColumn
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public FieldKind Kind { get; init; }
    public bool IsMeasure { get; init; }
}

public class ReportResult
{
    public string DatasetTitle { get; set; } = "";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public List<ResultColumn> Columns { get; set; } = new();
    public List<object?[]> Rows { get; set; } = new();
    public object?[]? Totals { get; set; }
    /// <summary>Số dòng dữ liệu nguồn sau khi lọc</summary>
    public int SourceRows { get; set; }
    public bool Truncated { get; set; }
    public List<string> Notes { get; set; } = new();
}
