using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ERP.Web.Services.Reporting;

/// <summary>Xuất báo cáo ra Excel (.xlsx tự dựng, không cần thư viện ngoài) và CSV</summary>
public static class ReportExport
{
    public record Col(string Title, FieldKind Kind);

    public static (List<Col> Cols, List<object?[]> Rows, object?[]? Totals) From(ReportResult r) =>
        (r.Columns.Select(c => new Col(c.Title, c.Kind)).ToList(), r.Rows, r.Totals);

    // ------------------------------------------------------------------ CSV
    public static byte[] Csv(IList<Col> cols, IEnumerable<object?[]> rows, object?[]? totals)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(c => Q(c.Title))));
        foreach (var r in rows.Concat(totals != null ? new[] { totals } : Array.Empty<object?[]>()))
            sb.AppendLine(string.Join(",", r.Select((v, i) => Q(Text(v, cols[i].Kind)))));
        return new UTF8Encoding(true).GetBytes(sb.ToString());
    }

    private static string Q(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string Text(object? v, FieldKind k) => v switch
    {
        null => "",
        DateTime d => d.ToString("dd/MM/yyyy"),
        decimal m => k == FieldKind.Qty ? m.ToString("0.###", CultureInfo.InvariantCulture) : k == FieldKind.Percent ? m.ToString("0.##", CultureInfo.InvariantCulture) : m.ToString("0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };

    // ------------------------------------------------------------------ XLSX
    // Chỉ số kiểu (cellXfs): 0 mặc định · 1 tiêu đề cột · 2 tiền · 3 số lượng · 4 phần trăm · 5 ngày · 6 tiêu đề báo cáo · 7 chữ đậm · 8 tiền đậm · 9 số lượng đậm · 10 % đậm · 11 chữ nghiêng
    public static byte[] Xlsx(string sheetName, string title, IEnumerable<string> subtitle, IList<Col> cols, IEnumerable<object?[]> rows, object?[]? totals)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        var widths = cols.Select(c => Math.Max(10, Math.Min(48, c.Title.Length + 4))).ToArray();
        var data = rows.ToList();
        foreach (var r in data.Take(200)) for (var i = 0; i < cols.Count && i < r.Length; i++) widths[i] = Math.Max(widths[i], Math.Min(48, (Text(r[i], cols[i].Kind).Length) + 3));
        sb.Append("<cols>");
        for (var i = 0; i < cols.Count; i++) sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{widths[i]}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");
        var row = 1;
        void StrRow(string s, int style) { sb.Append($"<row r=\"{row}\">{Str(1, row, s, style)}</row>"); row++; }
        StrRow(title, 6);
        foreach (var s in subtitle.Where(x => !string.IsNullOrWhiteSpace(x))) StrRow(s, 11);
        row++;
        var header = row;
        sb.Append($"<row r=\"{row}\">");
        for (var i = 0; i < cols.Count; i++) sb.Append(Str(i + 1, row, cols[i].Title, 1));
        sb.Append("</row>"); row++;
        foreach (var r in data) { AppendRow(sb, row++, r, cols, false); }
        if (totals != null) AppendRow(sb, row++, totals, cols, true);
        sb.Append("</sheetData>");
        sb.Append($"<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{header}\" topLeftCell=\"A{header + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("</worksheet>");
        // sheetViews phải đứng trước cols / sheetData theo schema: dựng lại đúng thứ tự
        var xml = sb.ToString();
        var views = $"<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{header}\" topLeftCell=\"A{header + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>";
        xml = xml.Replace(views, "").Replace("<cols>", views + "<cols>");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{Esc(SheetName(sheetName))}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Add(zip, "xl/styles.xml", Styles);
            Add(zip, "xl/worksheets/sheet1.xml", xml);
        }
        return ms.ToArray();
    }

    private static void AppendRow(StringBuilder sb, int row, object?[] r, IList<Col> cols, bool total)
    {
        sb.Append($"<row r=\"{row}\">");
        for (var i = 0; i < cols.Count && i < r.Length; i++)
        {
            var v = r[i]; var k = cols[i].Kind;
            if (v == null) { if (total) sb.Append($"<c r=\"{Ref(i + 1, row)}\" s=\"7\"/>"); continue; }
            if (v is decimal m)
                sb.Append($"<c r=\"{Ref(i + 1, row)}\" s=\"{(total ? 8 : 2) + (k == FieldKind.Qty ? 1 : k == FieldKind.Percent ? 2 : 0)}\"><v>{m.ToString(CultureInfo.InvariantCulture)}</v></c>");
            else if (v is DateTime d)
                sb.Append($"<c r=\"{Ref(i + 1, row)}\" s=\"5\"><v>{(d.Date - new DateTime(1899, 12, 30)).TotalDays.ToString(CultureInfo.InvariantCulture)}</v></c>");
            else sb.Append(Str(i + 1, row, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", total ? 7 : 0));
        }
        sb.Append("</row>");
    }

    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"3\"><numFmt numFmtId=\"164\" formatCode=\"#,##0\"/><numFmt numFmtId=\"165\" formatCode=\"#,##0.###\"/><numFmt numFmtId=\"166\" formatCode=\"0.00&quot;%&quot;\"/></numFmts>" +
        "<fonts count=\"4\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><name val=\"Calibri\"/></font><font><i/><sz val=\"10\"/><color rgb=\"FF64748B\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE8EEF7\"/></patternFill></fill></fills>" +
        "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FF94A3B8\"/></bottom><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"12\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"164\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"165\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"166\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "</cellXfs></styleSheet>";

    private static string Str(int col, int row, string s, int style) =>
        $"<c r=\"{Ref(col, row)}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{Esc(s)}</t></is></c>";

    private static string Ref(int col, int row)
    {
        var s = ""; var c = col;
        while (c > 0) { c--; s = (char)('A' + c % 26) + s; c /= 26; }
        return s + row;
    }

    private static string SheetName(string s) => new string(s.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()) is { Length: > 0 } n ? (n.Length > 31 ? n[..31] : n) : "Báo cáo";

    private static readonly Regex Bad = new("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]", RegexOptions.Compiled);
    private static string Esc(string s) => System.Security.SecurityElement.Escape(Bad.Replace(s, ""))!;

    private static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
