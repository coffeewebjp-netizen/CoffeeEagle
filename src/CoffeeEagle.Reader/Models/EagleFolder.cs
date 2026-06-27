namespace CoffeeEagle.Reader.Models;

public sealed class EagleFolder
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ParentId { get; set; }

    public int SortOrder { get; set; }

    public string Path { get; set; } = string.Empty;
}
