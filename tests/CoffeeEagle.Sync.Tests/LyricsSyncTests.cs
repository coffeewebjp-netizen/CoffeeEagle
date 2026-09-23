using System.Net;
using System.Text;
using System.Text.Json;
using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;

internal static class LyricsSyncTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Drive lyrics added/edited/deleted without audio mtime changes are resolved afresh", async () =>
        {
            var fixture = new Fixture(); using var http = new HttpClient(fixture);
            var service = new GoogleDriveLibraryService(new(), http);
            var asset = Asset();
            Check(await service.ReadLyricsAsync(asset, default) is null, "unrelated lyric matched");
            fixture.IncludeLyrics = true;
            Check(Encoding.UTF8.GetString((await service.ReadLyricsAsync(asset, default))!) == fixture.Lyrics, "new sidecar missed");
            fixture.Lyrics = "[00:02]変更した歌詞";
            Check(Encoding.UTF8.GetString((await service.ReadLyricsAsync(asset, default))!) == fixture.Lyrics, "stale cached lyrics");
            fixture.IncludeLyrics = false;
            Check(await service.ReadLyricsAsync(asset, default) is null, "deleted lyrics reused");
            Check(fixture.MediaReads == 2 && fixture.ParentReads == 0 && fixture.ListReads == 4, "unexpected cache/download/parent request");
        });
        await test("legacy Drive catalog resolves the physical parent and rejects ambiguous lyrics", async () =>
        {
            var fixture = new Fixture { IncludeLyrics = true }; using var http = new HttpClient(fixture);
            var service = new GoogleDriveLibraryService(new(), http); var asset = Asset(); asset.SourceDirectoryUri = null;
            Check(await service.ReadLyricsAsync(asset, default) is not null && fixture.ParentReads == 1, "legacy parent lookup");
            fixture.Duplicate = true;
            try { await service.ReadLyricsAsync(asset, default); throw new Exception("duplicate accepted"); }
            catch (InvalidDataException) { }
            Check(fixture.MediaReads == 1, "ambiguous file downloaded");
        });
        await test("Drive listing/download failure is distinguishable from a confirmed missing lyric", async () =>
        {
            var fixture = new Fixture { IncludeLyrics = true }; using var http = new HttpClient(fixture);
            var service = new GoogleDriveLibraryService(new(), http);
            fixture.FailListing = true;
            try { await service.ReadLyricsAsync(Asset(), default); throw new Exception("listing failure silently became missing"); }
            catch (InvalidOperationException) { }
            fixture.FailListing = false; fixture.FailMedia = true;
            try { await service.ReadLyricsAsync(Asset(), default); throw new Exception("download failure silently became missing"); }
            catch (IOException) { }
        });
        await test("empty Drive sibling listing cannot erase previously saved lyrics", async () =>
        {
            var fixture = new Fixture { EmptyListing = true }; using var http = new HttpClient(fixture);
            var service = new GoogleDriveLibraryService(new(), http);
            try { await service.ReadLyricsAsync(Asset(), default); throw new Exception("empty listing accepted as lyric deletion"); }
            catch (IOException) { }
            Check(fixture.MediaReads == 0, "unexpected download");
        });
        await test("lyric matching uses the actual audio filename even with stale Eagle metadata", async () =>
        {
            var fixture = new Fixture { IncludeLyrics = true }; using var http = new HttpClient(fixture);
            var asset = Asset(); asset.FileName = "old-title.mp3";
            Check(await new GoogleDriveLibraryService(new(), http).ReadLyricsAsync(asset, default) is not null, "metadata overrode actual sibling name");
        });
    }
    private static EagleAsset Asset() => new() { Id = "song", FileName = "曲.mp3", FileUri = "gdrive://files/audio/%E6%9B%B2.mp3", SourceDirectoryUri = "gdrive://folders/physical-info" };
    private sealed class Fixture : HttpMessageHandler
    {
        public bool IncludeLyrics, Duplicate, FailListing, FailMedia, EmptyListing;
        public int ParentReads, MediaReads, ListReads;
        public string Lyrics = "[00:01]テスト歌詞";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!; var query = Uri.UnescapeDataString(uri.Query);
            Check(request.Headers.Authorization?.Scheme == "Bearer", "no auth");
            if (uri.Segments.Last() == "files")
            {
                ListReads++;
                Check(query.Contains("'physical-info' in parents"), "wrong sibling directory");
                if (FailListing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if (EmptyListing) return Json(new { files = Array.Empty<object>() });
                var files = new List<object> { Entry("audio", "曲.mp3"), Entry("other", "他の曲.lrc") };
                if (IncludeLyrics) files.Add(Entry("lyrics", "曲.lrc"));
                if (Duplicate) files.Add(Entry("lyrics2", "曲.LRC"));
                return Json(new { files });
            }
            if (query.Contains("fields=parents")) { ParentReads++; return Json(new { parents = new[] { "physical-info" } }); }
            Check(uri.Segments.Last() == "lyrics" && query.Contains("alt=media"), "unexpected media request");
            MediaReads++;
            return Task.FromResult(new HttpResponseMessage(FailMedia ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(Lyrics, Encoding.UTF8) });
        }
        private static object Entry(string id, string name) => new { id, name, mimeType = "text/plain", size = "100" };
        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
    }
}
