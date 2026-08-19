using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class GoogleDriveLibraryService
{

    private static DriveEntry? SelectThumbnail(IReadOnlyList<DriveEntry> files)
    {
        return files
            .Where(file => file.Name.Contains("thumb", StringComparison.OrdinalIgnoreCase)
                || file.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
                || file.Name.StartsWith("cover", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Size <= 0 ? long.MaxValue : file.Size)
            .FirstOrDefault();
    }


    private static DriveEntry? SelectPrimaryMedia(
        IReadOnlyList<DriveEntry> allFiles,
        IReadOnlyList<DriveEntry> supportedFiles,
        DriveEntry? thumbnail,
        JsonElement metadata)
    {
        var candidates = supportedFiles
            .Where(file => thumbnail is null || !string.Equals(file.Id, thumbnail.Id, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count > 0)
        {
            return candidates
                .OrderBy(file => IsImageFile(file) && IsLikelyThumbnail(file) ? 1 : 0)
                .ThenByDescending(file => file.Size)
                .First();
        }

        var metadataFileName = ReadString(metadata, "fileName", "filename");
        var metadataExtension = NormalizeExtension(ReadString(metadata, "ext", "extension") ?? Path.GetExtension(metadataFileName ?? string.Empty));
        var fallback = allFiles
            .Where(file => thumbnail is null || !string.Equals(file.Id, thumbnail.Id, StringComparison.Ordinal))
            .Where(file => !IsLikelyThumbnail(file))
            .ToList();

        if (!string.IsNullOrWhiteSpace(metadataFileName))
        {
            var byName = fallback.FirstOrDefault(file => string.Equals(file.Name, metadataFileName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        if (IsKnownMediaExtension(metadataExtension))
        {
            return fallback
                .OrderBy(file => IsImageFile(file) ? 1 : 0)
                .ThenByDescending(file => file.Size)
                .FirstOrDefault()
                ?? thumbnail;
        }

        return thumbnail;
    }


    private static bool IsMetadataFile(DriveEntry entry)
    {
        return !entry.IsFolder && string.Equals(entry.Name, "metadata.json", StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsSupportedMediaFile(DriveEntry entry) => IsImageFile(entry) || IsAudioFile(entry) || IsVideoFile(entry);


    private static bool IsImageFile(DriveEntry entry) => entry.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ImageExtensions.Contains(Path.GetExtension(entry.Name));


    private static bool IsAudioFile(DriveEntry entry) => entry.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || AudioExtensions.Contains(Path.GetExtension(entry.Name));


    private static bool IsVideoFile(DriveEntry entry) => entry.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || VideoExtensions.Contains(Path.GetExtension(entry.Name));


    private static bool IsLikelyThumbnail(DriveEntry entry)
    {
        return entry.Name.Contains("thumb", StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
            || entry.Name.StartsWith("cover", StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsKnownMediaExtension(string? extension)
    {
        return !string.IsNullOrWhiteSpace(extension)
            && (ImageExtensions.Contains(extension) || AudioExtensions.Contains(extension) || VideoExtensions.Contains(extension));
    }


    private static string ResolveMediaKind(DriveEntry? primaryFile, JsonElement metadata)
    {
        var extension = NormalizeExtension(ReadString(metadata, "ext", "extension") ?? Path.GetExtension(primaryFile?.Name ?? string.Empty));
        if (primaryFile is not null)
        {
            if (IsAudioFile(primaryFile)) return EagleAssetMediaKind.Audio;
            if (IsVideoFile(primaryFile)) return EagleAssetMediaKind.Video;
            if (IsImageFile(primaryFile)) return EagleAssetMediaKind.Image;
        }

        if (!string.IsNullOrWhiteSpace(extension))
        {
            if (AudioExtensions.Contains(extension)) return EagleAssetMediaKind.Audio;
            if (VideoExtensions.Contains(extension)) return EagleAssetMediaKind.Video;
            if (ImageExtensions.Contains(extension)) return EagleAssetMediaKind.Image;
        }

        return EagleAssetMediaKind.Other;
    }


    private static List<EagleFolder> ReadFolders(JsonElement root)
    {
        if (!TryGetProperty(root, out var foldersElement, "folders"))
        {
            return [];
        }

        var folders = new List<EagleFolder>();
        ReadFolderElement(foldersElement, null, folders, 0);
        return folders.GroupBy(folder => folder.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }


    private static int ReadFolderElement(JsonElement element, string? parentId, List<EagleFolder> folders, int sortOrder)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                sortOrder = ReadFolderElement(item, parentId, folders, sortOrder);
            }

            return sortOrder;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return sortOrder;
        }

        var id = ReadString(element, "id", "uuid", "folderId");
        var name = ReadString(element, "name", "title");
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
        {
            folders.Add(new EagleFolder { Id = id, Name = name, ParentId = parentId, SortOrder = sortOrder++ });
            parentId = id;
        }

        if (TryGetProperty(element, out var children, "children", "folders"))
        {
            sortOrder = ReadFolderElement(children, parentId, folders, sortOrder);
        }

        return sortOrder;
    }


    private static void EnsureReferencedFolders(List<EagleFolder> folders, IEnumerable<EagleAsset> assets)
    {
        var knownFolderIds = folders.Select(folder => folder.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folderId in assets.SelectMany(asset => asset.FolderIds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (knownFolderIds.Contains(folderId))
            {
                continue;
            }

            folders.Add(new EagleFolder { Id = folderId, Name = folderId, SortOrder = folders.Count });
            knownFolderIds.Add(folderId);
        }
    }


    private static void NormalizeFolderPaths(List<EagleFolder> folders)
    {
        var byId = folders.ToDictionary(folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            folder.Path = BuildFolderPath(folder, byId, []);
        }
    }


    private static string BuildFolderPath(EagleFolder folder, IReadOnlyDictionary<string, EagleFolder> byId, HashSet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(folder.ParentId)
            || !byId.TryGetValue(folder.ParentId, out var parent)
            || !visited.Add(folder.Id))
        {
            return folder.Name;
        }

        return string.Join(" / ", BuildFolderPath(parent, byId, visited), folder.Name);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return [];
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            return string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var value = ReadString(item, "id", "name", "title");
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
            }
        }

        return values;
    }


    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }


    private static long ReadLong(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return 0;
        }

        return ReadLongValue(property);
    }


    private static bool ReadBoolean(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number != 0;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            return bool.TryParse(value, out var boolean)
                ? boolean
                : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number != 0;
        }

        return false;
    }


    private static long ReadLongValue(JsonElement property)
    {
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }


    private static DateTimeOffset? ReadDate(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                return parsedDate;
            }

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumber))
            {
                return FromUnixTime(parsedNumber);
            }
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return FromUnixTime(number);
        }

        return null;
    }


    private static DateTimeOffset? ReadIsoDate(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }


    private static DateTimeOffset? FromUnixTime(long value)
    {
        try
        {
            return value > 9_999_999_999
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch
        {
            return null;
        }
    }


    private static bool TryGetProperty(JsonElement element, out JsonElement property, params string[] names)
    {
        property = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in names)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        return false;
    }


    private static string CleanLibraryName(string name)
    {
        return name.EndsWith(".library", StringComparison.OrdinalIgnoreCase) ? name[..^".library".Length] : name;
    }


    private static string TrimInfoSuffix(string name)
    {
        return name.EndsWith(".info", StringComparison.OrdinalIgnoreCase) ? name[..^".info".Length] : name;
    }


    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var normalized = extension.Trim();
        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }
}
