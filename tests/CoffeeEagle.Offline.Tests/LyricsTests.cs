using System.Buffers.Binary;
using System.Text;
using CoffeeEagle.Offline;

internal static class LyricsTests
{
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static Task Parse(string text) { LrcLyrics.Parse(Bytes(text)); return Task.CompletedTask; }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test, string root)
    {
        await test("LRC requires exact sibling basename and rejects duplicate matches", async () =>
        {
            Check(LrcLyrics.FindSibling("日本語.曲.mp3", new[] { "unrelated.lrc", "日本語.曲.LRC" }, x => x) == "日本語.曲.LRC", "Unicode match");
            Check(LrcLyrics.FindSibling("track.mp3", new[] { "other.lrc", "Track.lrc", "track.mp3.lrc" }, x => x) is null, "wrong fallback");
            await Reject<InvalidDataException>(() => { LrcLyrics.FindSibling("a.mp3", new[] { "a.lrc", "a.LRC" }, x => x); return Task.CompletedTask; });
        });
        await test("LRC fractions, repeated timestamps, ordering and translations follow the playback clock", () =>
        {
            var lrc = LrcLyrics.Parse(Bytes("[ti:Example]\n[00:03.125]third\n[00:01.2][00:02.34]repeat\n[00:02.340]訳\n[00:04]"));
            Check(lrc.IndexAt(1199) == -1 && lrc.IndexAt(1200) == 0, "start boundary");
            Check(lrc.WindowAt(2340).Current == "repeat\n訳", "translations/repeated stamp");
            Check(lrc.WindowAt(3125).Previous == "repeat\n訳" && lrc.WindowAt(3125).Current == "third", "sorted window");
            Check(lrc.WindowAt(4000).Current == "" && lrc.WindowAt(1200).Current == "repeat", "clear and backward seek");
            return Task.CompletedTask;
        });
        await test("positive LRC offset advances, negative offset delays and applies to all lines", () =>
        {
            var early = LrcLyrics.Parse(Bytes("[00:01]one\n[offset:+500]"));
            var late = LrcLyrics.Parse(Bytes("[offset:-500]\n[00:01]one"));
            Check(early.IndexAt(499) == -1 && early.IndexAt(500) == 0, "positive offset");
            Check(late.IndexAt(1499) == -1 && late.IndexAt(1500) == 0, "negative offset");
            Check(LrcLyrics.Parse(Bytes("[offset:9999999999999999999999]\n[00:01]one")).IndexAt(999) == -1, "overflow offset");
            return Task.CompletedTask;
        });
        await test("untimed UTF-8/BOM UTF-16 lyrics remain readable and malformed tags are ignored", () =>
        {
            const string words = "[ar:Artist]\r\n日本語のテスト\r次の行\n[00:99]invalid\n[bad]";
            foreach (var bytes in new[] { Bytes(words), Encoding.UTF8.GetPreamble().Concat(Bytes(words)).ToArray(),
                Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(words)).ToArray(),
                Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(words)).ToArray() })
            {
                var lyric = LrcLyrics.Parse(bytes);
                Check(!lyric.IsTimed && lyric.PlainText == "日本語のテスト\n次の行", "encoding/plaintext");
            }
            return Task.CompletedTask;
        });
        await test("LRC input bounds reject binary, invalid encoding, too many events and oversized streams", async () =>
        {
            await Reject<InvalidDataException>(() => { LrcLyrics.Parse([0xff, 0x80]); return Task.CompletedTask; });
            await Reject<InvalidDataException>(() => Parse("bad\0text"));
            await Reject<InvalidDataException>(() => Parse(string.Concat(Enumerable.Repeat("[00:01]x\n", 4001))));
            await Reject<InvalidDataException>(() => LrcLyrics.ReadAsync(new MemoryStream(new byte[LrcLyrics.MaxBytes + 1])));
        });
        await test("lyrics persist independently and a cancelled or invalid replacement preserves them", async () =>
        {
            var folder = Path.Combine(root, "lyrics-persist"); var key = AudioRequest.Hash("song"); var revision = AudioRequest.Hash("v1");
            var store = new OfflineLyricsStore(folder); var first = Bytes("[00:01]original");
            await store.SaveAsync(key, revision, first);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Reject<OperationCanceledException>(() => store.SaveAsync(key, revision, Bytes("replacement"), cancel.Token));
            await Reject<InvalidDataException>(() => store.SaveAsync(key, revision, [0xff]));
            Check((await new OfflineLyricsStore(folder).ReadAsync(key, revision))!.SequenceEqual(first), "lost previous lyrics");
            await File.WriteAllTextAsync(Path.Combine(folder, "keep.txt"), "foreign");
            await store.SaveAsync(key, AudioRequest.Hash("v2"), Bytes("new"));
            Check(await store.ReadAsync(key, revision) is null && File.Exists(Path.Combine(folder, "keep.txt")), "revision cleanup ownership");
        });
        var audio = Bytes("test audio bytes");
        var request = new AudioRequest(AudioRequest.Hash("track"), "テスト音声", AudioRequest.Hash("revision"), ".mp3", audio.Length, AudioRequest.Hash("test audio bytes"));
        await test("v3 maximum lyrics and artwork roundtrip without consuming audio bytes", async () =>
        {
            var lyrics = Bytes(new string('a', LrcLyrics.MaxBytes));
            var art = new byte[OfflineArtworkStore.MaxImageBytes]; art[0] = 0xff; art[1] = 0xd8; art[2] = 0xff; art[^2] = 0xff; art[^1] = 0xd9;
            using var stream = new MemoryStream();
            await AudioWire.WriteHeaderAsync(stream, new(3, Guid.NewGuid().ToString("N"), request, art, lyrics), default);
            var end = stream.Position; stream.Write(audio); stream.Position = 0;
            var header = await AudioWire.ReadHeaderAsync(stream, default);
            Check(header.Lyrics!.SequenceEqual(lyrics) && header.Artwork!.SequenceEqual(art) && stream.Position == end, "framing");
        });
        await test("old wire versions reject lyrics and invalid v3 headers stay bounded", async () =>
        {
            foreach (var version in new[] { 1, 2, 4 })
                await Reject<InvalidDataException>(() => AudioWire.WriteHeaderAsync(new MemoryStream(), new(version, Guid.NewGuid().ToString("N"), request, Lyrics: Bytes("x")), default));
            var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, 128 * 1024 + 1);
            await Reject<InvalidDataException>(() => AudioWire.ReadHeaderAsync(new MemoryStream(length), default));
            await Reject<InvalidDataException>(() => AudioWire.WriteHeaderAsync(new MemoryStream(), new(3, Guid.NewGuid().ToString("N"), request, Lyrics: [0xff]), default));
        });
        await test("lyrics-only update/removal needs no audio download and legacy peers cannot erase lyrics", async () =>
        {
            var store = new OfflineAudioStore(Path.Combine(root, "lyrics-transfer"), OfflineAudioStore.GiB);
            var track = await store.SaveAsync(request, _ => Task.FromResult<Stream>(new MemoryStream(audio)));
            var header = new AudioEnvelope(3, Guid.NewGuid().ToString("N"), track.Request, Lyrics: Bytes("[00:01]first"));
            await AudioWire.ApplyLyricsAsync(store, header);
            await store.SaveAsync(request, _ => throw new Exception("must not download audio"));
            await AudioWire.ApplyLyricsAsync(store, header with { Lyrics = Bytes("[00:02]new") });
            await AudioWire.ApplyLyricsAsync(store, header with { Version = 2, Lyrics = null });
            Check(Encoding.UTF8.GetString((await store.Lyrics.ReadAsync(track.Key, track.Revision))!) == "[00:02]new", "legacy erase/update");
            Check(await store.VerifyAsync(track) && (await store.SnapshotAsync()).Tracks.Single() == track, "audio was changed");
            await AudioWire.ApplyLyricsAsync(store, header with { Lyrics = null });
            Check(await store.Lyrics.ReadAsync(track.Key, track.Revision) is null, "missing sidecar not cleared");
            await AudioWire.ApplyLyricsAsync(store, header);
            await store.RemoveAsync(track.Key);
            Check(await store.Lyrics.ReadAsync(track.Key, track.Revision) is null, "removed song leaked lyrics");
        });
    }
}
