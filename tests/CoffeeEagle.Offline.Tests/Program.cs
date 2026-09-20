using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CoffeeEagle.Offline;

var suite = new Suite();
await suite.RunAsync();
sealed class Suite
{
    private int _count;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CoffeeEagleOfflineTests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _audio = Enumerable.Range(0, 180000).Select(x => (byte)(x % 251)).ToArray();
    private AudioRequest Request(string key = "asset", string revision = "one", long? length = null, string? hash = null) =>
        new(AudioRequest.KeyFor("library", key), "音声テスト", AudioRequest.Hash(revision), ".mp3", length ?? _audio.Length, hash);
    private OfflineAudioStore Store(string folder, long limit = OfflineAudioStore.GiB, Func<long>? free = null) =>
        new(Path.Combine(_root, folder), limit, free ?? (() => 100 * OfflineAudioStore.GiB));
    private Task<Stream> Source(CancellationToken _) => Task.FromResult<Stream>(new MemoryStream(_audio));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private async Task Test(string name, Func<Task> test) { await test(); Console.WriteLine("PASS " + name); _count++; }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_root);
        try
        {
            await Test("persistent save survives restart and verifies actual bytes", async () =>
            {
                var store = Store("restart"); var track = await store.SaveAsync(Request(), Source);
                var restarted = Store("restart"); var state = await restarted.SnapshotAsync();
                Check(state.Tracks.Count == 1 && state.UsedBytes == _audio.Length, "snapshot");
                Check(await restarted.VerifyAsync(track), "hash");
            });
            await Test("unchanged revision does not reopen or download source", async () =>
            {
                var store = Store("skip"); await store.SaveAsync(Request(), Source);
                await store.SaveAsync(Request(), _ => throw new Exception("must not download"));
            });
            await Test("revision replacement commits new bytes and removes old copy", async () =>
            {
                var store = Store("update"); var old = await store.SaveAsync(Request(), Source);
                var bytes = Encoding.UTF8.GetBytes("new revision");
                var next = await store.SaveAsync(Request(revision: "two", length: bytes.Length), _ => Task.FromResult<Stream>(new MemoryStream(bytes)));
                Check(!File.Exists(store.PathFor(old)) && await store.VerifyAsync(next), "replacement");
                Check((await store.SnapshotAsync()).Tracks.Single().Revision == AudioRequest.Hash("two"), "catalog");
            });
            await Test("interrupted replacement preserves last verified version", async () =>
            {
                var store = Store("interrupted"); var old = await store.SaveAsync(Request(), Source);
                await Reject<IOException>(() => store.SaveAsync(Request(revision: "two"), _ => Task.FromResult<Stream>(new BrokenStream(_audio))));
                Check(await store.VerifyAsync(old) && (await store.SnapshotAsync()).Tracks.Single() == old, "preserve old");
                Check(!Directory.EnumerateFiles(Path.Combine(_root, "interrupted"), "*.partial").Any(), "partial cleanup");
            });
            await Test("wrong transferred hash and truncated payload never commit", async () =>
            {
                var store = Store("invalid-payload");
                await Reject<InvalidDataException>(() => store.SaveAsync(Request(hash: new string('a', 64)), Source));
                await Reject<InvalidDataException>(() => store.SaveAsync(Request(length: _audio.Length + 1), Source));
                Check((await store.SnapshotAsync()).Tracks.Count == 0, "no invalid track");
            });
            await Test("known quota rejection happens before reading source", async () =>
            {
                var store = Store("quota", _audio.Length - 1);
                await Reject<IOException>(() => store.SaveAsync(Request(), _ => throw new Exception("must not open")));
            });
            await Test("unknown-length download is bounded by quota", async () =>
            {
                var store = Store("unknown-quota", 50000);
                await Reject<InvalidDataException>(() => store.SaveAsync(Request(length: -1), Source));
                Check((await store.SnapshotAsync()).Tracks.Count == 0, "bounded");
            });
            await Test("low physical free space prevents staging", async () =>
            {
                var store = Store("space", free: () => OfflineAudioStore.ReserveBytes);
                await Reject<IOException>(() => store.SaveAsync(Request(), _ => throw new Exception("must not open")));
            });
            await Test("cancellation leaves catalog and source untouched", async () =>
            {
                using var cancel = new CancellationTokenSource();
                var store = Store("cancel");
                await Reject<OperationCanceledException>(() => store.SaveAsync(Request(), _ => { cancel.Cancel(); return Source(default); }, ct: cancel.Token));
                Check((await store.SnapshotAsync()).Tracks.Count == 0, "no cancellation commit");
            });
            await Test("same revision with damaged stored file is repaired", async () =>
            {
                var store = Store("repair"); var track = await store.SaveAsync(Request(), Source);
                await File.WriteAllBytesAsync(store.PathFor(track), new byte[_audio.Length]);
                Check(!await store.VerifyAsync(track), "damage detected");
                var repaired = await store.SaveAsync(Request(), Source); Check(await store.VerifyAsync(repaired), "repaired");
            });
            await Test("removal affects only app-owned target file", async () =>
            {
                var sentinel = Path.Combine(_root, "original.mp3"); await File.WriteAllBytesAsync(sentinel, _audio);
                var store = Store("remove"); var track = await store.SaveAsync(Request(), Source);
                await store.RemoveAsync(track.Key);
                Check(File.Exists(sentinel) && !File.Exists(store.PathFor(track)) && (await store.SnapshotAsync()).Tracks.Count == 0, "scope");
                await Reject<InvalidDataException>(() => store.RemoveAsync("../original.mp3"));
            });
            await Test("future and corrupt catalogs fail closed without deleting bytes", async () =>
            {
                var store = Store("catalog"); var track = await store.SaveAsync(Request(), Source);
                var catalog = Path.Combine(_root, "catalog", "catalog.json");
                await File.WriteAllTextAsync(catalog, "{\"Version\":2,\"LimitBytes\":1000000000,\"Tracks\":[]}");
                await Reject<InvalidDataException>(() => Store("catalog").SnapshotAsync());
                Check(File.Exists(store.PathFor(track)), "future data retained");
                await File.WriteAllTextAsync(catalog, "{bad");
                await Reject<System.Text.Json.JsonException>(() => Store("catalog").SnapshotAsync());
                Check(File.Exists(store.PathFor(track)), "corrupt data retained");
            });
            await Test("startup clears interrupted staging only in owned folder", async () =>
            {
                var store = Store("recovery"); var track = await store.SaveAsync(Request(), Source);
                await File.WriteAllTextAsync(Path.Combine(_root, "recovery", "unfinished.partial"), "partial");
                var restarted = Store("recovery"); await restarted.SnapshotAsync();
                Check(await restarted.VerifyAsync(track) && !File.Exists(Path.Combine(_root, "recovery", "unfinished.partial")), "recovered");
            });
            await Test("parallel saves serialize without lost catalog entries", async () =>
            {
                var store = Store("parallel");
                await Task.WhenAll(Enumerable.Range(0, 8).Select(x => store.SaveAsync(Request(key: x.ToString()), Source)));
                Check((await store.SnapshotAsync()).Tracks.Count == 8, "all saved");
            });
            await Test("storage limit is persistent and cannot silently evict pinned files", async () =>
            {
                var store = Store("limit"); await store.SaveAsync(Request(), Source);
                await store.SetLimitAsync(128 * 1024 * 1024);
                Check((await Store("limit").SnapshotAsync()).LimitBytes == 128 * 1024 * 1024, "persisted limit");
                await Reject<InvalidOperationException>(() => store.SetLimitAsync(1));
                Check((await store.SnapshotAsync()).Tracks.Count == 1, "pinned");
            });
            await Test("wire header roundtrip handles fragmented reads and Japanese titles", async () =>
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(_audio));
                var envelope = new AudioEnvelope(1, Guid.NewGuid().ToString("N"), Request(hash: hash));
                using var stream = new MemoryStream(); await AudioWire.WriteHeaderAsync(stream, envelope, default);
                using var fragmented = new FragmentedStream(stream.ToArray());
                Check(await AudioWire.ReadHeaderAsync(fragmented, default) == envelope, "roundtrip");
            });
            await Test("oversized or truncated wire headers are rejected before content", async () =>
            {
                var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, int.MaxValue);
                await Reject<InvalidDataException>(() => AudioWire.ReadHeaderAsync(new MemoryStream(length), default));
                BinaryPrimitives.WriteInt32BigEndian(length, 20);
                await Reject<EndOfStreamException>(() => AudioWire.ReadHeaderAsync(new MemoryStream(length), default));
            });
            await Test("unsafe names and unsupported protocol cannot enter storage", async () =>
            {
                var store = Store("unsafe");
                await Reject<InvalidDataException>(() => store.SaveAsync(Request() with { Extension = "/../escape.mp3" }, Source));
                await Reject<InvalidDataException>(() => store.SaveAsync(Request() with { Key = "../escape" }, Source));
                await Reject<InvalidDataException>(() => AudioWire.WriteHeaderAsync(new MemoryStream(), new(2, Guid.NewGuid().ToString("N"), Request(hash: new string('a', 64))), default));
            });
            Console.WriteLine($"{_count}/{_count} checks passed.");
        }
        finally { Directory.Delete(_root, recursive: true); }
    }
    private sealed class BrokenStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { if (Position > 0) throw new IOException("simulated disconnect"); return base.ReadAsync(buffer[..Math.Min(32768, buffer.Length)], ct); }
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], ct);
    }
}
