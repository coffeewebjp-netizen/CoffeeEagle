using System.Globalization;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class EagleLibraryIndexer
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic", ".heif"
    };

    private readonly AndroidDocumentTreeService _documents;

    public EagleLibraryIndexer(AndroidDocumentTreeService documents)
    {
        _documents = documents;
    }

    public async Task<EagleLibrary> IndexAsync(
        string treeUriString,
        EagleLibrary? previous = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = _documents.GetRoot(treeUriString);
        var rootChildren = _documents.ListChildren(treeUriString, root.DocumentId);
        var rootMetadata = rootChildren.FirstOrDefault(IsMetadataFile);
        var imagesDirectory = rootChildren.FirstOrDefault(child =>
            child.IsDirectory && string.Equals(child.Name, "images", StringComparison.OrdinalIgnoreCase));

        if (imagesDirectory is null)
        {
            throw new InvalidOperationException("EAGLEライブラリの images フォルダが見つかりません。");
        }

        var libraryName = CleanLibraryName(root.Name);
        var folders = new List<EagleFolder>();
        if (rootMetadata is not null)
        {
            progress?.Report("フォルダ情報を読み込み中...");
            await using var stream = _documents.OpenRead(rootMetadata.Uri);
            using var metadata = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            libraryName = ReadString(metadata.RootElement, "name", "title", "libraryName") ?? libraryName;
            folders.AddRange(ReadFolders(metadata.RootElement));
        }

        NormalizeFolderPaths(folders);
        progress?.Report("画像情報を索引化中...");
        var assets = await ScanAssetsAsync(treeUriString, imagesDirectory, progress, cancellationToken);
        EnsureReferencedFolders(folders, assets);
        NormalizeFolderPaths(folders);

        return new EagleLibrary
        {
            Id = previous?.Id ?? Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(libraryName) ? "EAGLE Library" : libraryName,
            SourceKind = _documents.GetSourceKind(treeUriString),
            SourceLabel = _documents.GetSourceLabel(treeUriString),
            TreeUri = treeUriString,
            RootDocumentId = root.DocumentId,
            IndexedAt = DateTimeOffset.UtcNow,
            Folders = folders
                .OrderBy(folder => folder.Path, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            Assets = assets
                .OrderByDescending(asset => asset.ModifiedAt ?? asset.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
        };
    }

    public bool IsGoogleDriveTree(string treeUriString) => _documents.IsGoogleDriveTree(treeUriString);

    private async Task<List<EagleAsset>> ScanAssetsAsync(
        string treeUriString,
        DocumentEntry imagesDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var assets = new List<EagleAsset>();
        var pendingDirectories = new Queue<DocumentEntry>();
        pendingDirectories.Enqueue(imagesDirectory);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Dequeue();
            IReadOnlyList<DocumentEntry> children;
            try
            {
                children = _documents.ListChildren(treeUriString, directory.DocumentId);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!child.IsDirectory)
                {
                    continue;
                }

                if (child.Name.EndsWith(".info", StringComparison.OrdinalIgnoreCase))
                {
                    var asset = await TryReadAssetAsync(treeUriString, child, cancellationToken);
                    if (asset is not null)
                    {
                        assets.Add(asset);
                        if (assets.Count % 50 == 0)
                        {
                            progress?.Report($"{assets.Count} 件を索引化中...");
                        }
                    }
                }
                else
                {
                    pendingDirectories.Enqueue(child);
                }
            }
        }

        progress?.Report($"{assets.Count} 件を索引化しました");
        return assets;
    }

    private async Task<EagleAsset?> TryReadAssetAsync(
        string treeUriString,
        DocumentEntry infoDirectory,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DocumentEntry> children;
        try
        {
            children = _documents.ListChildren(treeUriString, infoDirectory.DocumentId);
        }
        catch
        {
            return null;
        }

        var metadataEntry = children.FirstOrDefault(IsMetadataFile);
        if (metadataEntry is null)
        {
            return null;
        }

        try
        {
            await using var stream = _documents.OpenRead(metadataEntry.Uri);
            using var metadata = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = metadata.RootElement;
            var imageFiles = children.Where(child => !child.IsDirectory && IsImageFile(child)).ToList();
            var thumbnail = SelectThumbnail(imageFiles);
            var fullImage = SelectFullImage(imageFiles, thumbnail);
            var id = ReadString(root, "id", "uuid") ?? TrimInfoSuffix(infoDirectory.Name);
            var fileName = ReadString(root, "fileName", "filename")
                ?? fullImage?.Name
                ?? thumbnail?.Name
                ?? string.Empty;
            var name = ReadString(root, "name", "title")
                ?? Path.GetFileNameWithoutExtension(fileName)
                ?? TrimInfoSuffix(infoDirectory.Name);
            var extension = NormalizeExtension(ReadString(root, "ext", "extension") ?? Path.GetExtension(fileName));

            return new EagleAsset
            {
                Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                Name = string.IsNullOrWhiteSpace(name) ? TrimInfoSuffix(infoDirectory.Name) : name,
                FileName = fileName,
                Extension = extension,
                FileUri = fullImage?.Uri ?? thumbnail?.Uri,
                ThumbnailUri = thumbnail?.Uri ?? fullImage?.Uri,
                FolderIds = ReadStringArray(root, "folders", "folderIds", "folderId")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Tags = ReadStringArray(root, "tags", "tagNames")
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                SourceUrl = ReadString(root, "url", "source", "sourceUrl"),
                Annotation = ReadString(root, "annotation", "note", "description"),
                SizeBytes = fullImage?.Size ?? thumbnail?.Size ?? ReadLong(root, "size", "sizeBytes"),
                Width = (int)ReadLong(root, "width"),
                Height = (int)ReadLong(root, "height"),
                CreatedAt = ReadDate(root, "btime", "createdAt", "createTime", "birthTime"),
                ModifiedAt = ReadDate(root, "mtime", "modifiedAt", "modificationTime", "updatedAt")
            };
        }
        catch
        {
            return null;
        }
    }

    private static DocumentEntry? SelectThumbnail(IReadOnlyList<DocumentEntry> files)
    {
        return files
            .Where(file => file.Name.Contains("thumb", StringComparison.OrdinalIgnoreCase)
                || file.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
                || file.Name.StartsWith("cover", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Size <= 0 ? long.MaxValue : file.Size)
            .FirstOrDefault();
    }

    private static DocumentEntry? SelectFullImage(IReadOnlyList<DocumentEntry> files, DocumentEntry? thumbnail)
    {
        return files
            .Where(file => thumbnail is null || !string.Equals(file.DocumentId, thumbnail.DocumentId, StringComparison.Ordinal))
            .OrderByDescending(file => file.Size)
            .FirstOrDefault()
            ?? thumbnail;
    }

    private static bool IsMetadataFile(DocumentEntry entry)
    {
        return !entry.IsDirectory && string.Equals(entry.Name, "metadata.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageFile(DocumentEntry entry)
    {
        if (entry.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ImageExtensions.Contains(Path.GetExtension(entry.Name));
    }

    private static List<EagleFolder> ReadFolders(JsonElement root)
    {
        if (!TryGetProperty(root, out var foldersElement, "folders"))
        {
            return [];
        }

        var folders = new List<EagleFolder>();
        ReadFolderElement(foldersElement, null, folders, 0);
        return folders
            .GroupBy(folder => folder.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static int ReadFolderElement(
        JsonElement element,
        string? parentId,
        List<EagleFolder> folders,
        int sortOrder)
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
            folders.Add(new EagleFolder
            {
                Id = id,
                Name = name,
                ParentId = parentId,
                SortOrder = sortOrder++
            });
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

            folders.Add(new EagleFolder
            {
                Id = folderId,
                Name = folderId,
                SortOrder = folders.Count
            });
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

    private static string BuildFolderPath(
        EagleFolder folder,
        IReadOnlyDictionary<string, EagleFolder> byId,
        HashSet<string> visited)
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
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value.Trim());
                }
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var value = ReadString(item, "id", "name", "title");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value.Trim());
                }
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
        return name.EndsWith(".library", StringComparison.OrdinalIgnoreCase)
            ? name[..^".library".Length]
            : name;
    }

    private static string TrimInfoSuffix(string name)
    {
        return name.EndsWith(".info", StringComparison.OrdinalIgnoreCase)
            ? name[..^".info".Length]
            : name;
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


