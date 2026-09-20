using System.Security.Cryptography;
using System.Text.Json;

namespace CoffeeEagle.Offline;

/// <summary>Owns only an app-private audio directory. Catalog replacement is the commit point.</summary>
public sealed class OfflineAudioStore
{
    public const long GiB = 1024L * 1024 * 1024;
    public const long MaxFileBytes = 2 * GiB;
    public const long ReserveBytes = 16 * 1024 * 1024;
    private readonly string _root;
    private readonly long _defaultLimit;
    private readonly Func<long> _freeBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _recovered;
    public OfflineArtworkStore Artwork { get; }
    private string CatalogPath => Path.Combine(_root, "catalog.json");
    private sealed class Catalog
    {
        public int Version { get; set; } = 1;
        public long LimitBytes { get; set; }
        public List<OfflineTrack> Tracks { get; set; } = [];
    }

    public OfflineAudioStore(string root, long defaultLimit, Func<long>? freeBytes = null)
    {
        _root = Path.GetFullPath(root);
        _defaultLimit = defaultLimit;
        _freeBytes = freeBytes ?? (() => new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace);
        Directory.CreateDirectory(_root);
        Artwork = new(Path.Combine(_root, "artwork"));
    }

    private Catalog Load()
    {
        // Corrupt/future catalogs fail closed: never silently reset ownership or evict files.
        var state = File.Exists(CatalogPath)
            ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(CatalogPath)) ?? throw new InvalidDataException("保存一覧が壊れています。")
            : new Catalog { LimitBytes = _defaultLimit };
        if (state.Version != 1 || state.LimitBytes <= 0 || state.Tracks is null || state.Tracks.Count > 10000 ||
            state.Tracks.Select(x => x.Key).Distinct().Count() != state.Tracks.Count)
            throw new InvalidDataException("この保存一覧のバージョンまたは内容に対応していません。");
        foreach (var track in state.Tracks) track.Request.Validate(transferred: true);
        if (!_recovered)
        {
            var retained = state.Tracks.Select(x => x.FileName).ToHashSet(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(_root))
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".partial", StringComparison.Ordinal) ||
                    (IsOwnedAudioName(name) && !retained.Contains(name))) File.Delete(path);
            }
            _recovered = true;
        }
        return state;
    }

    private static bool IsOwnedAudioName(string name) => name.Length > 130 && name[64] == '-' &&
        AudioRequest.IsHash(name[..64]) && AudioRequest.IsHash(name.Substring(65, 64)) &&
        AudioRequest.SupportedExtensions.Contains(name[129..]);

    private void Commit(Catalog state)
    {
        var temp = CatalogPath + ".partial";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, state);
            output.Flush(flushToDisk: true);
        }
        File.Move(temp, CatalogPath, overwrite: true);
    }

    public async Task<OfflineSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = Load();
            return new(state.LimitBytes, state.Tracks.Where(IsPresent).ToArray());
        }
        finally { _gate.Release(); }
    }

    public async Task SetLimitAsync(long bytes, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = Load();
            if (bytes < 64 * 1024 * 1024 || bytes > 1024 * GiB || bytes < state.Tracks.Where(IsPresent).Sum(x => x.Length))
                throw new InvalidOperationException("保存済み容量以上、64 MB以上の上限を指定してください。");
            state.LimitBytes = bytes;
            Commit(state);
        }
        finally { _gate.Release(); }
    }

    public async Task<OfflineTrack> SaveAsync(AudioRequest request, Func<CancellationToken, Task<Stream>> openSource,
        IProgress<AudioProgress>? progress = null, CancellationToken ct = default)
    {
        request.Validate();
        await _gate.WaitAsync(ct);
        var temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".partial");
        string? createdPath = null;
        bool committed = false;
        try
        {
            var state = Load();
            var previous = state.Tracks.SingleOrDefault(x => x.Key == request.Key);
            if (previous is null && state.Tracks.Count >= 10000) throw new IOException("保存件数の上限です。保存済み音声を整理してください。");
            if (previous is not null && previous.Revision == request.Revision &&
                (request.Sha256 is null || request.Sha256 == previous.Sha256) && await VerifyAsync(previous, ct)) return previous;
            var used = state.Tracks.Where(x => x.Key != request.Key && IsPresent(x)).Sum(x => x.Length);
            var allowed = Math.Min(MaxFileBytes, state.LimitBytes - used);
            if (allowed <= 0 || (request.Length > 0 && request.Length > allowed))
                throw new IOException("保存容量の上限を超えます。保存対象または容量上限を変更してください。");
            if (_freeBytes() < (request.Length > 0 ? request.Length : 1) + ReserveBytes)
                throw new IOException("端末の空き容量が不足しています。");
            await using var input = await openSource(ct);
            long count = 0;
            string hash;
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                var buffer = new byte[65536];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    count += read;
                    if (count > allowed || (request.Length > 0 && count > request.Length))
                        throw new InvalidDataException("音声データが予定サイズまたは保存上限を超えました。");
                    if (_freeBytes() < read + ReserveBytes) throw new IOException("端末の空き容量が不足しています。");
                    digest.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report(new(count, request.Length));
                }
                await output.FlushAsync(ct);
                output.Flush(true);
                hash = Convert.ToHexStringLower(digest.GetHashAndReset());
            }
            if (count == 0 || (request.Length > 0 && count != request.Length) || (request.Sha256 is not null && hash != request.Sha256))
                throw new InvalidDataException("音声データが不完全です。もう一度転送してください。");
            ct.ThrowIfCancellationRequested();
            var track = new OfflineTrack(request.Key, request.Title, request.Revision, request.Extension.ToLowerInvariant(), count, hash, DateTimeOffset.UtcNow);
            createdPath = PathFor(track);
            File.Move(temp, createdPath, overwrite: true);
            state.Tracks.RemoveAll(x => x.Key == track.Key);
            state.Tracks.Add(track);
            Commit(state);
            committed = true;
            if (previous is not null && previous.FileName != track.FileName) TryDelete(PathFor(previous));
            return track;
        }
        finally
        {
            TryDelete(temp);
            if (!committed && createdPath is not null)
            {
                // A failed catalog write must leave an existing catalog-referenced file intact.
                try
                {
                    var retained = Load().Tracks.Any(x => PathFor(x) == createdPath);
                    if (!retained) TryDelete(createdPath);
                }
                catch { /* Preserve bytes if ownership cannot be established. */ }
            }
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (!AudioRequest.IsHash(key)) throw new InvalidDataException("音声IDが無効です。");
        await _gate.WaitAsync(ct);
        try
        {
            var state = Load();
            var track = state.Tracks.SingleOrDefault(x => x.Key == key);
            if (track is null) return;
            state.Tracks.Remove(track);
            Commit(state);
            TryDelete(PathFor(track));
        }
        finally { _gate.Release(); }
    }

    public string PathFor(OfflineTrack track)
    {
        track.Request.Validate(transferred: true);
        return Path.Combine(_root, track.FileName);
    }

    private bool IsPresent(OfflineTrack track) => File.Exists(PathFor(track)) && new FileInfo(PathFor(track)).Length == track.Length;
    public async Task<bool> VerifyAsync(OfflineTrack track, CancellationToken ct = default)
    {
        if (!IsPresent(track)) return false;
        await using var stream = File.OpenRead(PathFor(track));
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) == track.Sha256;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
