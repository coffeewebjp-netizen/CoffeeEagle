namespace CoffeeEagle.Reader.Models;

public sealed class EagleAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string? Extension { get; set; }

    public string? FileUri { get; set; }

    public string? ThumbnailUri { get; set; }

    public string MediaKind { get; set; } = EagleAssetMediaKind.Image;

    public string? SourceInfoId { get; set; }

    public long SourceModifiedStamp { get; set; }

    public List<string> FolderIds { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    public string? SourceUrl { get; set; }

    public string? Annotation { get; set; }

    public long SizeBytes { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? ModifiedAt { get; set; }

    public string SearchBlob => string.Join(' ', new[]
    {
        Name,
        FileName,
        Extension ?? string.Empty,
        MediaKind,
        SourceUrl ?? string.Empty,
        Annotation ?? string.Empty,
        string.Join(' ', Tags)
    });
}
