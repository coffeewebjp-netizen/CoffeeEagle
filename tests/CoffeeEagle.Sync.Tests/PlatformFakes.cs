using System.Text;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

// Platform/auth boundaries only; production HTTP parsing and both indexers run unchanged.
public sealed class EagleLibraryStore
{
    public Task<EagleReaderState> LoadAsync(CancellationToken ct) => Task.FromResult(new EagleReaderState());
}
internal static class FileSystem
{
    public static string CacheDirectory => Path.GetTempPath();
}
public sealed partial class GoogleDriveLibraryService
{
    private Task<string> GetValidAccessTokenAsync(EagleReaderState state, CancellationToken ct)
    {
        _accessTokenExpiresAt = DateTimeOffset.MaxValue;
        _accessToken = "fixture-token";
        return Task.FromResult(_accessTokenExpiresAt > DateTimeOffset.UtcNow ? _accessToken : throw new Exception("Expired fixture"));
    }
    private static string GetErrorMessage(string body) => body;
}
public sealed record DocumentEntry(string DocumentId, string Name, string MimeType, long LastModified, long Size, string Uri)
{
    public bool IsDirectory => MimeType == "directory";
}
public sealed class AndroidDocumentTreeService(string mtime)
{
    public void RequestProviderRefresh(string uri) { }
    public DocumentEntry GetRoot(string uri) => new("root", "Fixture.library", "directory", 1, 0, uri);
    public IReadOnlyList<DocumentEntry> ListChildren(string uri, string id) => id == "root"
        ? [new("images", "images", "directory", 1, 0, "images"), new("mtime", "mtime.json", "application/json", 1, 10, "mtime")]
        : [];
    public Stream OpenRead(string uri) => new MemoryStream(Encoding.UTF8.GetBytes(mtime));
    public DocumentEntry? TryGetDocument(string uri, string id) => null;
    public string GetSourceKind(string uri) => EagleLibrarySourceKind.GoogleDrive;
    public string GetSourceLabel(string uri) => "Google Drive";
    public bool IsGoogleDriveTree(string uri) => true;
}
