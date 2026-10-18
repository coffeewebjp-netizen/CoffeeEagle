using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

/// <summary>Stable provider-aware identities and revisions for ordinary phone file copies.</summary>
public static class PhoneFileCacheKey
{
    public static string ExclusionKey(EagleAsset asset) =>
        string.IsNullOrWhiteSpace(asset.SourceInfoId) ? asset.Id : asset.SourceInfoId;

    public static string ProviderIdentity(string sourceUri)
    {
        const string drivePrefix = "gdrive://files/";
        if (sourceUri.StartsWith(drivePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var end = sourceUri.IndexOf('/', drivePrefix.Length);
            var id = end < 0 ? sourceUri[drivePrefix.Length..] : sourceUri[drivePrefix.Length..end];
            return "gdrive-file:" + id;
        }

        return sourceUri;
    }

    public static string IdentityFor(EagleAsset asset, string sourceUri) =>
        ExclusionKey(asset) + "\n" + ProviderIdentity(sourceUri);

    public static string RevisionFor(EagleAsset asset, string? sourceUri = null)
    {
        sourceUri ??= asset.FileUri ?? asset.ThumbnailUri ?? string.Empty;
        var isThumbnail = !string.Equals(sourceUri, asset.FileUri, StringComparison.OrdinalIgnoreCase);
        var providerStamp = isThumbnail ? asset.ThumbnailFileModifiedStamp : asset.SourceFileModifiedStamp;
        var size = isThumbnail ? asset.ThumbnailSizeBytes : asset.SizeBytes;
        return string.Join('\n',
            IdentityFor(asset, sourceUri),
            asset.SourceModifiedStamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            providerStamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            asset.ModifiedAt?.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Path.GetExtension(sourceUri).ToLowerInvariant(),
            sourceUri);
    }
}

public sealed record PhoneFileCacheTarget(EagleAsset Asset, string Uri, string Revision, long? ExpectedLength)
{
    public string Identity => PhoneFileCacheKey.IdentityFor(Asset, Uri);
}

public sealed record PhoneFileCachePlan(
    IReadOnlyList<PhoneFileCacheTarget> Files,
    IReadOnlyList<EagleAsset> AudioAssets,
    int ExcludedCount,
    int UnverifiedCount)
{
    public static PhoneFileCachePlan Build(EagleLibrary library)
    {
        var sourceEntries = library.SourceEntries ?? [];
        HashSet<string>? activeSourceIds = sourceEntries.Count == 0
            ? null
            : new HashSet<string>(sourceEntries
                .Where(entry => string.Equals(entry.State, EagleSourceEntryState.Active, StringComparison.Ordinal))
                .Select(entry => entry.SourceInfoId), StringComparer.OrdinalIgnoreCase);
        var verifiedAssets = library.Assets
            .Where(asset => activeSourceIds is null || activeSourceIds.Contains(PhoneFileCacheKey.ExclusionKey(asset)))
            .ToList();
        var excludedIds = new HashSet<string>(library.PhoneCacheExcludedSourceInfoIds ?? [], StringComparer.OrdinalIgnoreCase);
        var eligibleAssets = verifiedAssets
            .Where(asset => !excludedIds.Contains(PhoneFileCacheKey.ExclusionKey(asset)))
            .ToList();
        var files = new Dictionary<string, PhoneFileCacheTarget>(StringComparer.Ordinal);
        foreach (var asset in eligibleAssets)
        {
            Add(asset.FileUri, asset);
            if (!string.Equals(asset.ThumbnailUri, asset.FileUri, StringComparison.OrdinalIgnoreCase))
                Add(asset.ThumbnailUri, asset);
        }

        return new PhoneFileCachePlan(
            files.Values.ToList(),
            eligibleAssets.Where(asset => asset.MediaKind == EagleAssetMediaKind.Audio
                && !string.IsNullOrWhiteSpace(asset.FileUri)).ToList(),
            verifiedAssets.Count - eligibleAssets.Count,
            library.Assets.Count - verifiedAssets.Count);

        void Add(string? uri, EagleAsset asset)
        {
            if (string.IsNullOrWhiteSpace(uri)) return;
            var target = new PhoneFileCacheTarget(
                asset,
                uri,
                PhoneFileCacheKey.RevisionFor(asset, uri),
                string.Equals(uri, asset.FileUri, StringComparison.OrdinalIgnoreCase)
                    ? asset.SizeBytes > 0 ? asset.SizeBytes : null
                    : asset.ThumbnailSizeBytes > 0 ? asset.ThumbnailSizeBytes : null);
            files.TryAdd(target.Identity + "\n" + target.Revision, target);
        }
    }
}
