using System.Globalization;
using System.Text;
using ERP.Web.Data;

namespace ERP.Web.Services.Reporting;

/// <summary>Một dòng dữ liệu nguồn: khóa trường → giá trị (string / decimal / DateTime / null)</summary>
public class Row : Dictionary<string, object?>
{
    public Row() : base(StringComparer.Ordinal) { }
}

public enum DateMode { None, Range, AsOf }

/// <summary>Nguồn dữ liệu báo cáo: danh sách trường được phép dùng và cách nạp dữ liệu (đã phi chuẩn hóa)</summary>
public abstract class Dataset
{
    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Group { get; }
    public virtual string Description => "";
    /// <summary>Vai trò được dùng (rỗng = mọi người đã đăng nhập)</summary>
    public virtual string Roles => "";
    public virtual DateMode DateMode => DateMode.Range;
    public virtual string DateLabel => "Ngày chứng từ";
    public abstract IReadOnlyList<Field> Fields { get; }
    public abstract Task<List<Row>> LoadAsync(ErpDbContext db, DateTime? from, DateTime? to);
    public Field? Find(string key) => Fields.FirstOrDefault(f => f.Key == key);
}

/// <summary>
/// Bộ máy chạy báo cáo động trên dữ liệu nguồn đã nạp trong bộ nhớ: lọc → nhóm / bảng chéo → chỉ số → sắp xếp → top N.
/// Chỉ dùng các trường khai báo trong Dataset nên không có rủi ro SQL tùy ý.
/// </summary>
public static class ReportEngine
{
    public const int MaxSourceRows = 100_000;
    public const int MaxPivotColumns = 40;
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static (DateTime? From, DateTime? To) ResolveRange(ReportDef def, DateTime? today = null)
    {
        var t = (today ?? DateTime.Today).Date;
        DateTime Q(int back) { var q = (t.Month - 1) / 3; var s = new DateTime(t.Year, q * 3 + 1, 1).AddMonths(-3 * back); return s; }
        return def.DatePreset switch
        {
            "today" => (t, t),
            "thisMonth" => (new DateTime(t.Year, t.Month, 1), new DateTime(t.Year, t.Month, 1).AddMonths(1).AddDays(-1)),
            "lastMonth" => (new DateTime(t.Year, t.Month, 1).AddMonths(-1), new DateTime(t.Year, t.Month, 1).AddDays(-1)),
            "thisQuarter" => (Q(0), Q(0).AddMonths(3).AddDays(-1)),
            "lastQuarter" => (Q(1), Q(1).AddMonths(3).AddDays(-1)),
            "thisYear" => (new DateTime(t.Year, 1, 1), new DateTime(t.Year, 12, 31)),
            "lastYear" => (new DateTime(t.Year - 1, 1, 1), new DateTime(t.Year - 1, 12, 31)),
            "last30" => (t.AddDays(-29), t),
            "last90" => (t.AddDays(-89), t),
            "last365" => (t.AddDays(-364), t),
            "custom" => (def.From?.Date, def.To?.Date),
            _ => (null, null)
        };
    }

    /// <summary>Loại bỏ trường không tồn tại, giới hạn số lượng, chuẩn hóa chế độ</summary>
    public static ReportDef Sanitize(ReportDef def, Dataset ds)
    {
        var d = def.Clone();
        d.Dataset = ds.Key;
        d.Mode = d.Mode == "detail" ? "detail" : "grouped";
        d.Rows = d.Rows.Where(k => ds.Find(k) != null).Distinct().Take(4).ToList();
        if (d.PivotColumn != null && (ds.Find(d.PivotColumn) == null || d.Rows.Contains(d.PivotColumn))) d.PivotColumn = null;
        d.Measures = d.Measures.Where(m => ds.Find(m.Field) is { } f && (m.Agg is Agg.Count or Agg.CountDistinct || f.IsNumeric)
                && (m.Agg != Agg.Ratio || (ds.Find(m.Field2 ?? "") is { IsNumeric: true })))
            .Select(m => { if (m.Agg is Agg.Sum && !ds.Find(m.Field)!.Additive) m.Agg = Agg.Avg; return m; }).Take(8).ToList();
        d.Columns = d.Columns.Where(k => ds.Find(k) != null).Distinct().Take(30).ToList();
        d.Filters = d.Filters.Where(f => ds.Find(f.Field) != null).Take(12).ToList();
        d.Sort = d.Sort.Take(3).ToList();
        if (d.Top is <= 0) d.Top = null;
        if (d.Top > 5000) d.Top = 5000;
        d.Chart = d.Chart is "bar" or "line" or "pie" ? d.Chart : "none";
        if (d.Mode == "grouped" && d.Measures.Count == 0) d.Measures.Add(new MeasureDef { Field = ds.Fields.First().Key, Agg = Agg.Count });
        if (d.Mode == "detail" && d.Columns.Count == 0) d.Columns = ds.Fields.Take(8).Select(f => f.Key).ToList();
        if (d.PivotColumn != null && d.Measures.Count > 1) d.Measures = d.Measures.Take(1).ToList();
        return d;
    }

    public static async Task<ReportResult> RunAsync(Dataset ds, ReportDef rawDef, ErpDbContext db)
    {
        var def = Sanitize(rawDef, ds);
        var (from, to) = ResolveRange(def);
        if (ds.DateMode == DateMode.AsOf) { from = null; to ??= DateTime.Today; }
        else if (ds.DateMode == DateMode.None) { from = null; to = null; }
        var all = await ds.LoadAsync(db, from, to);
        var result = new ReportResult { DatasetTitle = ds.Title, From = from, To = to };
        if (all.Count >= MaxSourceRows) { all = all.Take(MaxSourceRows).ToList(); result.Truncated = true; result.Notes.Add($"Dữ liệu nguồn quá lớn, chỉ lấy {MaxSourceRows:N0} dòng đầu – hãy thu hẹp khoảng thời gian."); }

        var rows = all.Where(r => def.Filters.All(f => Match(r, ds.Find(f.Field)!, f))).ToList();
        result.SourceRows = rows.Count;
        if (def.Mode == "detail") BuildDetail(ds, def, rows, result);
        else if (def.PivotColumn != null) BuildPivot(ds, def, rows, result);
        else BuildGrouped(ds, def, rows, result);
        return result;
    }

    // ------------------------------------------------------------------ Lọc
    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD); var sb = new StringBuilder(d.Length);
        foreach (var c in d) { var cat = CharUnicodeInfo.GetUnicodeCategory(c); if (cat != UnicodeCategory.NonSpacingMark) sb.Append(c == 'đ' ? 'd' : c == 'Đ' ? 'D' : c); }
        return sb.ToString().ToLowerInvariant().Trim();
    }

    private static bool TryDec(string? s, out decimal v) =>
        decimal.TryParse((s ?? "").Trim().Replace(" ", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out v)
        || decimal.TryParse((s ?? "").Trim(), NumberStyles.Any, Vi, out v);

    private static bool TryDate(string? s, out DateTime d) =>
        DateTime.TryParseExact((s ?? "").Trim(), new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    private static bool Match(Row r, Field f, FilterDef flt)
    {
        r.TryGetValue(f.Key, out var v);
        var op = flt.Op;
        if (op == "empty") return v == null || (v is string s0 && s0.Length == 0);
        if (op == "notempty") return !(v == null || (v is string s1 && s1.Length == 0));
        var val = flt.Value ?? "";
        if (f.IsNumeric)
        {
            if (!TryDec(val, out var x)) return true;           // giá trị lọc không hợp lệ: bỏ qua bộ lọc
            var n = v == null ? 0m : Convert.ToDecimal(v);
            return op switch { "eq" => n == x, "ne" => n != x, "gt" => n > x, "ge" => n >= x, "lt" => n < x, "le" => n <= x, _ => true };
        }
        if (f.Kind == FieldKind.Date)
        {
            if (!TryDate(val, out var x)) return true;
            if (v is not DateTime dt) return false;
            return op switch { "eq" => dt.Date == x, "ne" => dt.Date != x, "gt" => dt.Date > x, "ge" => dt.Date >= x, "lt" => dt.Date < x, "le" => dt.Date <= x, _ => true };
        }
        var t = Norm(v as string); var q = Norm(val);
        return op switch
        {
            "eq" => t == q, "ne" => t != q, "contains" => t.Contains(q), "notcontains" => !t.Contains(q), "startswith" => t.StartsWith(q),
            "in" => val.Split(',', ';').Select(Norm).Where(x => x.Length > 0).Contains(t),
            _ => true
        };
    }

    // ------------------------------------------------------------------ Tính toán chỉ số
    private class Acc
    {
        public decimal Sum; public int Count; public decimal? Min, Max; public HashSet<string>? Distinct; public decimal Sum2;
    }

    private static Acc NewAcc(MeasureDef m) => new() { Distinct = m.Agg == Agg.CountDistinct ? new HashSet<string>() : null };

    private static void Add(Acc a, MeasureDef m, Row r, Dataset ds)
    {
        r.TryGetValue(m.Field, out var raw);
        switch (m.Agg)
        {
            case Agg.Count: a.Count++; break;
            case Agg.CountDistinct: a.Count++; a.Distinct!.Add(Convert.ToString(raw, CultureInfo.InvariantCulture) ?? ""); break;
            default:
                var v = raw == null ? 0m : Convert.ToDecimal(raw);
                a.Count++; a.Sum += v;
                a.Min = a.Min == null ? v : Math.Min(a.Min.Value, v); a.Max = a.Max == null ? v : Math.Max(a.Max.Value, v);
                if (m.Agg == Agg.Ratio) { r.TryGetValue(m.Field2 ?? "", out var d); a.Sum2 += d == null ? 0m : Convert.ToDecimal(d); }
                break;
        }
    }

    private static object? Value(Acc a, MeasureDef m) => m.Agg switch
    {
        Agg.Count => (decimal)a.Count,
        Agg.CountDistinct => (decimal)a.Distinct!.Count,
        Agg.Sum => a.Sum,
        Agg.Avg => a.Count == 0 ? null : Math.Round(a.Sum / a.Count, 4),
        Agg.Min => a.Min,
        Agg.Max => a.Max,
        Agg.Ratio => a.Sum2 == 0 ? null : Math.Round(a.Sum * 100m / a.Sum2, 2),
        _ => null
    };

    private static string MeasureTitle(MeasureDef m, Dataset ds)
    {
        if (!string.IsNullOrWhiteSpace(m.Title)) return m.Title!;
        var f = ds.Find(m.Field)?.Title ?? m.Field;
        return m.Agg switch
        {
            Agg.Sum => f, Agg.Count => "Số dòng", Agg.CountDistinct => $"Số {f.ToLowerInvariant()} (không trùng)", Agg.Avg => $"{f} (TB)",
            Agg.Min => $"{f} (nhỏ nhất)", Agg.Max => $"{f} (lớn nhất)", Agg.Ratio => $"{f} / {ds.Find(m.Field2 ?? "")?.Title ?? m.Field2} (%)", _ => f
        };
    }

    private static FieldKind MeasureKind(MeasureDef m, Dataset ds) => m.Agg switch
    {
        Agg.Count or Agg.CountDistinct => FieldKind.Number,
        Agg.Ratio => FieldKind.Percent,
        Agg.Avg => ds.Find(m.Field)!.Kind == FieldKind.Percent ? FieldKind.Percent : ds.Find(m.Field)!.Kind == FieldKind.Qty ? FieldKind.Qty : FieldKind.Money,
        _ => ds.Find(m.Field)!.Kind
    };

    private static string KeyOf(Row r, List<string> dims) =>
        string.Join("\u001f", dims.Select(d => r.TryGetValue(d, out var v) ? FormatKey(v) : ""));

    private static string FormatKey(object? v) => v switch { null => "", DateTime d => d.ToString("yyyy-MM-dd"), string s => s, _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };

    private static object? Display(object? v) => v is string { Length: 0 } ? "(trống)" : v ?? "(trống)";

    // ------------------------------------------------------------------ Tổng hợp theo nhóm
    private static void BuildGrouped(Dataset ds, ReportDef def, List<Row> rows, ReportResult res)
    {
        var dims = def.Rows;
        foreach (var d in dims) res.Columns.Add(new ResultColumn { Key = d, Title = ds.Find(d)!.Title, Kind = ds.Find(d)!.Kind });
        for (var i = 0; i < def.Measures.Count; i++)
            res.Columns.Add(new ResultColumn { Key = $"m{i}", Title = MeasureTitle(def.Measures[i], ds), Kind = MeasureKind(def.Measures[i], ds), IsMeasure = true });

        var groups = new Dictionary<string, (object?[] Dim, Acc[] Accs)>();
        foreach (var r in rows)
        {
            var key = KeyOf(r, dims);
            if (!groups.TryGetValue(key, out var g))
            {
                g = (dims.Select(d => r.TryGetValue(d, out var v) ? v : null).ToArray(), def.Measures.Select(NewAcc).ToArray());
                groups[key] = g;
            }
            for (var i = 0; i < def.Measures.Count; i++) Add(g.Accs[i], def.Measures[i], r, ds);
        }
        var table = groups.Values.Select(g => g.Dim.Select(Display).Concat(def.Measures.Select((m, i) => Value(g.Accs[i], m))).ToArray()).ToList();
        Finish(ds, def, res, table, dims.Count);

        if (def.ShowTotals && dims.Count > 0)
        {
            var accs = def.Measures.Select(NewAcc).ToArray();
            foreach (var r in rows) for (var i = 0; i < def.Measures.Count; i++) Add(accs[i], def.Measures[i], r, ds);
            res.Totals = new object?[] { "Tổng cộng" }.Concat(Enumerable.Repeat<object?>(null, Math.Max(0, dims.Count - 1))).Concat(def.Measures.Select((m, i) => Value(accs[i], m))).ToArray();
        }
    }

    // ------------------------------------------------------------------ Bảng chéo
    private static void BuildPivot(Dataset ds, ReportDef def, List<Row> rows, ReportResult res)
    {
        var dims = def.Rows; var m = def.Measures[0]; var pc = def.PivotColumn!;
        foreach (var d in dims) res.Columns.Add(new ResultColumn { Key = d, Title = ds.Find(d)!.Title, Kind = ds.Find(d)!.Kind });
        var colVals = rows.Select(r => FormatKey(r.TryGetValue(pc, out var v) ? v : null)).Distinct().OrderBy(x => x, StringComparer.Create(Vi, true)).ToList();
        if (colVals.Count > MaxPivotColumns) { colVals = colVals.Take(MaxPivotColumns).ToList(); res.Notes.Add($"Cột chéo có quá nhiều giá trị, chỉ hiển thị {MaxPivotColumns} cột đầu."); }
        var kind = MeasureKind(m, ds);
        foreach (var c in colVals) res.Columns.Add(new ResultColumn { Key = "pv:" + c, Title = c.Length == 0 ? "(trống)" : c, Kind = kind, IsMeasure = true });
        if (m.Agg is Agg.Sum or Agg.Count) res.Columns.Add(new ResultColumn { Key = "pv_total", Title = "Tổng", Kind = kind, IsMeasure = true });
        var addTotal = m.Agg is Agg.Sum or Agg.Count;

        var groups = new Dictionary<string, (object?[] Dim, Dictionary<string, Acc> Cells, Acc Tot)>();
        foreach (var r in rows)
        {
            var key = KeyOf(r, dims); var col = FormatKey(r.TryGetValue(pc, out var cv) ? cv : null);
            if (!colVals.Contains(col)) continue;
            if (!groups.TryGetValue(key, out var g)) { g = (dims.Select(d => r.TryGetValue(d, out var v) ? v : null).ToArray(), new(), NewAcc(m)); groups[key] = g; }
            if (!g.Cells.TryGetValue(col, out var a)) g.Cells[col] = a = NewAcc(m);
            Add(a, m, r, ds); Add(g.Tot, m, r, ds);
        }
        var table = groups.Values.Select(g => g.Dim.Select(Display)
            .Concat(colVals.Select(c => g.Cells.TryGetValue(c, out var a) ? Value(a, m) : (m.Agg is Agg.Sum or Agg.Count or Agg.CountDistinct ? 0m : null)))
            .Concat(addTotal ? new[] { Value(g.Tot, m) } : Array.Empty<object?>()).ToArray()).ToList();
        Finish(ds, def, res, table, dims.Count, firstSortMeasureIndex: Math.Min(res.Columns.Count - 1, dims.Count + colVals.Count));

        if (def.ShowTotals)
        {
            var colTot = colVals.Select(c => { var a = NewAcc(m); foreach (var r in rows.Where(r => FormatKey(r.TryGetValue(pc, out var v) ? v : null) == c)) Add(a, m, r, ds); return Value(a, m); }).ToList();
            var all = NewAcc(m); foreach (var r in rows) Add(all, m, r, ds);
            res.Totals = new object?[] { "Tổng cộng" }.Concat(Enumerable.Repeat<object?>(null, Math.Max(0, dims.Count - 1))).Concat(colTot).Concat(addTotal ? new[] { Value(all, m) } : Array.Empty<object?>()).ToArray();
            if (dims.Count == 0) res.Totals = colTot.Concat(addTotal ? new[] { Value(all, m) } : Array.Empty<object?>()).ToArray();
        }
    }

    // ------------------------------------------------------------------ Chi tiết
    private static void BuildDetail(Dataset ds, ReportDef def, List<Row> rows, ReportResult res)
    {
        foreach (var c in def.Columns) res.Columns.Add(new ResultColumn { Key = c, Title = ds.Find(c)!.Title, Kind = ds.Find(c)!.Kind, IsMeasure = ds.Find(c)!.IsNumeric });
        var table = rows.Select(r => def.Columns.Select(c => r.TryGetValue(c, out var v) ? v : null).ToArray()).ToList();
        Finish(ds, def, res, table, 0, detail: true);
        if (def.ShowTotals)
            res.Totals = def.Columns.Select((c, i) => ds.Find(c)!.Additive ? (object?)rows.Sum(r => r.TryGetValue(c, out var v) && v != null ? Convert.ToDecimal(v) : 0m) : (i == 0 ? "Tổng cộng" : null)).ToArray();
    }

    // ------------------------------------------------------------------ Sắp xếp, top N
    private static void Finish(Dataset ds, ReportDef def, ReportResult res, List<object?[]> table, int dimCount, bool detail = false, int? firstSortMeasureIndex = null)
    {
        var cmp = StringComparer.Create(Vi, true);
        int Compare(object? a, object? b)
        {
            if (a is null && b is null) return 0; if (a is null) return -1; if (b is null) return 1;
            if (a is decimal x && b is decimal y) return x.CompareTo(y);
            if (a is DateTime d1 && b is DateTime d2) return d1.CompareTo(d2);
            return cmp.Compare(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
        }
        var sorts = def.Sort.Where(s => res.Columns.Any(c => c.Key == s.Key)).ToList();
        if (sorts.Count == 0)
        {
            if (detail) { /* giữ thứ tự nguồn */ }
            else if (dimCount > 0)
            {
                // Nhóm theo thời gian / mã thì xếp tăng dần; còn lại xếp giảm dần theo chỉ số đầu tiên
                var timeLike = ds.Find(def.Rows[0]) is { } f0 && (f0.Kind == FieldKind.Date || f0.Key is "month" or "quarter" or "year");
                if (timeLike) sorts.Add(new SortDef { Key = def.Rows[0] });
                else sorts.Add(new SortDef { Key = res.Columns[firstSortMeasureIndex ?? dimCount].Key, Desc = true });
            }
        }
        if (sorts.Count > 0)
        {
            IOrderedEnumerable<object?[]>? ordered = null;
            foreach (var s in sorts)
            {
                var idx = res.Columns.FindIndex(c => c.Key == s.Key);
                var comparer = Comparer<object?>.Create(Compare);
                ordered = ordered == null
                    ? (s.Desc ? table.OrderByDescending(r => r[idx], comparer) : table.OrderBy(r => r[idx], comparer))
                    : (s.Desc ? ordered.ThenByDescending(r => r[idx], comparer) : ordered.ThenBy(r => r[idx], comparer));
            }
            table = ordered!.ToList();
        }
        if (def.Top.HasValue && def.Top.Value < table.Count) table = table.Take(def.Top.Value).ToList();
        res.Rows = table;
    }
}
