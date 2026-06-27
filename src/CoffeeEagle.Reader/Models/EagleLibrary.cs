namespace CoffeeEagle.Reader.Models;

public sealed class EagleLibrary
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "EAGLE Library";

    public string SourceKind { get; set; } = EagleLibrarySourceKind.DocumentTree;

    public string SourceLabel { get; set; } = "端末フォルダ";

    public string TreeUri { get; set; } = string.Empty;

    public string RootDocumentId { get; set; } = string.Empty;

    public DateTimeOffset IndexedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<EagleFolder> Folders { get; set; } = [];

    public List<EagleAsset> Assets { get; set; } = [];
}
