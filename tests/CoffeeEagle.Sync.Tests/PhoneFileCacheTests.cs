using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;

internal static class PhoneFileCacheTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("phone file cache reuses unchanged revision and replaces stale data", async () =>
        {
            using var root = new ScratchDirectory();
            var store = new PhoneFileCacheStore(root.Path);
            var opens = 0;
            async Task<Stream> Open(CancellationToken _) { opens++; return new MemoryStream([1, 2, 3]); }
            var first = await store.SaveIfMissingAsync("file-a", "r1", ".png", Open, expectedLength: 3, requireNonEmpty: true);
            var reused = await store.SaveIfMissingAsync("file-a", "r1", ".png", Open, expectedLength: 3, requireNonEmpty: true);
            check(first.Downloaded && !reused.Downloaded && opens == 1, "unchanged bytes were reopened");
            await store.SaveIfMissingAsync("file-a", "r2", ".png", _ => Task.FromResult<Stream>(new MemoryStream([4, 5])), expectedLength: 2, requireNonEmpty: true);
            check(!File.Exists(first.Path), "verified replacement did not prune the older browsing revision");
        });

        await test("short reads and cancellation keep the last verified revision", async () =>
        {
            using var root = new ScratchDirectory();
            var store = new PhoneFileCacheStore(root.Path);
            var old = await store.SaveIfMissingAsync("asset", "old", ".bin", _ => Task.FromResult<Stream>(new MemoryStream([1, 2])), expectedLength: 2, requireNonEmpty: true);
            try
            {
                await store.SaveIfMissingAsync("asset", "new", ".bin", _ => Task.FromResult<Stream>(new MemoryStream([3])), expectedLength: 2, requireNonEmpty: true);
                throw new Exception("short source unexpectedly committed");
            }
            catch (InvalidDataException) { }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await store.SaveIfMissingAsync("asset", "cancelled", ".bin", _ => Task.FromResult<Stream>(new MemoryStream([9])), cancellationToken: cancelled.Token);
                throw new Exception("cancelled write unexpectedly completed");
            }
            catch (OperationCanceledException) { }
            check(File.Exists(old.Path) && File.ReadAllBytes(old.Path).SequenceEqual(new byte[] { 1, 2 }), "last good copy was lost");
        });

        await test("midstream cancellation cleans temporary bytes and a later retry commits", async () =>
        {
            using var root = new ScratchDirectory();
            var store = new PhoneFileCacheStore(root.Path);
            var old = await store.SaveIfMissingAsync("asset", "old", ".bin", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])), expectedLength: 4, requireNonEmpty: true);
            using var cancelled = new CancellationTokenSource();
            try
            {
                await store.SaveIfMissingAsync("asset", "new", ".bin",
                    _ => Task.FromResult<Stream>(new CancelAfterChunkStream([5, 6, 7, 8], cancelled)),
                    expectedLength: 4, requireNonEmpty: true, cancellationToken: cancelled.Token);
                throw new Exception("midstream cancellation unexpectedly committed");
            }
            catch (OperationCanceledException) { }
            check(File.Exists(old.Path) && !Directory.EnumerateFiles(root.Path, "*.tmp").Any(), "partial write damaged old data or left a temp");
            var retried = await store.SaveIfMissingAsync("asset", "new", ".bin", _ => Task.FromResult<Stream>(new MemoryStream([5, 6, 7, 8])), expectedLength: 4, requireNonEmpty: true);
            check(retried.Downloaded && File.ReadAllBytes(retried.Path).SequenceEqual(new byte[] { 5, 6, 7, 8 }) && !File.Exists(old.Path), "retry did not atomically replace old revision");
        });

        await test("wrong-length cache hit is repaired and zero-byte media is not accepted", async () =>
        {
            using var root = new ScratchDirectory();
            var store = new PhoneFileCacheStore(root.Path);
            var existing = await store.SaveIfMissingAsync("image", "same", ".jpg", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])), expectedLength: 3, requireNonEmpty: true);
            File.WriteAllBytes(existing.Path, [0]);
            var repaired = await store.SaveIfMissingAsync("image", "same", ".jpg", _ => Task.FromResult<Stream>(new MemoryStream([7, 8, 9])), expectedLength: 3, requireNonEmpty: true);
            check(repaired.Downloaded && File.ReadAllBytes(existing.Path).SequenceEqual(new byte[] { 7, 8, 9 }), "same-revision corruption was reused");
            try
            {
                await store.SaveIfMissingAsync("empty", "same", ".mp4", _ => Task.FromResult<Stream>(new MemoryStream()), requireNonEmpty: true);
                throw new Exception("empty media unexpectedly committed");
            }
            catch (InvalidDataException) { }
        });

        await test("same-revision concurrent requests share one provider read", async () =>
        {
            using var root = new ScratchDirectory();
            var store = new PhoneFileCacheStore(root.Path);
            var opens = 0;
            async Task<Stream> Open(CancellationToken _) { Interlocked.Increment(ref opens); await Task.Delay(20); return new MemoryStream([1, 2, 3, 4]); }
            var results = await Task.WhenAll(Enumerable.Range(0, 5)
                .Select(_ => store.SaveIfMissingAsync("same-source", "r1", ".dat", Open, expectedLength: 4, requireNonEmpty: true)));
            check(opens == 1 && results.Count(result => result.Downloaded) == 1, "concurrent duplicate reads were not coalesced");
        });

        await test("lyrics pointer changes before old revision cleanup", async () =>
        {
            using var root = new ScratchDirectory();
            var store = new PhoneFileCacheStore(root.Path);
            const string audioUri = "content://audio/1";
            var identity = PhoneFileCacheStore.LyricsIdentity(audioUri);
            var old = await store.SaveIfMissingAsync(identity, "lrc-r1", ".lrc", _ => Task.FromResult<Stream>(new MemoryStream("[00:01.00]old"u8.ToArray())), preserveOlderRevisions: true);
            await store.SetLyricsManifestAsync(audioUri, new PhoneLyricsCacheState(PhoneLyricsCacheState.Available, "lrc-1", "lrc-r1", ".lrc", old.Length, "audio-r1"));
            var next = await store.SaveIfMissingAsync(identity, "lrc-r2", ".lrc", _ => Task.FromResult<Stream>(new MemoryStream("[00:01.00]new"u8.ToArray())), preserveOlderRevisions: true);
            check(File.Exists(old.Path), "old lyric bytes were removed before pointer commit");
            await store.SetLyricsManifestAsync(audioUri, new PhoneLyricsCacheState(PhoneLyricsCacheState.Available, "lrc-2", "lrc-r2", ".lrc", next.Length, "audio-r1"));
            check(store.TryGetLyricsPath(audioUri, out var current) && current == next.Path, "manifest did not switch to the verified new sidecar");
            await store.RemoveOlderRevisionsAsync(identity, "lrc-r2", ".lrc");
            check(!File.Exists(old.Path) && File.Exists(next.Path), "old lyric copy remained after safe pointer switch");
        });

        await test("phone cache plans all active file kinds, respects exclusions and retries unchanged items", () =>
        {
            var primary = new EagleAsset
            {
                Id = "id-video", SourceInfoId = "video-info", Name = "Video", MediaKind = EagleAssetMediaKind.Video,
                FileUri = "gdrive://files/video-id/movie.mp4", ThumbnailUri = "gdrive://files/thumb-id/cover.jpg",
                SizeBytes = 120, ThumbnailSizeBytes = 13, SourceModifiedStamp = 20,
                SourceFileModifiedStamp = 21, ThumbnailFileModifiedStamp = 22
            };
            var audio = new EagleAsset
            {
                Id = "id-audio", SourceInfoId = "audio-info", Name = "Song", MediaKind = EagleAssetMediaKind.Audio,
                FileUri = "content://library/song.mp3", SizeBytes = 80, SourceModifiedStamp = 30
            };
            var excluded = new EagleAsset { Id = "id-skip", SourceInfoId = "skip-info", FileUri = "content://library/skip.png", SizeBytes = 10 };
            var unreadable = new EagleAsset { Id = "id-stale", SourceInfoId = "stale-info", FileUri = "content://library/old.png", SizeBytes = 10 };
            var library = new EagleLibrary
            {
                Assets = [primary, audio, excluded, unreadable],
                PhoneCacheExcludedSourceInfoIds = ["skip-info"],
                SourceEntries =
                [
                    new() { SourceInfoId = "video-info", State = EagleSourceEntryState.Active },
                    new() { SourceInfoId = "audio-info", State = EagleSourceEntryState.Active },
                    new() { SourceInfoId = "skip-info", State = EagleSourceEntryState.Active },
                    new() { SourceInfoId = "stale-info", State = EagleSourceEntryState.ReadFailed }
                ]
            };
            var first = PhoneFileCachePlan.Build(library);
            var retry = PhoneFileCachePlan.Build(library);
            check(first.Files.Count == 3 && first.AudioAssets.Single() == audio, "plan missed active media or included stale/unselected entries");
            check(first.Files.Any(item => item.Uri == primary.FileUri && item.ExpectedLength == 120)
                && first.Files.Any(item => item.Uri == primary.ThumbnailUri && item.ExpectedLength == 13)
                && first.Files.Any(item => item.Uri == audio.FileUri && item.ExpectedLength == 80), "primary/thumbnail expected lengths were not independent");
            check(first.ExcludedCount == 1 && first.UnverifiedCount == 1, "skip counts are wrong");
            check(first.Files.Select(item => item.Identity + "\n" + item.Revision).SequenceEqual(
                retry.Files.Select(item => item.Identity + "\n" + item.Revision)), "unchanged eligible items are not planned again for retry");
            check(PhoneFileCacheKey.RevisionFor(primary, primary.FileUri) != PhoneFileCacheKey.RevisionFor(primary, primary.ThumbnailUri), "thumbnail revision shares primary metadata");
            return Task.CompletedTask;
        });

        await test("cache preflight preserves the free-space reserve", async () =>
        {
            using var root = new ScratchDirectory();
            var store = new PhoneFileCacheStore(root.Path, () => 20, reserveBytes: 16);
            try
            {
                await store.SaveIfMissingAsync("too-large", "r1", ".bin", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4, 5])), expectedLength: 5);
                throw new Exception("write exceeded the configured reserve");
            }
            catch (PhoneFileCacheStorageException) { }
        });

        await test("library cache exclusions survive JSON persistence round-trip", () =>
        {
            var library = new EagleLibrary
            {
                PhoneCacheExcludedSourceInfoIds = ["one.info", "another.info"],
                Assets = [new EagleAsset { Id = "one", SourceInfoId = "one.info", FileUri = "content://one" }]
            };
            var serialized = System.Text.Json.JsonSerializer.Serialize(library);
            var restarted = System.Text.Json.JsonSerializer.Deserialize<EagleLibrary>(serialized)!;
            check(restarted.PhoneCacheExcludedSourceInfoIds.SequenceEqual(library.PhoneCacheExcludedSourceInfoIds), "exclusions changed across persisted model JSON");
            return Task.CompletedTask;
        });
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coffee-eagle-phone-cache-tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    private sealed class CancelAfterChunkStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        private int _readCount;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readCount) > 1)
            {
                cancellation.Cancel();
                return Task.FromException<int>(new OperationCanceledException(cancellation.Token));
            }
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) > 1)
            {
                cancellation.Cancel();
                return ValueTask.FromException<int>(new OperationCanceledException(cancellation.Token));
            }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
