namespace CoffeeEagle.Offline;

/// <summary>Small disposable artwork cache; audio/catalog ownership stays separate.</summary>
public sealed class OfflineArtworkStore
{
    public const int MaxImageBytes = 16 * 1024;
    public const long DefaultLimit = 16 * 1024 * 1024;
    private readonly string _root;
    private readonly long _limit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public OfflineArtworkStore(string root, long limit = DefaultLimit)
    {
        if (limit < MaxImageBytes) throw new ArgumentOutOfRangeException(nameof(limit));
        _root = Path.GetFullPath(root); _limit = limit;
        Directory.CreateDirectory(_root);
        foreach (var partial in Directory.EnumerateFiles(_root, "*.jpg.partial").Where(x => Owned(Path.GetFileName(x)[..^8]))) File.Delete(partial);
    }
    public static void Validate(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes.Length > MaxImageBytes || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[2] != 0xff)
            throw new InvalidDataException("サムネイルの形式またはサイズが無効です。");
    }
    public string PathFor(string key, string revision)
    {
        if (!AudioRequest.IsHash(key) || !AudioRequest.IsHash(revision)) throw new InvalidDataException("サムネイルIDが無効です。");
        return Path.Combine(_root, key + "-" + revision + ".jpg");
    }
    private static bool Owned(string name) => name.Length == 133 && name[64] == '-' && name.EndsWith(".jpg", StringComparison.Ordinal) &&
        AudioRequest.IsHash(name[..64]) && AudioRequest.IsHash(name.Substring(65, 64));

    public async Task<byte[]?> ReadAsync(string key, string revision, CancellationToken ct = default)
    {
        var path = PathFor(key, revision);
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxImageBytes) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            try { Validate(bytes); return bytes; } catch (InvalidDataException) { return null; }
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(string key, string revision, byte[] bytes, CancellationToken ct = default)
    {
        Validate(bytes); var path = PathFor(key, revision);
        await _gate.WaitAsync(ct);
        var temp = path + ".partial";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
            foreach (var old in Directory.EnumerateFiles(_root).Where(x => Owned(Path.GetFileName(x)) && Path.GetFileName(x).StartsWith(key + "-", StringComparison.Ordinal) && x != path)) File.Delete(old);
            var files = Directory.EnumerateFiles(_root).Where(x => Owned(Path.GetFileName(x))).Select(x => new FileInfo(x)).OrderBy(x => x.LastWriteTimeUtc).ToList();
            var used = files.Sum(x => x.Length);
            foreach (var file in files.Where(x => x.FullName != path)) { if (used <= _limit) break; used -= file.Length; file.Delete(); }
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } finally { _gate.Release(); } }
    }
}
