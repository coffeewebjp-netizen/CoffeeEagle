namespace CoffeeEagle.Reader.Models;

public sealed class EagleReaderState
{
    public string? ActiveLibraryId { get; set; }

    public string? SelectedFolderId { get; set; }

    public string? SelectedTag { get; set; }

    public List<string> SelectedTags { get; set; } = [];

    public string SearchText { get; set; } = string.Empty;

    public int GridSpan { get; set; } = 3;

    public List<EagleLibrary> Libraries { get; set; } = [];
}
