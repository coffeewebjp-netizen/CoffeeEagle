namespace CoffeeEagle.Reader.Models;

public sealed class EagleSourceEntry
{
    public string SourceInfoId { get; set; } = string.Empty;

    public long SourceModifiedStamp { get; set; }

    public string State { get; set; } = EagleSourceEntryState.Active;
}

public static class EagleSourceEntryState
{
    public const string Active = "active";
    public const string Deleted = "deleted";
    public const string MissingMetadata = "missing-metadata";
    public const string ReadFailed = "read-failed";
}
