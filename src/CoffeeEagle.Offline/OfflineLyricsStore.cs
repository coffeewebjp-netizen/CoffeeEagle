namespace CoffeeEagle.Offline;

/// <summary>Optional sidecars; independent of audio revisions and the v1 audio catalog.</summary>
public sealed class OfflineLyricsStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public OfflineLyricsStore(string root)
    {
        _root = Path.GetFullPath(root); Directory.CreateDirectory(_root);
        foreach (var path in Directory.EnumerateFiles(_root, "*.partial"))
        {
            var name = Path.GetFileName(path);
            if (name.Length == 141 && AudioRequest.IsHash(name[..64]) && name[64] == '-' &&
                AudioRequest.IsHash(name.Substring(65, 64)) && name[129..] == ".lrc.partial") File.Delete(path);
        }
    }
    public string PathFor(string key, string revision)
    {
        if (!AudioRequest.IsHash(key) || !AudioRequest.IsHash(revision)) throw new InvalidDataException("歌詞の保存先が無効です。");
        return Path.Combine(_root, $"{key}-{revision}.lrc");
    }
    public async Task<byte[]?> ReadAsync(string key, string revision, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = PathFor(key, revision);
            if (!File.Exists(path)) return null;
            await using var input = File.OpenRead(path);
            return await LrcLyrics.ReadAsync(input, ct);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(string key, string revision, byte[]? bytes, CancellationToken ct = default)
    {
        var path = PathFor(key, revision);
        if (bytes is not null) LrcLyrics.Parse(bytes);
        await _gate.WaitAsync(ct);
        var temp = path + ".partial";
        try
        {
            ct.ThrowIfCancellationRequested();
            if (bytes is null) File.Delete(path);
            else
            {
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                { await output.WriteAsync(bytes, ct); output.Flush(flushToDisk: true); }
                ct.ThrowIfCancellationRequested();
                File.Move(temp, path, overwrite: true);
            }
            foreach (var old in OwnedPaths(key).Where(x => x != path)) File.Delete(old);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); _gate.Release(); }
    }
    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (!AudioRequest.IsHash(key)) throw new InvalidDataException("歌詞IDが無効です。");
        await _gate.WaitAsync(ct);
        try { foreach (var path in OwnedPaths(key)) File.Delete(path); }
        finally { _gate.Release(); }
    }
    private IEnumerable<string> OwnedPaths(string key) => Directory.EnumerateFiles(_root, key + "-*.lrc")
        .Where(path => Path.GetFileName(path).Length == 133 && AudioRequest.IsHash(Path.GetFileName(path).Substring(65, 64)));
}
