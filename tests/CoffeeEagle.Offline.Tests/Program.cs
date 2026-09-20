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
                await Reject<InvalidDataException>(() => AudioWire.WriteHeaderAsync(new MemoryStream(), new(99, Guid.NewGuid().ToString("N"), Request(hash: new string('a', 64))), default));
            });
            await Test("Watch targets persist independently of folders and audio copies", async () =>
            {
                var path = Path.Combine(_root, "targets.json"); var targets = new WatchTargetStore(path);
                var first = AudioRequest.KeyFor("library-a", "same-id"); var second = AudioRequest.KeyFor("library-b", "same-id");
                await targets.SetAsync([first, second], true);
                Check((await new WatchTargetStore(path).ReadAsync()).SetEquals([first, second]), "restart and library isolation");
                var store = Store("target-audio"); var track = await store.SaveAsync(Request(), Source);
                await targets.SetAsync([first, track.Key], true); await targets.SetAsync([first, track.Key], false);
                Check((await targets.ReadAsync()).SetEquals([second]) && await store.VerifyAsync(track), "OFF only changes flags");
                await Task.WhenAll(Enumerable.Range(0, 8).Select(x => targets.SetAsync([AudioRequest.Hash(x.ToString())], true)));
                Check((await targets.ReadAsync()).Count == 9, "parallel choices retained");
            });
            await Test("invalid target state fails closed", async () =>
            {
                var path = Path.Combine(_root, "bad-targets.json"); var targets = new WatchTargetStore(path);
                await Reject<InvalidDataException>(() => targets.SetAsync(["../escape"], true));
                await File.WriteAllTextAsync(path, "{\"Version\":2,\"Keys\":[]}");
                await Reject<InvalidDataException>(() => targets.SetAsync([AudioRequest.Hash("test")], true));
                Check((await File.ReadAllTextAsync(path)).Contains("\"Version\":2"), "future state preserved");
                await File.WriteAllTextAsync(path, "{broken");
                await Reject<System.Text.Json.JsonException>(() => targets.ReadAsync());
            });
            await Test("v2 artwork roundtrips while v1 rejects artwork", async () =>
            {
                var art = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
                var envelope = new AudioEnvelope(2, Guid.NewGuid().ToString("N"), Request(hash: new string('a', 64)), art);
                using var stream = new MemoryStream(); await AudioWire.WriteHeaderAsync(stream, envelope, default);
                var read = await AudioWire.ReadHeaderAsync(new FragmentedStream(stream.ToArray()), default);
                Check(read.Version == 2 && read.Audio == envelope.Audio && read.Artwork!.SequenceEqual(art), "v2 fragmented header");
                await Reject<InvalidDataException>(() => AudioWire.WriteHeaderAsync(new MemoryStream(), envelope with { Version = 1 }, default));
                await Reject<InvalidDataException>(() => AudioWire.WriteHeaderAsync(new MemoryStream(), envelope with { Artwork = new byte[17000] }, default));
            });
            await Test("artwork eviction and revision updates preserve audio and foreign files", async () =>
            {
                var store = Store("art-audio"); var track = await store.SaveAsync(Request(), Source);
                var folder = Path.Combine(_root, "art-audio", "artwork"); var artStore = new OfflineArtworkStore(folder, 16384);
                var art = new byte[10000]; art[0] = 0xff; art[1] = 0xd8; art[2] = 0xff;
                var sentinel = Path.Combine(folder, "unowned.jpg"); await File.WriteAllTextAsync(sentinel, "original");
                await artStore.SaveAsync(track.Key, track.Revision, art);
                Check((await artStore.ReadAsync(track.Key, track.Revision))!.SequenceEqual(art), "art stored");
                var next = AudioRequest.Hash("next"); await artStore.SaveAsync(track.Key, next, art);
                Check(await artStore.ReadAsync(track.Key, track.Revision) is null, "old revision removed");
                var another = AudioRequest.Hash("another"); await artStore.SaveAsync(another, next, art);
                Check(await artStore.ReadAsync(track.Key, next) is null && await artStore.ReadAsync(another, next) is not null, "bounded cache");
                Check(await store.VerifyAsync(track) && File.Exists(sentinel), "audio and foreign data preserved");
                await Reject<InvalidDataException>(() => artStore.SaveAsync("../escape", next, art));
                await Reject<InvalidDataException>(() => artStore.SaveAsync(another, next, new byte[4]));
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
