using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CoffeeEagle.Offline;

public sealed record LyricLine(long TimeMs, string Text);

/// <summary>Bounded, line-synchronised LRC. Positive offset advances the lyric clock.</summary>
public sealed class LrcLyrics
{
    public const int MaxBytes = 64 * 1024;
    private const int MaxLines = 4000;
    private static readonly Regex Stamp = new(@"\G\[(\d{1,4}):([0-5]\d)(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant);
    private static readonly Regex Metadata = new(@"^\[(?:ar|al|ti|au|by|re|ve|length|offset):.*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public IReadOnlyList<LyricLine> Lines { get; }
    public string PlainText { get; }
    public bool IsTimed => Lines.Count > 0;
    private LrcLyrics(IReadOnlyList<LyricLine> lines, string plainText) { Lines = lines; PlainText = plainText; }

    public static bool Matches(string audioName, string candidateName) =>
        string.Equals(Path.GetExtension(candidateName), ".lrc", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileNameWithoutExtension(audioName), Path.GetFileNameWithoutExtension(candidateName), StringComparison.Ordinal);

    public static T? FindSibling<T>(string audioName, IEnumerable<T> siblings, Func<T, string> name) where T : class
    {
        var matches = siblings.Where(x => Matches(audioName, name(x))).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("同じ名前の歌詞が複数あります。1つに整理してください。");
        return matches.SingleOrDefault();
    }

    public static async Task<byte[]> ReadAsync(Stream source, CancellationToken ct = default)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > MaxBytes) throw new InvalidDataException("歌詞ファイルは64 KB以内にしてください。");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        Parse(bytes);
        return bytes;
    }

    public static LrcLyrics Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("歌詞ファイルは64 KB以内にしてください。");
        string text;
        try
        {
            text = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ? new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2)
                : bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2)
                : Utf8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("歌詞をUTF-8またはBOM付きUTF-16で保存してください。", ex); }
        if (text.Contains('\0')) throw new InvalidDataException("歌詞に読み取れない文字が含まれています。");
        var timed = new List<LyricLine>(); var plain = new List<string>(); long offset = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase) && line.EndsWith(']') &&
                long.TryParse(line[8..^1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            { offset = Math.Clamp(value, -86400000L, 86400000L); continue; }
            var stamps = new List<long>(); var end = 0;
            for (var match = Stamp.Match(line); match.Success; match = Stamp.Match(line, end))
            {
                var fraction = match.Groups[3].Value.PadRight(3, '0');
                stamps.Add(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60000 +
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 1000 + int.Parse(fraction, CultureInfo.InvariantCulture));
                end = match.Index + match.Length;
            }
            if (stamps.Count > 0)
            {
                var words = line[end..].Trim();
                foreach (var time in stamps) timed.Add(new(time, words));
                if (timed.Count > MaxLines) throw new InvalidDataException("歌詞の時刻行が多すぎます。");
            }
            else if (line.Length > 0 && !Metadata.IsMatch(line) && !line.StartsWith('[')) plain.Add(line);
        }
        // Preserve blank timed lines; equal timestamps (e.g. translations) are displayed together.
        var lines = timed.GroupBy(x => x.TimeMs).OrderBy(x => x.Key)
            .Select(x => new LyricLine(x.Key - offset, string.Join('\n', x.Select(y => y.Text)))).ToArray();
        return new(lines, string.Join('\n', plain));
    }

    public int IndexAt(long positionMs)
    {
        var low = 0; var high = Lines.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (Lines[mid].TimeMs <= positionMs) low = mid + 1; else high = mid;
        }
        return low - 1;
    }

    public (string Previous, string Current, string Next) WindowAt(long positionMs)
    {
        if (!IsTimed) return ("", PlainText, "");
        var index = IndexAt(positionMs);
        return (index > 0 ? Lines[index - 1].Text : "", index >= 0 ? Lines[index].Text : "", index + 1 < Lines.Count ? Lines[index + 1].Text : "");
    }
}
