using System.Text;

namespace ERP.Web.Services;

/// <summary>
/// Bộ tạo mã QR tự viết (không phụ thuộc thư viện ngoài): chế độ byte (UTF-8), mức sửa lỗi L, phiên bản 1–5 (tối đa 106 byte, một khối mã sửa lỗi).
/// Đủ cho mã hàng, số lô, mã vị trí, mã pallet trên tem nhãn.
/// </summary>
public static class QrCode
{
    private static readonly int[] DataCodewords = { 19, 34, 55, 80, 108 };   // phiên bản 1..5, mức L
    private static readonly int[] EcCodewords = { 7, 10, 15, 20, 26 };

    public static int MaxBytes => 106;

    /// <summary>Ma trận mô-đun (true = đen); null nếu nội dung quá dài</summary>
    public static bool[][]? Encode(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var ver = -1;
        for (var v = 0; v < 5; v++) if (4 + 8 + 8 * data.Length <= 8 * DataCodewords[v]) { ver = v; break; }
        if (ver < 0) return null;
        var size = 21 + 4 * ver; var dataLen = DataCodewords[ver]; var ecLen = EcCodewords[ver];

        // Bit dữ liệu: chế độ byte 0100, độ dài 8 bit, dữ liệu, kết thúc, đệm
        var bits = new List<bool>();
        void Put(int val, int len) { for (var i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); }
        Put(4, 4); Put(data.Length, 8); foreach (var b in data) Put(b, 8);
        Put(0, Math.Min(4, dataLen * 8 - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);
        var cw = new List<byte>();
        for (var i = 0; i < bits.Count; i += 8) { var b = 0; for (var j = 0; j < 8; j++) b = (b << 1) | (bits[i + j] ? 1 : 0); cw.Add((byte)b); }
        for (var pad = 0xEC; cw.Count < dataLen; pad ^= 0xEC ^ 0x11) cw.Add((byte)pad);
        cw.AddRange(ReedSolomon(cw.ToArray(), ecLen));

        var allBits = new List<bool>();
        foreach (var b in cw) for (var i = 7; i >= 0; i--) allBits.Add(((b >> i) & 1) != 0);

        // Khung
        var mod = new bool[size][]; var fn = new bool[size][];
        for (var i = 0; i < size; i++) { mod[i] = new bool[size]; fn[i] = new bool[size]; }
        void Set(int x, int y, bool dark) { mod[y][x] = dark; fn[y][x] = true; }
        for (var i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
        void Finder(int cx, int cy)
        {
            for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy; if (x < 0 || x >= size || y < 0 || y >= size) continue;
                    var dist = Math.Max(Math.Abs(dx), Math.Abs(dy)); Set(x, y, dist != 2 && dist != 4);
                }
        }
        Finder(3, 3); Finder(size - 4, 3); Finder(3, size - 4);
        if (ver >= 1)
        {
            var c = size - 7;
            for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++) Set(c + dx, c + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }
        DrawFormat(mod, fn, size, 0); // giữ chỗ vùng thông tin định dạng

        // Đặt dữ liệu theo đường zigzag
        var idx = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j; var upward = ((right + 1) & 2) == 0; var y = upward ? size - 1 - vert : vert;
                    if (!fn[y][x] && idx < allBits.Count) { mod[y][x] = allBits[idx]; idx++; }
                }
        }

        // Chọn mặt nạ có điểm phạt thấp nhất
        var best = -1; var bestScore = int.MaxValue; bool[][]? bestMod = null;
        for (var m = 0; m < 8; m++)
        {
            var t = new bool[size][]; for (var y = 0; y < size; y++) t[y] = (bool[])mod[y].Clone();
            for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) if (!fn[y][x] && Mask(m, x, y)) t[y][x] = !t[y][x];
            DrawFormat(t, fn, size, m);
            var score = Penalty(t, size);
            if (score < bestScore) { bestScore = score; best = m; bestMod = t; }
        }
        return bestMod;
    }

    private static bool Mask(int m, int x, int y) => m switch
    {
        0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0, 4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0, 6 => (x * y % 2 + x * y % 3) % 2 == 0, _ => ((x + y) % 2 + x * y % 3) % 2 == 0
    };

    /// <summary>Thông tin định dạng: mức sửa lỗi L (01) + mặt nạ, mã BCH(15,5), XOR 0x5412</summary>
    public static int FormatBits(int mask)
    {
        var data = (1 << 3) | mask; var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        return ((data << 10) | rem) ^ 0x5412;
    }

    private static void DrawFormat(bool[][] mod, bool[][] fn, int size, int mask)
    {
        var bits = FormatBits(mask);
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        void Set(int x, int y, bool d) { mod[y][x] = d; fn[y][x] = true; }
        for (var i = 0; i <= 5; i++) Set(8, i, Bit(i));
        Set(8, 7, Bit(6)); Set(8, 8, Bit(7)); Set(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
        for (var i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(i));
        Set(8, size - 8, true);
    }

    private static int Penalty(bool[][] m, int n)
    {
        var score = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < n; a++)
            {
                var run = 1;
                for (var b = 1; b < n; b++)
                {
                    var cur = pass == 0 ? m[a][b] : m[b][a]; var prev = pass == 0 ? m[a][b - 1] : m[b - 1][a];
                    if (cur == prev) { run++; if (run == 5) score += 3; else if (run > 5) score++; } else run = 1;
                }
            }
        for (var y = 0; y < n - 1; y++) for (var x = 0; x < n - 1; x++) if (m[y][x] == m[y][x + 1] && m[y][x] == m[y + 1][x] && m[y][x] == m[y + 1][x + 1]) score += 3;
        var dark = m.Sum(r => r.Count(c => c)); var total = n * n;
        score += (int)(Math.Ceiling(Math.Abs(dark * 20.0 - total * 10) / total) - 1) * 10;
        return score;
    }

    // ------------------------------------------------------------------ Reed–Solomon trên GF(256), đa thức 0x11D
    private static int Mul(int x, int y) { var z = 0; for (var i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; } return z; }

    public static byte[] ReedSolomon(byte[] data, int degree)
    {
        var div = new byte[degree]; div[degree - 1] = 1; var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++) { div[j] = (byte)Mul(div[j], root); if (j + 1 < degree) div[j] ^= div[j + 1]; }
            root = Mul(root, 0x02);
        }
        var res = new byte[degree];
        foreach (var b in data)
        {
            var factor = b ^ res[0];
            Array.Copy(res, 1, res, 0, degree - 1); res[degree - 1] = 0;
            for (var i = 0; i < degree; i++) res[i] ^= (byte)Mul(div[i], factor);
        }
        return res;
    }

    public static string Svg(string text, int px = 70)
    {
        var m = Encode(text); if (m == null) return "";
        var n = m.Length; var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-4 -4 {n + 8} {n + 8}\" width=\"{px}\" height=\"{px}\" shape-rendering=\"crispEdges\"><rect x=\"-4\" y=\"-4\" width=\"{n + 8}\" height=\"{n + 8}\" fill=\"#fff\"/><path fill=\"#000\" d=\"");
        for (var y = 0; y < n; y++) for (var x = 0; x < n; x++) if (m[y][x]) sb.Append($"M{x},{y}h1v1h-1z");
        return sb.Append("\"/></svg>").ToString();
    }
}
