using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Html;

namespace ERP.Web.Services.Reporting;

/// <summary>Biểu đồ SVG đơn giản (cột ngang / đường / tròn) vẽ từ kết quả báo cáo: chỉ số đầu tiên theo nhóm đầu tiên</summary>
public static class ReportChart
{
    private static readonly string[] Palette = { "#2563eb", "#f59e0b", "#10b981", "#ef4444", "#8b5cf6", "#14b8a6", "#f97316", "#64748b", "#ec4899", "#84cc16", "#0ea5e9", "#a16207" };
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Short(decimal v)
    {
        var a = Math.Abs(v);
        return a >= 1_000_000_000m ? (v / 1_000_000_000m).ToString("0.##", CultureInfo.GetCultureInfo("vi-VN")) + " tỷ"
            : a >= 1_000_000m ? (v / 1_000_000m).ToString("0.#", CultureInfo.GetCultureInfo("vi-VN")) + " tr"
            : v.ToString("#,##0.##", CultureInfo.GetCultureInfo("vi-VN"));
    }

    public static bool CanRender(ReportDef def, ReportResult r) =>
        def.Mode == "grouped" && def.Chart != "none" && def.PivotColumn == null && def.Rows.Count > 0 && r.Rows.Count > 0 && r.Columns.Count > def.Rows.Count;

    public static IHtmlContent Render(ReportDef def, ReportResult r)
    {
        if (!CanRender(def, r)) return HtmlString.Empty;
        var d = def.Rows.Count;
        // Vẽ chỉ số đang dùng để sắp xếp (nếu có), không thì chỉ số đầu tiên
        var vi = r.Columns.FindIndex(c => c.IsMeasure && def.Sort.Count > 0 && c.Key == def.Sort[0].Key);
        if (vi < d) vi = d;
        var take = def.Chart == "pie" ? 10 : def.Chart == "line" ? 36 : 15;
        var data = r.Rows.Take(take).Select(row => (Label: string.Join(" · ", row.Take(d).Select(x => x is DateTime dt ? dt.ToString("dd/MM/yyyy") : Convert.ToString(x, CultureInfo.InvariantCulture))),
            Value: row[vi] is null ? 0m : Convert.ToDecimal(row[vi]))).ToList();
        var title = r.Columns[vi].Title;
        var html = def.Chart switch { "line" => Line(data, title), "pie" => Pie(data, title), _ => Bar(data, title) };
        return new HtmlString(html);
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static string Bar(List<(string Label, decimal Value)> data, string title)
    {
        const int w = 900, left = 230, right = 110, rowH = 28, top = 26;
        var h = top + data.Count * rowH + 8;
        var min = Math.Min(0m, data.Min(x => x.Value)); var max = Math.Max(0m, data.Max(x => x.Value));
        var span = (double)(max - min); if (span == 0) span = 1;
        double X(decimal v) => left + (double)(v - min) / span * (w - left - right);
        var zero = X(0);
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {w} {h}\" role=\"img\" aria-label=\"{E(title)}\" class=\"rpt-chart\">");
        sb.Append($"<text x=\"{left}\" y=\"14\" class=\"rc-title\">{E(title)}</text>");
        sb.Append($"<line x1=\"{F(zero)}\" y1=\"{top - 6}\" x2=\"{F(zero)}\" y2=\"{h - 4}\" class=\"rc-axis\"/>");
        for (var i = 0; i < data.Count; i++)
        {
            var y = top + i * rowH; var v = data[i].Value;
            var x1 = Math.Min(zero, X(v)); var bw = Math.Max(1, Math.Abs(X(v) - zero));
            sb.Append($"<text x=\"{left - 8}\" y=\"{y + 15}\" text-anchor=\"end\" class=\"rc-label\">{E(Cut(data[i].Label, 34))}<title>{E(data[i].Label)}</title></text>");
            sb.Append($"<rect x=\"{F(x1)}\" y=\"{y + 4}\" width=\"{F(bw)}\" height=\"{rowH - 10}\" rx=\"3\" class=\"rc-bar{(v < 0 ? " neg" : "")}\"><title>{E(data[i].Label)}: {N(v)}</title></rect>");
            sb.Append($"<text x=\"{F(v >= 0 ? x1 + bw + 6 : x1 - 6)}\" y=\"{y + 16}\" text-anchor=\"{(v >= 0 ? "start" : "end")}\" class=\"rc-val\">{E(Short(v))}</text>");
        }
        return sb.Append("</svg>").ToString();
    }

    private static string Line(List<(string Label, decimal Value)> data, string title)
    {
        const int w = 900, h = 300, l = 60, r = 20, t = 34, b = 56;
        var min = Math.Min(0m, data.Min(x => x.Value)); var max = Math.Max(0m, data.Max(x => x.Value));
        if (max == min) max = min + 1;
        double Y(decimal v) => t + (double)(max - v) / (double)(max - min) * (h - t - b);
        double X(int i) => data.Count == 1 ? l + (w - l - r) / 2.0 : l + i * (w - l - r) / (double)(data.Count - 1);
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {w} {h}\" role=\"img\" aria-label=\"{E(title)}\" class=\"rpt-chart\">");
        sb.Append($"<text x=\"{l}\" y=\"16\" class=\"rc-title\">{E(title)}</text>");
        for (var g = 0; g <= 4; g++)
        {
            var v = min + (max - min) * g / 4; var y = Y(v);
            sb.Append($"<line x1=\"{l}\" y1=\"{F(y)}\" x2=\"{w - r}\" y2=\"{F(y)}\" class=\"rc-grid\"/><text x=\"{l - 6}\" y=\"{F(y + 4)}\" text-anchor=\"end\" class=\"rc-label\">{E(Short(v))}</text>");
        }
        sb.Append("<polyline fill=\"none\" class=\"rc-line\" points=\"" + string.Join(" ", data.Select((p, i) => $"{F(X(i))},{F(Y(p.Value))}")) + "\"/>");
        var step = Math.Max(1, data.Count / 12);
        for (var i = 0; i < data.Count; i++)
        {
            sb.Append($"<circle cx=\"{F(X(i))}\" cy=\"{F(Y(data[i].Value))}\" r=\"4\" class=\"rc-dot\"><title>{E(data[i].Label)}: {N(data[i].Value)}</title></circle>");
            if (i % step == 0) sb.Append($"<text x=\"{F(X(i))}\" y=\"{h - b + 18}\" text-anchor=\"middle\" class=\"rc-label\">{E(Cut(data[i].Label, 10))}</text>");
        }
        return sb.Append("</svg>").ToString();
    }

    private static string Pie(List<(string Label, decimal Value)> data, string title)
    {
        var pos = data.Where(x => x.Value > 0).ToList();
        if (pos.Count == 0) return "<div class=\"text-muted small\">Biểu đồ tròn cần giá trị dương.</div>";
        var total = pos.Sum(x => x.Value);
        const int w = 900, h = 300, cx = 150, cy = 150, rad = 120;
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {w} {h}\" role=\"img\" aria-label=\"{E(title)}\" class=\"rpt-chart\">");
        sb.Append($"<text x=\"310\" y=\"22\" class=\"rc-title\">{E(title)}</text>");
        double a0 = -Math.PI / 2;
        for (var i = 0; i < pos.Count; i++)
        {
            var frac = (double)(pos[i].Value / total); var a1 = a0 + frac * 2 * Math.PI;
            var color = Palette[i % Palette.Length];
            if (pos.Count == 1) sb.Append($"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"{rad}\" fill=\"{color}\"><title>{E(pos[i].Label)}</title></circle>");
            else
            {
                var x0 = cx + rad * Math.Cos(a0); var y0 = cy + rad * Math.Sin(a0); var x1 = cx + rad * Math.Cos(a1); var y1 = cy + rad * Math.Sin(a1);
                sb.Append($"<path d=\"M{cx},{cy} L{F(x0)},{F(y0)} A{rad},{rad} 0 {(frac > 0.5 ? 1 : 0)} 1 {F(x1)},{F(y1)} Z\" fill=\"{color}\" stroke=\"#fff\" stroke-width=\"1.5\"><title>{E(pos[i].Label)}: {N(pos[i].Value)} ({frac * 100:0.#}%)</title></path>");
            }
            sb.Append($"<rect x=\"310\" y=\"{40 + i * 24}\" width=\"12\" height=\"12\" rx=\"2\" fill=\"{color}\"/><text x=\"330\" y=\"{51 + i * 24}\" class=\"rc-label\">{E(Cut(pos[i].Label, 40))} — {E(Short(pos[i].Value))} ({frac * 100:0.#}%)</text>");
            a0 = a1;
        }
        return sb.Append("</svg>").ToString();
    }
}

/// <summary>Định dạng hiển thị ô báo cáo theo kiểu trường</summary>
public static class ReportFormat
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    public static string Cell(object? v, FieldKind k) => v switch
    {
        null => "",
        DateTime d => d.ToString("dd/MM/yyyy"),
        decimal m => k switch
        {
            FieldKind.Qty => m.ToString("#,##0.###", Vi),
            FieldKind.Percent => m.ToString("#,##0.##", Vi) + "%",
            FieldKind.Number => m % 1 == 0 ? m.ToString("#,##0", Vi) : m.ToString("#,##0.##", Vi),
            _ => m.ToString("#,##0", Vi)
        },
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };
    public static bool IsRight(FieldKind k) => k is FieldKind.Number or FieldKind.Money or FieldKind.Qty or FieldKind.Percent;
}
