using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoffeeEagle.Reader.Services;

/// <summary>Atomic, revision-keyed persistent files for ordinary phone browsing.</summary>
public sealed class PhoneFileCacheStore
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sourceLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lyricsManifestLock = new(1, 1);
    private readonly Func<long>? _availableBytes;
    private readonly long _reserveBytes;
    private readonly string _lyricsManifestPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public PhoneFileCacheStore(string rootDirectory, Func<long>? availableBytes = null, long reserveBytes = 16L * 1024 * 1024)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _lyricsManifestPath = Path.Combine(RootDirectory, "lyrics-index-v1.json");
        _availableBytes = availableBytes;
        _reserveBytes = Math.Max(0, reserveBytes);
    }

    public string RootDirectory { get; }

    public bool TryGetPath(string cacheIdentity, string revision, string? extension, out string path) =>
        TryGetPath(cacheIdentity, revision, extension, expectedLength: null, out path);

    public bool TryGetPath(string cacheIdentity, string revision, string? extension, long? expectedLength, out string path)
    {
        path = PathFor(cacheIdentity, revision, extension);
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        return length > 0 && (expectedLength is null || length == expectedLength.Value);
    }

    public async Task<PhoneFileCacheWriteResult> SaveIfMissingAsync(
        string cacheIdentity,
        string revision,
        string? extension,
        Func<CancellationToken, Task<Stream>> openSource,
        long maxBytes = long.MaxValue,
        long? expectedLength = null,
        bool requireNonEmpty = false,
        bool preserveOlderRevisions = false,
        Func<string, CancellationToken, Task>? validateTemporary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentNullException.ThrowIfNull(openSource);
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var sourceKey = Hash(cacheIdentity);
        var gate = _sourceLocks.GetOrAdd(sourceKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(cacheIdentity, revision, extension);
            if (File.Exists(path)
                && (!requireNonEmpty || new FileInfo(path).Length > 0)
                && (expectedLength is null || new FileInfo(path).Length == expectedLength.Value))
            {
                try
                {
                    if (validateTemporary is null) return new PhoneFileCacheWriteResult(path, Downloaded: false, new FileInfo(path).Length);
                    await validateTemporary(path, cancellationToken);
                    return new PhoneFileCacheWriteResult(path, Downloaded: false, new FileInfo(path).Length);
                }
                catch (InvalidDataException)
                {
                    // A damaged current revision is replaced atomically after a new verified read.
                }
            }

            Directory.CreateDirectory(RootDirectory);
            EnsureAvailableSpace(expectedLength);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using var input = await openSource(cancellationToken);
                long length = 0;
                var output = CreateTemporary(temporaryPath);
                try
                {
                    var buffer = new byte[64 * 1024];
                    var bytesSinceSpaceCheck = 0L;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        if (length + read > maxBytes)
                        {
                            throw new InvalidDataException($"ファイルが上限 {maxBytes:N0} bytes を超えています。");
                        }

                        try
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        }
                        catch (IOException ex)
                        {
                            throw new PhoneFileCacheStorageException("端末キャッシュへ書き込めませんでした。空き容量を確認してください。", ex);
                        }

                        length += read;
                        bytesSinceSpaceCheck += read;
                        if (bytesSinceSpaceCheck >= 1024 * 1024)
                        {
                            EnsureAvailableSpace(null);
                            bytesSinceSpaceCheck = 0;
                        }
                    }

                    try
                    {
                        await output.FlushAsync(cancellationToken);
                        output.Flush(flushToDisk: true);
                    }
                    catch (IOException ex)
                    {
                        throw new PhoneFileCacheStorageException("端末キャッシュを保存できませんでした。空き容量を確認してください。", ex);
                    }
                }
                finally
                {
                    try { await output.DisposeAsync(); }
                    catch (IOException ex) { throw new PhoneFileCacheStorageException("端末キャッシュを閉じられませんでした。空き容量を確認してください。", ex); }
                }

                if (expectedLength is not null && length != expectedLength.Value)
                    throw new InvalidDataException($"取得した長さ {length:N0} bytes が索引の長さ {expectedLength.Value:N0} bytes と一致しません。");
                if (requireNonEmpty && length == 0)
                    throw new InvalidDataException("空のメディアファイルはキャッシュできません。");
                if (validateTemporary is not null) await validateTemporary(temporaryPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporaryPath, path, overwrite: true);
                }
                catch (IOException ex)
                {
                    throw new PhoneFileCacheStorageException("端末キャッシュを保存できませんでした。空き容量を確認してください。", ex);
                }

                if (!preserveOlderRevisions) RemoveOlderRevisions(sourceKey, path);
                return new PhoneFileCacheWriteResult(path, Downloaded: true, length);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetLyricsManifestAsync(string audioUri, PhoneLyricsCacheState state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioUri);
        ArgumentNullException.ThrowIfNull(state);
        await _lyricsManifestLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var entries = await ReadLyricsManifestAsync(cancellationToken);
            entries[Hash(audioUri)] = state;
            var temporaryPath = _lyricsManifestPath + ".tmp";
            try
            {
                await using (var stream = CreateTemporary(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(stream, entries, _jsonOptions, cancellationToken);
                }

                File.Move(temporaryPath, _lyricsManifestPath, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _lyricsManifestLock.Release();
        }
    }

    public PhoneLyricsCacheState? GetLyricsManifest(string audioUri)
    {
        if (string.IsNullOrWhiteSpace(audioUri) || !File.Exists(_lyricsManifestPath)) return null;
        _lyricsManifestLock.Wait();
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, PhoneLyricsCacheState>>(
                File.ReadAllText(_lyricsManifestPath), _jsonOptions);
            return entries is not null && entries.TryGetValue(Hash(audioUri), out var state) ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _lyricsManifestLock.Release();
        }
    }

    public bool TryGetLyricsPath(string audioUri, out string path)
    {
        var state = GetLyricsManifest(audioUri);
        if (state is not null
            && string.Equals(state.Status, PhoneLyricsCacheState.Available, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(state.SourceUri)
            && !string.IsNullOrWhiteSpace(state.Revision))
        {
            return TryGetPath(LyricsIdentity(audioUri), state.Revision, state.Extension, state.SizeBytes, out path);
        }

        path = string.Empty;
        return false;
    }

    public async Task RemoveLyricsAsync(string audioUri, string? audioRevision = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioUri)) return;
        var identity = LyricsIdentity(audioUri);
        var sourceKey = Hash(identity);
        var gate = _sourceLocks.GetOrAdd(sourceKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await SetLyricsManifestAsync(audioUri, new PhoneLyricsCacheState(PhoneLyricsCacheState.Missing, AudioRevision: audioRevision), cancellationToken);
            foreach (var path in EnumerateRevisions(sourceKey)) TryDelete(path);
        }
        finally
        {
            gate.Release();
        }
    }

    public long GetSizeBytes()
    {
        if (!Directory.Exists(RootDirectory)) return 0;
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(RootDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(file, _lyricsManifestPath, StringComparison.OrdinalIgnoreCase)) continue;
            try { size += new FileInfo(file).Length; } catch (IOException) { }
        }

        return size;
    }

    private void EnsureAvailableSpace(long? expectedLength)
    {
        if (_availableBytes is null) return;
        var available = _availableBytes();
        if (available < _reserveBytes || (expectedLength is > 0 && expectedLength.Value > available - _reserveBytes))
        {
            throw new PhoneFileCacheStorageException("端末の空き容量が不足しています。16 MiB以上の空きを確保してから再試行してください。", new IOException("Insufficient free space."));
        }
    }

    public static string LyricsIdentity(string audioUri) => "lyrics:" + audioUri;

    public async Task RemoveOlderRevisionsAsync(string cacheIdentity, string currentRevision, string? extension, CancellationToken cancellationToken = default)
    {
        var sourceKey = Hash(cacheIdentity);
        var gate = _sourceLocks.GetOrAdd(sourceKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { RemoveOlderRevisions(sourceKey, PathFor(cacheIdentity, currentRevision, extension)); }
        finally { gate.Release(); }
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string PathFor(string cacheIdentity, string revision, string? extension)
    {
        var sourceKey = Hash(cacheIdentity);
        var revisionKey = Hash(revision)[..24];
        return Path.Combine(RootDirectory, $"{sourceKey}-{revisionKey}{NormalizeExtension(extension)}");
    }

    private async Task<Dictionary<string, PhoneLyricsCacheState>> ReadLyricsManifestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_lyricsManifestPath)) return new(StringComparer.Ordinal);
        await using var stream = new FileStream(_lyricsManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, PhoneLyricsCacheState>>(stream, _jsonOptions, cancellationToken)
            ?? new Dictionary<string, PhoneLyricsCacheState>(StringComparer.Ordinal);
    }

    private IEnumerable<string> EnumerateRevisions(string sourceKey)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        return Directory.EnumerateFiles(RootDirectory, sourceKey + "-*", SearchOption.TopDirectoryOnly).ToArray();
    }

    private void RemoveOlderRevisions(string sourceKey, string currentPath)
    {
        foreach (var path in EnumerateRevisions(sourceKey))
        {
            if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) TryDelete(path);
        }
    }

    private static string NormalizeExtension(string? value)
    {
        var extension = Path.GetExtension(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16
            || extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.')) return ".bin";
        return extension.ToLowerInvariant();
    }

    private static FileStream CreateTemporary(string path)
    {
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        }
        catch (IOException ex)
        {
            throw new PhoneFileCacheStorageException("端末キャッシュを作成できませんでした。空き容量を確認してください。", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}

public sealed record PhoneFileCacheWriteResult(string Path, bool Downloaded, long Length);

public sealed class PhoneFileCacheStorageException(string message, Exception innerException) : IOException(message, innerException);

public sealed record PhoneLyricsCacheState(string Status, string? SourceUri = null, string? Revision = null, string? Extension = null, long? SizeBytes = null, string? AudioRevision = null)
{
    public const string Available = "available";
    public const string Missing = "missing";
    public const string Pending = "pending";
}
