using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace ERP.Web.Infrastructure;

/// <summary>Đọc bảng tính đơn giản: .xlsx (sheet đầu tiên) hoặc .csv (UTF-8, phân cách , hoặc ;). Dòng đầu là tiêu đề cột.</summary>
public static class TabularReader
{
    public static List<Dictionary<string, string>> Read(Stream input, string fileName)
    {
        using var ms = new MemoryStream(); input.CopyTo(ms); ms.Position = 0;
        var rows = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? Csv(ms) : Xlsx(ms);
        rows = rows.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (rows.Count == 0) return new();
        var header = rows[0].Select(h => Key(h)).ToList();
        return rows.Skip(1).Select(r => header.Select((h, i) => (h, v: i < r.Count ? r[i].Trim() : "")).Where(x => x.h.Length > 0).GroupBy(x => x.h).ToDictionary(g => g.Key, g => g.First().v)).ToList();
    }

    /// <summary>Chuẩn hóa tiêu đề cột: bỏ dấu, chữ thường, bỏ khoảng trắng và ký tự đặc biệt (VD "Mã số thuế" → "masothue")</summary>
    public static string Key(string s)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD); var sb = new StringBuilder();
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var ch = c == 'đ' || c == 'Đ' ? 'd' : char.ToLowerInvariant(c);
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string? Get(Dictionary<string, string> row, params string[] names)
    {
        foreach (var n in names) if (row.TryGetValue(Key(n), out var v) && v.Length > 0) return v;
        return null;
    }

    public static decimal Dec(string? s, decimal def = 0)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        s = s.Trim().Replace(" ", "");
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && !(s.Contains('.') && s.Contains(',') && s.IndexOf('.') < s.IndexOf(','))) return v;
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.GetCultureInfo("vi-VN"), out v) ? v : def;
    }

    public static DateTime? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial > 20000 && serial < 80000) return DateTime.FromOADate(serial).Date;
        return DateTime.TryParseExact(s.Trim(), new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;
    }

    private static List<List<string>> Csv(Stream s)
    {
        var text = new StreamReader(s, Encoding.UTF8, true).ReadToEnd();
        var first = text.Split('\n').FirstOrDefault() ?? "";
        var sep = first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var q = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (q) { if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; } else cell.Append(c); continue; }
            if (c == '"') q = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add(row); row = new(); }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    private static List<List<string>> Xlsx(Stream s)
    {
        using var zip = new ZipArchive(s, ZipArchiveMode.Read);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        var sst = zip.GetEntry("xl/sharedStrings.xml");
        if (sst != null) using (var r = sst.Open()) shared = XDocument.Load(r).Root!.Elements(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList();
        var sheet = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml")).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName).FirstOrDefault()
            ?? throw new InvalidDataException("Không tìm thấy trang tính trong file Excel.");
        XDocument doc; using (var r = sheet.Open()) doc = XDocument.Load(r);
        var rows = new List<List<string>>();
        foreach (var rEl in doc.Descendants(ns + "row"))
        {
            var row = new List<string>();
            foreach (var c in rEl.Elements(ns + "c"))
            {
                var refAttr = (string?)c.Attribute("r") ?? "";
                var col = 0; foreach (var ch in refAttr.TakeWhile(char.IsLetter)) col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                while (col > 0 && row.Count < col - 1) row.Add("");
                var t = (string?)c.Attribute("t"); var v = c.Element(ns + "v")?.Value ?? "";
                row.Add(t == "s" && int.TryParse(v, out var si) && si < shared.Count ? shared[si] : t == "inlineStr" ? string.Concat(c.Descendants(ns + "t").Select(x => x.Value)) : v);
            }
            rows.Add(row);
        }
        return rows;
    }
}
