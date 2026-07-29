namespace CoffeeEagle.Reader.Models;

public sealed record LibrarySyncProgress(
    string Phase,
    string? Target = null,
    int? Completed = null,
    int? Total = null,
    string? Detail = null)
{
    public static implicit operator LibrarySyncProgress(string phase) => new(phase);
}
