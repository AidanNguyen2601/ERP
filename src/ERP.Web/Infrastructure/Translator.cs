using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ERP.Web.Infrastructure;

/// <summary>
/// Dịch giao diện sang tiếng Anh khi người dùng chọn ngôn ngữ "en". Từ điển Việt → Anh (Resources/i18n/en.tsv, nhúng vào assembly) gồm:
/// - câu cố định ("Lưu nháp" → "Save draft");
/// - mẫu có tham số ("Đã lưu {0}." → "Saved {0}."): phần tham số được dịch đệ quy (VD tên trạng thái lồng trong câu).
/// Nội dung là dữ liệu người dùng nhập (tên khách hàng, hàng hóa...) không có trong từ điển nên giữ nguyên.
/// </summary>
public static class Translator
{
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<Template>> Buckets = new(StringComparer.Ordinal);
    private static readonly List<Template> Leading = new();      // mẫu bắt đầu bằng tham số
    private static readonly List<(string Vi, string En)> Fragments = new();
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly Regex VnChar = new("[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđĐÀÁẠẢÃÂẦẤẬẨẪĂẰẮẶẲẴÈÉẸẺẼÊỀẾỆỂỄÌÍỊỈĨÒÓỌỎÕÔỒỐỘỔỖƠỜỚỢỞỠÙÚỤỦŨƯỪỨỰỬỮỲÝỴỶỸ]", RegexOptions.Compiled);

    private static readonly Regex ShortMillion = new(@"(\d) tr\b", RegexOptions.Compiled);
    private static readonly Regex CodeSplit = new(@"^(\p{L}[^\d]*?)\s+([A-Z0-9][A-Z0-9\-/.]*\d[A-Z0-9\-/.]*)(\b.*)?$", RegexOptions.Compiled);

    /// <summary>Literal: số chữ cái cố định trong mẫu (đo độ cụ thể của mẫu)</summary>
    private sealed record Template(Regex Pattern, string En, int[] Order, int Literal);

    public static int Count => Exact.Count + Buckets.Values.Sum(b => b.Count) + Leading.Count;

    static Translator()
    {
        var asm = typeof(Translator).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("en.tsv", StringComparison.OrdinalIgnoreCase));
        if (name == null) return;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var tab = line.IndexOf('\t'); if (tab <= 0) continue;
            Add(Norm(line[..tab]), line[(tab + 1)..].Trim());
        }
        Fragments.Sort((a, b) => b.Vi.Length.CompareTo(a.Vi.Length));
        // Mẫu cụ thể (nhiều chữ cố định) thử trước mẫu chung: "Sửa định mức {0}" trước "Sửa {0}"
        Leading.Sort((a, b) => b.Literal.CompareTo(a.Literal));
        foreach (var list in Buckets.Values) list.Sort((a, b) => b.Literal.CompareTo(a.Literal));
    }

    private static string Norm(string s) => Regex.Replace(s.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();

    private static void Add(string vi, string en)
    {
        if (vi.Length == 0 || en.Length == 0) return;
        if (!Regex.IsMatch(vi, @"\{\d+\}"))
        {
            Exact.TryAdd(vi, en);
            if (vi.Length >= 10 && VnChar.IsMatch(vi)) Fragments.Add((vi, en));
            return;
        }
        // Mẫu: phần chữ thoát regex, {n} → nhóm bắt (.+?)
        var parts = Regex.Split(vi, @"(\{\d+\})");
        var sb = new StringBuilder("^"); var order = new List<int>();
        foreach (var p in parts)
        {
            var m = Regex.Match(p, @"^\{(\d+)\}$");
            if (m.Success) { sb.Append("(.+?)"); order.Add(int.Parse(m.Groups[1].Value)); }
            else sb.Append(Regex.Escape(p).Replace("\\ ", "\\s+"));
        }
        sb.Append('$');
        var literal = Regex.Replace(vi, @"\{\d+\}", "").Count(char.IsLetter);
        var t = new Template(new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), en, order.ToArray(), literal);
        if (vi.StartsWith('{')) { Leading.Add(t); return; }
        var key = FirstWord(vi);
        if (!Buckets.TryGetValue(key, out var list)) Buckets[key] = list = new();
        list.Add(t);
    }

    private static string FirstWord(string s) { var i = 0; while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{') i++; return s[..i]; }

    public static bool HasVietnamese(string s) => VnChar.IsMatch(s);

    /// <summary>Dịch một đoạn văn bản (giữ khoảng trắng đầu / cuối)</summary>
    public static string Translate(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lead = text.Length - text.TrimStart().Length; var trail = text.Length - text.TrimEnd().Length;
        var core = text.Trim();
        if (!VnChar.IsMatch(core))
        {
            // Tiếng Việt không dấu ("Doanh thu", "Xem →") chỉ dịch khi khớp nguyên câu; số tiền rút gọn "723,6 tr" → "723,6 m"
            if (Exact.TryGetValue(Norm(core), out var plain)) return text[..lead] + plain + text[(text.Length - trail)..];
            // Viết tắt không dấu + số: "MST: 0312345678", "TK 131"
            var ab = Regex.Match(core, @"^(MST|TK|SL)(:?\s+[\d\s.,/-]+)$");
            if (ab.Success) return text[..lead] + (ab.Groups[1].Value switch { "MST" => "Tax ID", "TK" => "Acct", _ => "Qty" }) + ab.Groups[2].Value + text[(text.Length - trail)..];
            return core.Contains(" tr") ? ShortMillion.Replace(text, "$1 m") : text;
        }
        if (Cache.Count > 100_000) Cache.Clear();   // nội dung có dữ liệu người dùng → giới hạn bộ nhớ đệm
        var res = Cache.GetOrAdd(core, k => ShortMillion.Replace(Core(Norm(k), 0), "$1 m"));
        return text[..lead] + res + text[(text.Length - trail)..];
    }

    private static readonly ConcurrentDictionary<(string, int), string> CoreCache = new();

    /// <summary>Có nhớ kết quả theo (chuỗi, độ sâu): các cách tách câu gọi lại cùng một cụm nhiều lần</summary>
    private static string Core(string s, int depth)
    {
        if (!VnChar.IsMatch(s)) return s;
        if (CoreCache.Count > 200_000) CoreCache.Clear();
        return CoreCache.GetOrAdd((s, depth), k => CoreRaw(k.Item1, k.Item2));
    }

    private static string CoreRaw(string s, int depth)
    {
        if (Exact.TryGetValue(s, out var en)) return en;
        // "đơn vị tính" → tra "Đơn vị tính" rồi hạ chữ đầu
        if (char.IsLower(s[0]) && Exact.TryGetValue(char.ToUpper(s[0]) + s[1..], out var up) && up.Length > 0) return char.ToLower(up[0]) + up[1..];
        if (NumberWords(s) is { } words) return words;
        if (depth < 4)
        {
            // Thử lần lượt các cách tách câu; nhận ngay cách dịch trọn vẹn, nếu không thì giữ cách còn ít tiếng Việt nhất
            string? best = null; var bestVn = VnCount(s);
            foreach (var c in Strategies(s, depth))
            {
                if (c == s) continue;
                var vn = VnCount(c);
                if (vn == 0) return c;
                if (vn < bestVn) { best = c; bestVn = vn; }
            }
            if (best != null) return best;
        }
        // Thay các cụm đã biết trong câu (cụm dài trước, theo ranh giới từ)
        var outp = s;
        foreach (var (vi, enf) in Fragments)
        {
            if (outp.Length < vi.Length) continue;
            var idx = outp.IndexOf(vi, StringComparison.Ordinal);
            while (idx >= 0)
            {
                var okL = idx == 0 || !char.IsLetterOrDigit(outp[idx - 1]); var end = idx + vi.Length; var okR = end >= outp.Length || !char.IsLetterOrDigit(outp[end]);
                if (okL && okR) { outp = outp[..idx] + enf + outp[end..]; idx = outp.IndexOf(vi, idx + enf.Length, StringComparison.Ordinal); }
                else idx = outp.IndexOf(vi, idx + 1, StringComparison.Ordinal);
            }
            if (!VnChar.IsMatch(outp)) break;
        }
        // Chỉ nhận khi dịch trọn vẹn: tránh câu nửa Việt nửa Anh (VD tên do người dùng đặt có chứa cụm trùng từ điển)
        return VnChar.IsMatch(outp) ? s : outp;
    }

    private static int VnCount(string s) => VnChar.Matches(s).Count;

    private static IEnumerable<string> Strategies(string s, int depth)
    {
        var d = depth + 1;
        // Ký hiệu trang trí đầu / cuối: "– Nháp", "Sổ quỹ →", "Xếp hạng:"
        var md = Regex.Match(s, @"^([–·→←•\-]\s+)(.+)$");
        if (md.Success) yield return md.Groups[1].Value + Core(md.Groups[2].Value, d);
        md = Regex.Match(s, @"^(.+?)(\s*[→←:·])$");
        if (md.Success) yield return Core(md.Groups[1].Value, d) + md.Groups[2].Value;

        // Mẫu có tham số (mẫu cụ thể trước)
        var cands = Buckets.TryGetValue(FirstWord(s), out var b) ? b.Concat(Leading) : Leading;
        foreach (var t in cands)
        {
            var m = t.Pattern.Match(s);
            if (!m.Success) continue;
            var vals = new Dictionary<int, string>(); var ok = true;
            for (var g = 1; g < m.Groups.Count && ok; g++)
            {
                var raw = m.Groups[g].Value.Trim(); if (raw.Length == 0) { vals[t.Order[g - 1]] = ""; continue; }
                var tr = Core(raw, d);
                // Tham số còn tiếng Việt: chấp nhận khi là dữ liệu (tên riêng, mã); không nhận khi là chữ thường
                // (cụm từ hệ thống chưa dịch được) hoặc mẫu quá ngắn ("{0} tháng") dễ khớp nhầm cả câu
                if (VnChar.IsMatch(tr) && (char.IsLower(raw[0]) || t.Literal < 6)) ok = false;
                vals[t.Order[g - 1]] = tr;
            }
            if (ok) yield return Regex.Replace(t.En, @"\{(\d+)\}", x => vals.TryGetValue(int.Parse(x.Groups[1].Value), out var v) ? v : x.Value);
        }
        // Nhiều câu: "A. B." → dịch từng câu
        var sentences = Regex.Split(s, @"(?<=[.!?:;])\s+(?=\p{Lu})");
        if (sentences.Length > 1) yield return string.Join(" ", sentences.Select(x => Core(x, d)));
        // Dấu phân cách "·", " – ", " | ", " / "
        foreach (var sep in new[] { " · ", " – ", " | ", " / " })
            if (s.Contains(sep)) yield return string.Join(sep, JoinGreedy(s.Split(sep), sep, d));
        var mp = Regex.Match(s, @"^(.*?)\s*\(([^()]*)\)(.*)$");
        if (mp.Success) { var c = Core(mp.Groups[3].Value.Trim(), d); yield return $"{Core(mp.Groups[1].Value, d)} ({Core(mp.Groups[2].Value, d)}){(c.Length > 0 ? " " + c : "")}".Trim(); }
        var mc = Regex.Match(s, @"^([^:]{2,60}):\s*(.+)$");
        if (mc.Success) yield return Core(mc.Groups[1].Value, d) + ": " + Core(mc.Groups[2].Value, d);
        // "Nhập kho PN2611-0001 – ..." : tên loại + mã chứng từ
        var mk = CodeSplit.Match(s);
        if (mk.Success) { var rm = Regex.Match(mk.Groups[3].Value, @"^(\s*[:–·-]?\s*)(.*)$", RegexOptions.Singleline); yield return Core(mk.Groups[1].Value, d) + " " + mk.Groups[2].Value + rm.Groups[1].Value + Core(rm.Groups[2].Value, d); }
        // "488 Cái": số lượng + đơn vị
        var mn = Regex.Match(s, @"^([\d.,]+)\s+(\S.*)$");
        if (mn.Success) yield return mn.Groups[1].Value + " " + Core(mn.Groups[2].Value, d);
    }

    /// <summary>Tách theo dấu phân cách nhưng ưu tiên cụm dài nhất có trong từ điển ("Tiền gửi không kỳ hạn – ngoại tệ")</summary>
    private static IEnumerable<string> JoinGreedy(string[] parts, string sep, int depth)
    {
        for (var i = 0; i < parts.Length;)
        {
            var taken = 1;
            for (var j = parts.Length; j > i + 1; j--)
                if (Exact.ContainsKey(string.Join(sep, parts[i..j]).Trim())) { taken = j - i; break; }
            yield return taken > 1 ? Exact[string.Join(sep, parts[i..(i + taken)]).Trim()] : Core(parts[i].Trim(), depth);
            i += taken;
        }
    }

    // ------------------------------------------------------------------ Số tiền bằng chữ
    private static readonly Dictionary<string, int> ViDigit = new() { ["không"] = 0, ["một"] = 1, ["mốt"] = 1, ["hai"] = 2, ["ba"] = 3, ["bốn"] = 4, ["tư"] = 4, ["năm"] = 5, ["lăm"] = 5, ["sáu"] = 6, ["bảy"] = 7, ["tám"] = 8, ["chín"] = 9 };
    private static readonly Dictionary<string, long> ViUnit = new() { ["nghìn"] = 1_000, ["triệu"] = 1_000_000, ["tỷ"] = 1_000_000_000 };

    /// <summary>"Chín mươi mốt triệu ba trăm nghìn đồng." (VietnameseNumber.ToWords) → "Ninety-one million three hundred thousand dong."</summary>
    private static string? NumberWords(string s)
    {
        var m = Regex.Match(s, @"^(Âm )?(\p{L}[\p{L} ]*) đồng\.?$");
        if (!m.Success) return null;
        var words = m.Groups[2].Value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long total = 0, group = 0, digit = 0;
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (ViDigit.TryGetValue(w, out var d)) digit = d;
            else if (w == "trăm") { group += digit * 100; digit = 0; }
            else if (w == "mươi") { group += digit * 10; digit = 0; }
            else if (w == "mười") group += 10;
            else if (w == "lẻ") { }
            else if (ViUnit.TryGetValue(w, out var u))
            {
                var mult = u;
                while (i + 1 < words.Length && ViUnit.TryGetValue(words[i + 1], out var u2)) { mult *= u2; i++; }
                total += (group + digit) * mult; group = 0; digit = 0;
            }
            else return null;
        }
        total += group + digit;
        if (m.Groups[2].Value.Trim().Equals("Không", StringComparison.OrdinalIgnoreCase)) total = 0;
        var en = EnWords(total);
        return (m.Groups[1].Success ? "Minus " + en : char.ToUpper(en[0]) + en[1..]) + " dong.";
    }

    private static readonly string[] Ones = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
    private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    private static string EnWords(long n)
    {
        if (n < 20) return Ones[n];
        if (n < 100) return Tens[n / 10] + (n % 10 > 0 ? "-" + Ones[n % 10] : "");
        if (n < 1000) return Ones[n / 100] + " hundred" + (n % 100 > 0 ? " " + EnWords(n % 100) : "");
        foreach (var (v, name) in new[] { (1_000_000_000_000L, "trillion"), (1_000_000_000L, "billion"), (1_000_000L, "million"), (1_000L, "thousand") })
            if (n >= v) return EnWords(n / v) + " " + name + (n % v > 0 ? " " + EnWords(n % v) : "");
        return n.ToString(CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ HTML
    private static readonly Regex Tags = new(@"(<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>|<textarea\b[^>]*>.*?</textarea>|<pre\b[^>]*>.*?</pre>|<!--.*?-->|<[^>]+>)", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Attr = new(@"\b(placeholder|title|aria-label|alt|label|data-confirm|data-confirm-click|data-fav-title)\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ButtonValue = new(@"(<input\b[^>]*\btype\s*=\s*""(?:submit|button|reset)""[^>]*\bvalue\s*=\s*"")([^""]*)("")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JsString = new(@"'((?:[^'\\\n]|\\.)*)'|""((?:[^""\\\n]|\\.)*)""", RegexOptions.Compiled);

    public static string TranslateHtml(string html)
    {
        var sb = new StringBuilder(html.Length + 1024); var last = 0;
        foreach (Match m in Tags.Matches(html))
        {
            if (m.Index > last) sb.Append(TextNode(html[last..m.Index]));
            sb.Append(TagPart(m.Value));
            last = m.Index + m.Length;
        }
        if (last < html.Length) sb.Append(TextNode(html[last..]));
        return sb.ToString();
    }

    private static string TextNode(string raw)
    {
        if (raw.Length == 0 || string.IsNullOrWhiteSpace(raw)) return raw;
        var text = WebUtility.HtmlDecode(raw);
        var tr = Translate(text);
        return tr == text ? raw : WebUtility.HtmlEncode(tr);
    }

    private static string TagPart(string tag)
    {
        if (tag.StartsWith("<script", StringComparison.OrdinalIgnoreCase))
        {
            if (!VnChar.IsMatch(WebUtility.HtmlDecode(tag)) && !tag.Contains("\\u")) return tag;
            return JsString.Replace(tag, m =>
            {
                var single = m.Groups[1].Success; var body = single ? m.Groups[1].Value : m.Groups[2].Value;
                var plain = Regex.Unescape(body);
                if (!VnChar.IsMatch(plain) || plain.Contains('<')) return m.Value;
                var tr = Core(Norm(plain), 0);
                if (tr == Norm(plain)) return m.Value;
                var esc = tr.Replace("\\", "\\\\").Replace(single ? "'" : "\"", single ? "\\'" : "\\\"");
                return single ? $"'{esc}'" : $"\"{esc}\"";
            });
        }
        if (tag.StartsWith("<style", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("<textarea", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("<pre", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("<!--")) return tag;
        if (!tag.Contains('&') && !VnChar.IsMatch(tag)) return tag;
        tag = Attr.Replace(tag, m => { var v = WebUtility.HtmlDecode(m.Groups[2].Value); var tr = Translate(v); return tr == v ? m.Value : $"{m.Groups[1].Value}=\"{WebUtility.HtmlEncode(tr)}\""; });
        tag = ButtonValue.Replace(tag, m => { var v = WebUtility.HtmlDecode(m.Groups[2].Value); var tr = Translate(v); return tr == v ? m.Value : m.Groups[1].Value + WebUtility.HtmlEncode(tr) + m.Groups[3].Value; });
        return tag;
    }
}

/// <summary>Dịch trang HTML trả về khi người dùng chọn tiếng Anh</summary>
public class TranslationMiddleware
{
    private readonly RequestDelegate _next;
    public TranslationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!Lang.IsEn(ctx) || ctx.Request.Path.StartsWithSegments("/api") || HttpMethods.IsHead(ctx.Request.Method)) { await _next(ctx); return; }
        var original = ctx.Response.Body;
        await using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await _next(ctx);
            buffer.Position = 0;
            var type = ctx.Response.ContentType ?? "";
            if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) && ctx.Response.StatusCode == 200)
            {
                var html = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync();
                var bytes = Encoding.UTF8.GetBytes(Translator.TranslateHtml(html));
                ctx.Response.ContentLength = bytes.Length;
                ctx.Response.Body = original;
                await original.WriteAsync(bytes);
                return;
            }
            ctx.Response.Body = original;
            await buffer.CopyToAsync(original);
        }
        finally { ctx.Response.Body = original; }
    }
}
