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

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".alac", ".ape", ".amr", ".mid", ".midi"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".wmv", ".mpeg", ".mpg", ".3gp", ".ts", ".mts", ".m2ts", ".flv", ".ogv"
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
        _documents.RequestProviderRefresh(treeUriString);
        var root = _documents.GetRoot(treeUriString);
        var rootChildren = _documents.ListChildren(treeUriString, root.DocumentId);
        var rootMetadata = rootChildren.FirstOrDefault(IsMetadataFile);
        var mtimeEntry = rootChildren.FirstOrDefault(child =>
            !child.IsDirectory && string.Equals(child.Name, "mtime.json", StringComparison.OrdinalIgnoreCase));
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
        var mtimeIndex = await ReadMtimeIndexAsync(mtimeEntry, cancellationToken);
        progress?.Report("メディア情報を索引化中...");
        var scan = await ScanAssetsAsync(treeUriString, imagesDirectory, mtimeIndex, previous, progress, cancellationToken);
        var assets = scan.Assets;
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
            IndexMessage = scan.ToMessage(),
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

    private async Task<AssetScanResult> ScanAssetsAsync(
        string treeUriString,
        DocumentEntry imagesDirectory,
        MtimeIndex mtimeIndex,
        EagleLibrary? previous,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var scan = new AssetScanResult
        {
            MtimeFound = mtimeIndex.Found,
            MtimeReadFailed = mtimeIndex.ReadFailed,
            MtimeAssetIds = mtimeIndex.AssetModifiedAt.Count,
            MtimeDeclaredTotal = mtimeIndex.DeclaredTotal
        };
        var assets = scan.Assets;
        var previousAssets = CreatePreviousAssetLookup(previous);
        var seenInfoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Queue<DocumentEntry>();
        pendingDirectories.Enqueue(imagesDirectory);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Dequeue();
            scan.VisitedDirectories++;
            IReadOnlyList<DocumentEntry> children;
            try
            {
                children = _documents.ListChildren(treeUriString, directory.DocumentId);
            }
            catch
            {
                scan.DirectoryReadFailures++;
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
                    scan.InfoDirectories++;
                    var infoId = TrimInfoSuffix(child.Name);
                    seenInfoIds.Add(infoId);
                    if (TryReusePreviousAsset(infoId, mtimeIndex, previousAssets, out var reusedAsset))
                    {
                        scan.ReusedAssets++;
                        assets.Add(reusedAsset);
                        ReportAssetProgress(assets.Count, scan.ReusedAssets, progress, "索引化中");
                        continue;
                    }

                    var asset = await TryReadAssetAsync(treeUriString, child, scan, GetMtimeStamp(mtimeIndex, infoId), cancellationToken);
                    if (asset is not null)
                    {
                        assets.Add(asset);
                        ReportAssetProgress(assets.Count, scan.ReusedAssets, progress, "索引化中");
                    }
                }
                else
                {
                    pendingDirectories.Enqueue(child);
                }
            }
        }

        await RecoverMissingMtimeAssetsAsync(
            treeUriString,
            imagesDirectory,
            mtimeIndex,
            seenInfoIds,
            scan,
            progress,
            cancellationToken);

        progress?.Report(scan.ToMessage());
        return scan;
    }

    private async Task RecoverMissingMtimeAssetsAsync(
        string treeUriString,
        DocumentEntry imagesDirectory,
        MtimeIndex mtimeIndex,
        HashSet<string> seenInfoIds,
        AssetScanResult scan,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (mtimeIndex.AssetModifiedAt.Count == 0)
        {
            return;
        }

        var missingIds = mtimeIndex.AssetModifiedAt.Keys
            .Where(id => !seenInfoIds.Contains(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        scan.MtimeMissingIds = missingIds.Count;
        if (missingIds.Count == 0)
        {
            return;
        }

        progress?.Report($"mtime差分を確認中... unresolved {missingIds.Count}");
        foreach (var assetId in missingIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var infoDirectory = FindInfoDirectoryById(treeUriString, imagesDirectory.DocumentId, assetId);
            if (infoDirectory is null)
            {
                scan.MtimeDirectFailures++;
                continue;
            }

            scan.MtimeDirectHits++;
            scan.InfoDirectories++;
            seenInfoIds.Add(assetId);
            var asset = await TryReadAssetAsync(treeUriString, infoDirectory, scan, GetMtimeStamp(mtimeIndex, assetId), cancellationToken);
            if (asset is not null)
            {
                scan.Assets.Add(asset);
                scan.MtimeRecoveredIds++;
            }
        }
    }

    private static Dictionary<string, EagleAsset> CreatePreviousAssetLookup(EagleLibrary? previous)
    {
        var lookup = new Dictionary<string, EagleAsset>(StringComparer.OrdinalIgnoreCase);
        if (previous?.Assets is null)
        {
            return lookup;
        }

        foreach (var asset in previous.Assets)
        {
            AddPreviousAsset(lookup, asset.SourceInfoId, asset);
            AddPreviousAsset(lookup, asset.Id, asset);
        }

        return lookup;
    }

    private static void AddPreviousAsset(Dictionary<string, EagleAsset> lookup, string? key, EagleAsset asset)
    {
        if (!string.IsNullOrWhiteSpace(key) && !lookup.ContainsKey(key))
        {
            lookup[key] = asset;
        }
    }

    private static bool TryReusePreviousAsset(
        string infoId,
        MtimeIndex mtimeIndex,
        IReadOnlyDictionary<string, EagleAsset> previousAssets,
        out EagleAsset asset)
    {
        asset = null!;
        if (!mtimeIndex.Found
            || mtimeIndex.ReadFailed
            || !mtimeIndex.AssetModifiedAt.TryGetValue(infoId, out var sourceModifiedStamp)
            || sourceModifiedStamp <= 0
            || !previousAssets.TryGetValue(infoId, out var previous)
            || previous.SourceModifiedStamp != sourceModifiedStamp)
        {
            return false;
        }

        asset = CloneAsset(previous);
        asset.SourceInfoId = string.IsNullOrWhiteSpace(asset.SourceInfoId) ? infoId : asset.SourceInfoId;
        asset.SourceModifiedStamp = sourceModifiedStamp;
        return true;
    }

    private static EagleAsset CloneAsset(EagleAsset source)
    {
        return new EagleAsset
        {
            Id = source.Id,
            Name = source.Name,
            FileName = source.FileName,
            Extension = source.Extension,
            FileUri = source.FileUri,
            ThumbnailUri = source.ThumbnailUri,
            MediaKind = source.MediaKind,
            SourceInfoId = source.SourceInfoId,
            SourceModifiedStamp = source.SourceModifiedStamp,
            FolderIds = source.FolderIds.ToList(),
            Tags = source.Tags.ToList(),
            SourceUrl = source.SourceUrl,
            Annotation = source.Annotation,
            SizeBytes = source.SizeBytes,
            Width = source.Width,
            Height = source.Height,
            CreatedAt = source.CreatedAt,
            ModifiedAt = source.ModifiedAt
        };
    }

    private static long GetMtimeStamp(MtimeIndex mtimeIndex, string infoId)
    {
        return mtimeIndex.AssetModifiedAt.TryGetValue(infoId, out var sourceModifiedStamp)
            ? sourceModifiedStamp
            : 0;
    }

    private static void ReportAssetProgress(int assetCount, int reusedAssets, IProgress<string>? progress, string message)
    {
        if (assetCount > 0 && assetCount % 50 == 0)
        {
            progress?.Report($"{assetCount} 件を{message}... reused {reusedAssets}");
        }
    }

    private DocumentEntry? FindInfoDirectoryById(string treeUriString, string imagesDocumentId, string assetId)
    {
        foreach (var documentId in BuildInfoDirectoryDocumentIdCandidates(imagesDocumentId, assetId))
        {
            var entry = _documents.TryGetDocument(treeUriString, documentId);
            if (entry is not null && entry.IsDirectory)
            {
                return entry;
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildInfoDirectoryDocumentIdCandidates(string parentDocumentId, string assetId)
    {
        var infoName = assetId.EndsWith(".info", StringComparison.OrdinalIgnoreCase)
            ? assetId
            : assetId + ".info";
        if (parentDocumentId.EndsWith('/') || parentDocumentId.EndsWith(':'))
        {
            yield return parentDocumentId + infoName;
        }
        else
        {
            yield return parentDocumentId + "/" + infoName;
        }

        if (!parentDocumentId.EndsWith(':'))
        {
            yield return parentDocumentId + ":" + infoName;
        }
    }

    private async Task<EagleAsset?> TryReadAssetAsync(
        string treeUriString,
        DocumentEntry infoDirectory,
        AssetScanResult scan,
        long sourceModifiedStamp,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DocumentEntry> children;
        try
        {
            children = _documents.ListChildren(treeUriString, infoDirectory.DocumentId);
        }
        catch
        {
            scan.DirectoryReadFailures++;
            return null;
        }

        var metadataEntry = children.FirstOrDefault(IsMetadataFile);
        if (metadataEntry is null)
        {
            scan.MetadataMissing++;
            return null;
        }

        scan.MetadataFiles++;
        try
        {
            await using var stream = _documents.OpenRead(metadataEntry.Uri);
            using var metadata = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = metadata.RootElement;
            var files = children.Where(child => !child.IsDirectory && !IsMetadataFile(child)).ToList();
            var imageFiles = files.Where(IsImageFile).ToList();
            var mediaFiles = files.Where(IsSupportedMediaFile).ToList();
            scan.ImageFiles += imageFiles.Count;
            scan.MediaFiles += mediaFiles.Count;
            var thumbnail = SelectThumbnail(imageFiles);
            var primaryFile = SelectPrimaryMedia(files, mediaFiles, thumbnail, root);
            var mediaKind = ResolveMediaKind(primaryFile, root);
            var id = ReadString(root, "id", "uuid") ?? TrimInfoSuffix(infoDirectory.Name);
            var fileName = ReadString(root, "fileName", "filename")
                ?? primaryFile?.Name
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
                FileUri = primaryFile?.Uri ?? thumbnail?.Uri,
                ThumbnailUri = thumbnail?.Uri ?? (mediaKind == EagleAssetMediaKind.Image ? primaryFile?.Uri : null),
                MediaKind = mediaKind,
                SourceInfoId = TrimInfoSuffix(infoDirectory.Name),
                SourceModifiedStamp = sourceModifiedStamp,
                FolderIds = ReadStringArray(root, "folders", "folderIds", "folderId")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Tags = ReadStringArray(root, "tags", "tagNames")
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                SourceUrl = ReadString(root, "url", "source", "sourceUrl"),
                Annotation = ReadString(root, "annotation", "note", "description"),
                SizeBytes = primaryFile?.Size ?? thumbnail?.Size ?? ReadLong(root, "size", "sizeBytes"),
                Width = (int)ReadLong(root, "width"),
                Height = (int)ReadLong(root, "height"),
                CreatedAt = ReadDate(root, "btime", "createdAt", "createTime", "birthTime"),
                ModifiedAt = ReadDate(root, "mtime", "modifiedAt", "modificationTime", "updatedAt")
            };
        }
        catch
        {
            scan.AssetReadFailures++;
            return null;
        }
    }

    private async Task<MtimeIndex> ReadMtimeIndexAsync(DocumentEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return MtimeIndex.Empty;
        }

        try
        {
            var assets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var declaredTotal = 0;
            await using var stream = _documents.OpenRead(entry.Uri);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new MtimeIndex(true, true, assets, 0);
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "all", StringComparison.OrdinalIgnoreCase))
                {
                    declaredTotal = (int)ReadLongValue(property.Value);
                    continue;
                }

                var mtime = ReadLongValue(property.Value);
                if (!string.IsNullOrWhiteSpace(property.Name) && mtime > 0)
                {
                    assets[property.Name] = mtime;
                }
            }

            return new MtimeIndex(true, false, assets, declaredTotal);
        }
        catch
        {
            return new MtimeIndex(true, true, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), 0);
        }
    }

    private sealed class AssetScanResult
    {
        public List<EagleAsset> Assets { get; } = [];
        public int VisitedDirectories { get; set; }
        public int DirectoryReadFailures { get; set; }
        public int InfoDirectories { get; set; }
        public int MetadataFiles { get; set; }
        public int MetadataMissing { get; set; }
        public int ImageFiles { get; set; }
        public int MediaFiles { get; set; }
        public int AssetReadFailures { get; set; }
        public int ReusedAssets { get; set; }
        public bool MtimeFound { get; set; }
        public bool MtimeReadFailed { get; set; }
        public int MtimeAssetIds { get; set; }
        public int MtimeDeclaredTotal { get; set; }
        public int MtimeMissingIds { get; set; }
        public int MtimeDirectHits { get; set; }
        public int MtimeDirectFailures { get; set; }
        public int MtimeRecoveredIds { get; set; }

        public string ToMessage()
        {
            var mtimeMessage = ", mtime none";
            if (MtimeFound)
            {
                var mtimeTotal = MtimeDeclaredTotal > 0 ? MtimeDeclaredTotal : MtimeAssetIds;
                var unresolved = Math.Max(0, MtimeMissingIds - MtimeRecoveredIds);
                mtimeMessage = unresolved > 0 || MtimeRecoveredIds > 0
                    ? $", mtime {mtimeTotal}, unresolved {unresolved}, recovered {MtimeRecoveredIds}"
                    : $", mtime {mtimeTotal}, ok";
            }

            if (MtimeReadFailed)
            {
                mtimeMessage += ", mtime-read-fail";
            }

            return $"{Assets.Count} 件を索引化 / dirs {VisitedDirectories}, .info {InfoDirectories}, metadata {MetadataFiles}, reused {ReusedAssets}, images {ImageFiles}, media {MediaFiles}, read-fail {DirectoryReadFailures + AssetReadFailures}, metadata-missing {MetadataMissing}{mtimeMessage}";
        }
    }

    private sealed record MtimeIndex(
        bool Found,
        bool ReadFailed,
        IReadOnlyDictionary<string, long> AssetModifiedAt,
        int DeclaredTotal)
    {
        public static MtimeIndex Empty { get; } = new(false, false, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), 0);
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

    private static DocumentEntry? SelectPrimaryMedia(
        IReadOnlyList<DocumentEntry> allFiles,
        IReadOnlyList<DocumentEntry> supportedFiles,
        DocumentEntry? thumbnail,
        JsonElement metadata)
    {
        var candidates = supportedFiles
            .Where(file => thumbnail is null || !string.Equals(file.DocumentId, thumbnail.DocumentId, StringComparison.Ordinal))
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
            .Where(file => thumbnail is null || !string.Equals(file.DocumentId, thumbnail.DocumentId, StringComparison.Ordinal))
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

    private static bool IsMetadataFile(DocumentEntry entry)
    {
        return !entry.IsDirectory && string.Equals(entry.Name, "metadata.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedMediaFile(DocumentEntry entry)
    {
        return IsImageFile(entry) || IsAudioFile(entry) || IsVideoFile(entry);
    }

    private static bool IsImageFile(DocumentEntry entry)
    {
        if (entry.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ImageExtensions.Contains(Path.GetExtension(entry.Name));
    }

    private static bool IsAudioFile(DocumentEntry entry)
    {
        if (entry.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return AudioExtensions.Contains(Path.GetExtension(entry.Name));
    }

    private static bool IsVideoFile(DocumentEntry entry)
    {
        if (entry.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return VideoExtensions.Contains(Path.GetExtension(entry.Name));
    }

    private static bool IsLikelyThumbnail(DocumentEntry entry)
    {
        return entry.Name.Contains("thumb", StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
            || entry.Name.StartsWith("cover", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownMediaExtension(string? extension)
    {
        return !string.IsNullOrWhiteSpace(extension)
            && (ImageExtensions.Contains(extension)
                || AudioExtensions.Contains(extension)
                || VideoExtensions.Contains(extension));
    }

    private static string ResolveMediaKind(DocumentEntry? primaryFile, JsonElement metadata)
    {
        var extension = NormalizeExtension(ReadString(metadata, "ext", "extension") ?? Path.GetExtension(primaryFile?.Name ?? string.Empty));
        if (primaryFile is not null)
        {
            if (IsAudioFile(primaryFile))
            {
                return EagleAssetMediaKind.Audio;
            }

            if (IsVideoFile(primaryFile))
            {
                return EagleAssetMediaKind.Video;
            }

            if (IsImageFile(primaryFile))
            {
                return EagleAssetMediaKind.Image;
            }
        }

        if (!string.IsNullOrWhiteSpace(extension))
        {
            if (AudioExtensions.Contains(extension))
            {
                return EagleAssetMediaKind.Audio;
            }

            if (VideoExtensions.Contains(extension))
            {
                return EagleAssetMediaKind.Video;
            }

            if (ImageExtensions.Contains(extension))
            {
                return EagleAssetMediaKind.Image;
            }
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


