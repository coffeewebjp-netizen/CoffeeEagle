using System.Net;
using System.Text;
using System.Text.Json;

// Simulate Drive exposing metadata and artwork before the large original arrives.
// The Eagle mtime is already final and will not change when the upload completes.
sealed class PendingMediaFixture(string extension) : HttpMessageHandler
{
    public bool IncludeMedia { get; set; }
    public bool FileNameOnly { get; set; }
    public long SourceStamp { get; set; } = 1000;
    public int InfoReads { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var id = uri.Segments.Last();
        var query = Uri.UnescapeDataString(uri.Query);
        object payload;
        if (id == "files")
        {
            var parent = query.Split("'")[1];
            var files = new List<object>();
            switch (parent)
            {
                case "folder-media":
                    files.Add(Entry("images", "images", "application/vnd.google-apps.folder"));
                    files.Add(Entry("mtime", "mtime.json", "application/json"));
                    break;
                case "images":
                    files.Add(Entry("info", "asset.info", "application/vnd.google-apps.folder"));
                    break;
                case "info":
                    InfoReads++;
                    files.Add(Entry("metadata", "metadata.json", "application/json"));
                    files.Add(Entry("thumbnail", "clip_thumbnail.png", "image/png", 19_970));
                    files.Add(Entry("artwork", "poster.jpg", "image/jpeg", 40_000_000));
                    if (IncludeMedia) files.Add(Entry("original", "clip." + extension, extension == "mp4" ? "video/mp4" : "audio/mpeg", 31_644_520));
                    break;
                default: throw new Exception("Unexpected folder: " + parent);
            }
            payload = new { files };
        }
        else if (query.Contains("alt=media"))
        {
            payload = id switch
            {
                "mtime" => new Dictionary<string, long> { ["all"] = 1, ["asset"] = SourceStamp },
                "metadata" => FileNameOnly
                    ? (object)new { id = "asset", name = "clip", fileName = "clip." + extension, size = 31_644_520 }
                    : new { id = "asset", name = "clip", ext = extension, size = 31_644_520 },
                _ => throw new Exception("Unexpected content request: " + id)
            };
        }
        else payload = Entry("folder-media", "Media.library", "application/vnd.google-apps.folder");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") });
    }

    static object Entry(string id, string name, string mimeType, long size = 0) => new
    {
        id, name, mimeType, size = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        modifiedTime = "2026-09-21T00:00:00Z"
    };
}
