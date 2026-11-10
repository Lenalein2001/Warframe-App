using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace WarframeCompanion.Services;

public sealed record RewardLayout(int Cards, double Scale, double Unit);
public sealed record ParsedRiven(string? Weapon, (string Stat, double Value, bool Negative)[] Stats);
public static class OcrReader
{
    public static double Similarity(string a, string b)
    {
        return NativeEngine.Similarity(a, b);
    }
    public static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");
    public static (string? Name, double Score) Match(string text, IEnumerable<string> names)
    {
        static string[] Tokens(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9 ]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = Tokens(text); if (words.Length == 0) return (null, 0);
        var entries = names.Select(name => (Name: name, Tokens: Tokens(name))).Where(n => n.Tokens.Length > 0).ToArray();
        var heads = entries.Select(e => e.Tokens[0]).Distinct().ToArray(); var candidates = new HashSet<string>();
        foreach (var word in words.Distinct()) foreach (var head in heads.Select(h => (Head: h, Score: Similarity(h, word))).Where(h => h.Score >= .7).OrderByDescending(h => h.Score).ThenByDescending(h => h.Head, StringComparer.Ordinal).Take(3)) candidates.Add(head.Head);
        var hits = new List<(string Name, double Score)>();
        foreach (var entry in entries.Where(e => candidates.Contains(e.Tokens[0])))
        {
            var wanted = string.Join(' ', entry.Tokens); var need = entry.Name.Length <= 6 ? .95 : .72; var best = 0.0;
            foreach (var width in new[] { Math.Max(1, entry.Tokens.Length - 1), entry.Tokens.Length, entry.Tokens.Length + 1 }.Distinct())
                for (var start = 0; start < Math.Max(1, words.Length - width + 1); start++) { var value = string.Join(' ', words.Skip(start).Take(width)); if (2.0 * Math.Min(value.Length, wanted.Length) / (value.Length + wanted.Length) < need) continue; best = Math.Max(best, Similarity(value, wanted)); }
            if (best >= need) hits.Add((entry.Name, best));
        }
        var hit = hits.OrderByDescending(h => Math.Round(h.Score, 2)).ThenByDescending(h => h.Name.Length).ThenBy(h => h.Name, StringComparer.Ordinal).FirstOrDefault(); return hit.Name == null ? (null, 0) : hit;
    }
    public static ParsedRiven ParseRiven(string text, IEnumerable<string> weapons)
    {
        var list = weapons.ToArray(); var weapon = list.Where(w => Regex.IsMatch(text, @"(?<!\w)" + Regex.Escape(w) + @"(?!\w)", RegexOptions.IgnoreCase)).OrderByDescending(w => w.Length).FirstOrDefault();
        if (weapon == null)
        {
            var best = text.Split('\n').SelectMany(line => list.Select(w => (Name: w, Score: Similarity(w.ToLowerInvariant(), line.Trim().ToLowerInvariant()[..Math.Min(line.Trim().Length, w.Length + 6)])))).OrderByDescending(x => x.Score).FirstOrDefault();
            if (best.Score >= .82) weapon = best.Name;
        }
        var blob = Regex.Replace(text, @"\s+", " "); var matches = Regex.Matches(blob, @"([+\-−])\s*(\d+(?:[.,]\d+)?)\s*(%|s|m|x)?|(?:^|\s)[xX×]\s?(\d+(?:[.,]\d+)?)");
        var stats = new List<(string, double, bool)>();
        foreach (Match match in matches)
        {
            var after = blob[(match.Index + match.Length)..]; after = after[..Math.Min(70, after.Length)]; var stat = StatAfter(after); if (stat == null) continue;
            var multi = match.Groups[4].Success; var value = double.Parse((multi ? match.Groups[4] : match.Groups[2]).Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (multi) { if (!stat.StartsWith("Damage to ", StringComparison.Ordinal)) continue; value = (value - 1) * 100; } else if (match.Groups[1].Value is "-" or "−") value = -value;
            var negative = stat == "Recoil" ? value > 0 : value < 0;
            if (!stats.Any(s => s.Item1 == stat)) stats.Add((stat, value, negative));
        }
        return new(weapon, stats.Take(4).ToArray());
    }
    public static string? StatAfter(string text)
    {
        var words = Regex.Replace(text.ToLowerInvariant(), "[^a-z ]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries); if (words.Length == 0) return null;
        bool Close(string a, string b) => a == b || a.Length > 3 && b.Length > 3 && a[..4] == b[..4] && Math.Abs(a.Length - b.Length) <= 2;
        var candidates = RivenService.Data.Wording.OrderByDescending(p => p.Key.Split(' ').Length).ToArray();
        foreach (var pair in candidates) { var tokens = pair.Key.Split(' '); if (words.Take(tokens.Length).SequenceEqual(tokens)) return pair.Value; }
        foreach (var pair in candidates)
        {
            var tokens = pair.Key.Split(' '); var i = 0;
            for (var j = 0; j < Math.Min(words.Length, tokens.Length + 5) && i < tokens.Length; j++) { if (!Close(words[j], tokens[i])) continue; if (i == 0 && j > 1) break; i++; }
            if (i == tokens.Length) return pair.Value;
        }
        foreach (var pair in candidates.Where(p => !p.Key.Contains(' ') && p.Key.Length >= 4 && words[0].Length >= p.Key.Length)) if (Similarity(words[0][^pair.Key.Length..], pair.Key) >= .8) return pair.Value;
        if (candidates.Any(p => p.Key.Split(' ').Skip(1).Any(w => Close(words[0], w)))) return null;
        var owners = candidates.Where(p => Close(words[0], p.Key.Split(' ')[0])).Select(p => p.Value).Distinct().ToArray(); return owners.Length == 1 ? owners[0] : null;
    }
    private static int Luma(SKBitmap frame, int x, int y) { var c = frame.GetPixel(x, y); return (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000; }
    private static (double Coverage, int? Row) Bar(SKBitmap frame, double unit, double x0, double x1)
    {
        var inset = (x1 - x0) * .1; var xa = Math.Max(0, (int)(x0 + inset)); var xb = Math.Min(frame.Width, (int)(x1 - inset)); if (xb <= xa) return (0, null);
        var edge = Math.Max(2, (int)Math.Round(3.2 * unit)); var centre = frame.Height / 2.0 - 62.5 * unit; (double Coverage, int? Row) best = (0, null);
        foreach (var y in Enumerable.Range(-8, 17).Select(s => (int)Math.Round(centre + s * .5 * unit)).Distinct().Where(y => y >= edge && y + edge < frame.Height))
        { var lit = Enumerable.Range(xa, xb - xa).Count(x => Luma(frame, x, y) - Math.Max(Luma(frame, x, y - edge), Luma(frame, x, y + edge)) >= 25); var share = lit / (double)(xb - xa); if (share > best.Coverage) best = (share, y); }
        return best;
    }
    private static bool Edges(SKBitmap frame, int y, double left, double width, int cards)
    {
        var reach = Math.Max(4, width * .05); var edges = new List<bool?>();
        int[] Row(double a, double b) => Enumerable.Range(Math.Max(0, (int)a), Math.Max(0, Math.Min(frame.Width, (int)b) - Math.Max(0, (int)a))).Select(x => Luma(frame, x, y)).ToArray();
        for (var j = 0; j <= cards; j++)
        {
            var edge = left + j * width; var beside = (j > 0 ? Row(edge - 3 * reach, edge - reach) : []).Concat(j < cards ? Row(edge + reach, edge + 3 * reach) : []).Order().ToArray();
            if (beside.Length == 0) { edges.Add(null); continue; } var level = beside[beside.Length / 2];
            if (level < 50 || beside.Count(l => l * 4 >= level * 3) * 5 < beside.Length * 4) { edges.Add(null); continue; }
            edges.Add(Row(edge - reach, edge + reach).Count(l => l * 20 < level * 13) >= 2);
        }
        var judged = edges.OfType<bool>().ToArray(); var inner = edges.Skip(1).Take(cards - 1).OfType<bool>().ToArray();
        return judged.Length >= 2 && judged.Count(b => b) * 5 >= judged.Length * 3 && (inner.Length == 0 || inner.Contains(true));
    }
    public static RewardLayout? Layout(SKBitmap frame, double? scale)
    {
        foreach (var s in scale is { } known ? new[] { known } : [1, .95, 1.05, .9, 1.1, .85, 1.15, .8, 1.2, .75, 1.25, .7, .65, .6, .55, .5])
        {
            var unit = Math.Min(frame.Width / 1920.0, frame.Height / 1080.0) * s; var c = 242 * unit; var mid = frame.Width / 2.0;
            bool Seen(double from) => Bar(frame, unit, mid + from * c, mid + (from + 1) * c).Coverage >= .6;
            var even = new[] { -2.0, -1, 0, 1 }.Select(Seen).ToArray(); var odd = new[] { -1.5, -.5, .5 }.Select(Seen).ToArray();
            var cards = even[0] || even[3] ? 4 : odd[0] || odd[2] ? 3 : even[1] || even[2] ? 2 : odd[1] ? 1 : 0; if (cards == 0) continue;
            var left = mid - cards * c / 2; if (Bar(frame, unit, left, left + cards * c).Row is { } y && Edges(frame, y, left, c, cards)) return new(cards, s, unit);
        }
        return null;
    }
}
