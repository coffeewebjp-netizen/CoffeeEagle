using System.Net;
using System.Text;
using System.Text.Json;
using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;

var passed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task Test(string name, Func<Task> run) { await run(); Console.WriteLine("PASS " + name); passed++; }
async Task Reject(Func<Task> run)
{
    try { await run(); } catch (InvalidOperationException) { return; }
    throw new Exception("Expected safe rejection");
}
EagleLibrary Existing(string folder) => new()
{
    Id = "saved-" + folder,
    SourceKind = EagleLibrarySourceKind.GoogleDriveApi,
    TreeUri = GoogleDriveLibraryService.BuildFolderUri(folder),
    RootDocumentId = folder
};

await Test("switching two API libraries uses each source and preserves identity", async () =>
{
    var handler = new DriveFixture();
    using var http = new HttpClient(handler);
    var service = new GoogleDriveLibraryService(new EagleLibraryStore(), http);
    var state = new EagleReaderState { GoogleDriveFolderId = "folder-B" };
    foreach (var id in new[] { "folder-A", "folder-B", "folder-A" })
    {
        var previous = Existing(id);
        var result = await service.IndexAsync(state, previous);
        Check(result.Id == previous.Id && result.RootDocumentId == id && result.Name == id, "library changed identity/source");
        Check(result.Assets.Single().Id == "asset-" + id, "assets from wrong source");
    }
    Check(handler.RootReads.SequenceEqual(new[] { "folder-A", "folder-B", "folder-A" }), "wrong Drive root requests");
    Check(state.GoogleDriveFolderId == "folder-B", "last add input mutated");
});
await Test("new registration uses the entered folder URL", async () =>
{
    using var http = new HttpClient(new DriveFixture());
    var result = await new GoogleDriveLibraryService(new(), http).IndexAsync(
        new() { GoogleDriveFolderId = "https://drive.google.com/drive/folders/folder-B?x=1" });
    Check(result.RootDocumentId == "folder-B", "new source incorrect");
});
await Test("legacy API root ID works without the last add input", async () =>
{
    using var http = new HttpClient(new DriveFixture());
    var previous = Existing("folder-A"); previous.TreeUri = "";
    var result = await new GoogleDriveLibraryService(new(), http).IndexAsync(new(), previous);
    Check(result.RootDocumentId == "folder-A", "legacy root lost");
});
await Test("invalid existing source cannot silently use another library", async () =>
{
    var handler = new DriveFixture(); using var http = new HttpClient(handler);
    await Reject(() => new GoogleDriveLibraryService(new(), http).IndexAsync(
        new() { GoogleDriveFolderId = "folder-B" }, new() { SourceKind = EagleLibrarySourceKind.GoogleDriveApi }));
    Check(handler.RootReads.Count == 0, "invalid source made a request");
});
foreach (var mtime in new[] { "{\"asset-old\":1000}", "{\"all\":30}" })
{
    await Test("empty Provider listing with nonempty mtime preserves prior catalog: " + mtime, async () =>
    {
        var previous = new EagleLibrary { Id = "existing", Assets = [new EagleAsset { Id = "asset-old", Name = "keep" }] };
        await Reject(() => new EagleLibraryIndexer(new(mtime)).IndexAsync("fixture", previous));
        Check(previous.Assets.Count == 1 && previous.Assets[0].Name == "keep", "catalog was mutated");
    });
}
await Test("genuinely empty library remains supported", async () =>
{
    var result = await new EagleLibraryIndexer(new("{\"all\":0}")).IndexAsync("fixture");
    Check(result.Assets.Count == 0, "empty library failed");
});
Console.WriteLine($"{passed}/{passed} checks passed");

sealed class DriveFixture : HttpMessageHandler
{
    public List<string> RootReads { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var id = uri.Segments.Last();
        var query = Uri.UnescapeDataString(uri.Query);
        object payload;
        if (id == "files")
        {
            var parent = query.Split("'")[1];
            payload = parent.StartsWith("folder-")
                ? new { files = new[] { Entry("images-" + parent, "images", true) } }
                : parent.StartsWith("images-")
                    ? new { files = new[] { Entry("info-" + parent[7..], "asset-" + parent[7..] + ".info", true) } }
                    : new { files = new[] { Entry("meta-" + parent[5..], "metadata.json"), Entry("media-" + parent[5..], "photo.png") } };
        }
        else if (query.Contains("alt=media"))
            payload = new { id = "asset-" + id[5..], name = "Photo", ext = "png" };
        else
        {
            RootReads.Add(id);
            payload = Entry(id, id + ".library", true);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") });
    }
    static object Entry(string id, string name, bool folder = false) => new
    {
        id, name, mimeType = folder ? "application/vnd.google-apps.folder" : name.EndsWith(".png") ? "image/png" : "application/json",
        size = "10", modifiedTime = "2026-09-21T00:00:00Z"
    };
}
