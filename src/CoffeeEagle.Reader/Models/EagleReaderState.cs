namespace CoffeeEagle.Reader.Models;

public sealed class EagleReaderState
{
    public string? ActiveLibraryId { get; set; }

    public string? SelectedFolderId { get; set; }

    public string? SelectedTag { get; set; }

    public List<string> SelectedTags { get; set; } = [];

    public string SearchText { get; set; } = string.Empty;

    public int GridSpan { get; set; } = 3;

    public string GoogleDriveClientId { get; set; } = "327808944898-qr1qd5imhe3ddp56feng1kmpkpqnq10c.apps.googleusercontent.com";

    public string? GoogleDriveClientSecret { get; set; }

    public string? GoogleDriveFolderId { get; set; }

    public DateTimeOffset? GoogleDriveConnectedAt { get; set; }

    public List<EagleLibrary> Libraries { get; set; } = [];
}