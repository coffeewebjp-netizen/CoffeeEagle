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
        IProgress<LibrarySyncProgress>? progress = null,
        bool allowAssetReuse = true,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new LibrarySyncProgress("同期フォルダーへ接続中", treeUriString, Detail: "Storage Access Framework"));
        _documents.RequestProviderRefresh(treeUriString);
        var root = _documents.GetRoot(treeUriString);
        progress?.Report(new LibrarySyncProgress("ルート一覧を取得中", root.Name, Detail: "DocumentProvider"));
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
            progress?.Report(new LibrarySyncProgress("フォルダー構成を取得中", rootMetadata.Name, Detail: "ライブラリmetadata.json"));
            await using var stream = _documents.OpenRead(rootMetadata.Uri);
            using var metadata = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            libraryName = ReadString(metadata.RootElement, "name", "title", "libraryName") ?? libraryName;
            folders.AddRange(ReadFolders(metadata.RootElement));
        }

        NormalizeFolderPaths(folders);
        if (mtimeEntry is not null)
        {
            progress?.Report(new LibrarySyncProgress("更新一覧を取得中", mtimeEntry.Name, Detail: "変更件数を確認"));
        }

        var mtimeIndex = await ReadMtimeIndexAsync(mtimeEntry, cancellationToken);
        var sourceIndexModifiedStamp = mtimeEntry?.LastModified ?? 0;
        progress?.Report(new LibrarySyncProgress(
            "メディア情報を索引化中",
            imagesDirectory.Name,
            0,
            ResolveExpectedAssetCount(mtimeIndex),
            "DocumentProvider"));
        var scan = await ScanAssetsAsync(
            treeUriString,
            imagesDirectory,
            mtimeIndex,
            sourceIndexModifiedStamp,
            previous,
            allowAssetReuse,
            progress,
            cancellationToken);
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
            IndexFormatVersion = EagleLibrary.CurrentIndexFormatVersion,
            SourceIndexModifiedStamp = sourceIndexModifiedStamp,
            Folders = folders
                .OrderBy(folder => folder.Path, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            Assets = assets
                .OrderByDescending(asset => asset.ModifiedAt ?? asset.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SourceEntries = scan.SourceEntries
                .OrderBy(entry => entry.SourceInfoId, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public bool IsGoogleDriveTree(string treeUriString) => _documents.IsGoogleDriveTree(treeUriString);

    private async Task<AssetScanResult> ScanAssetsAsync(
        string treeUriString,
        DocumentEntry imagesDirectory,
        MtimeIndex mtimeIndex,
        long sourceIndexModifiedStamp,
        EagleLibrary? previous,
        bool allowAssetReuse,
        IProgress<LibrarySyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var scan = new AssetScanResult
        {
            MtimeFound = mtimeIndex.Found,
            MtimeReadFailed = mtimeIndex.ReadFailed,
            MtimeAssetIds = mtimeIndex.AssetModifiedAt.Count,
            MtimeDeclaredTotal = mtimeIndex.DeclaredTotal
        };
        var hasCurrentSourceSnapshot = previous is not null
            && previous.IndexFormatVersion == EagleLibrary.CurrentIndexFormatVersion
            && previous.SourceEntries is not null;
        var previousSources = hasCurrentSourceSnapshot
            ? CreatePreviousSourceLookup(previous!)
            : new Dictionary<string, EagleSourceEntry>(StringComparer.OrdinalIgnoreCase);
        var previousAssets = CreatePreviousAssetLookup(previous);
        var sourceIndexUnchanged = hasCurrentSourceSnapshot
            && !mtimeIndex.ReadFailed
            && sourceIndexModifiedStamp > 0
            && previous?.SourceIndexModifiedStamp == sourceIndexModifiedStamp;
        var infoDirectories = new Dictionary<string, DocumentEntry>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Queue<DocumentEntry>();
        pendingDirectories.Enqueue(imagesDirectory);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Dequeue();
            scan.VisitedDirectories++;
            progress?.Report(new LibrarySyncProgress(
                "フォルダー一覧を取得中",
                directory.Name,
                Detail: $"確認済みフォルダー {scan.VisitedDirectories:N0} / 残り {pendingDirectories.Count:N0}"));
            IReadOnlyList<DocumentEntry> children;
            try
            {
                children = _documents.ListChildren(treeUriString, directory.DocumentId);
            }
            catch
            {
                scan.DiscoveryReadFailures++;
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
                    var infoId = TrimInfoSuffix(child.Name);
                    if (!string.IsNullOrWhiteSpace(infoId) && infoDirectories.TryAdd(infoId, child))
                    {
                        scan.InfoDirectories++;
                    }
                }
                else
                {
                    if (IsEagleTrashDirectory(child.Name))
                    {
                        scan.TrashDirectories++;
                        continue;
                    }

                    pendingDirectories.Enqueue(child);
                }
            }
        }

        if (scan.DiscoveryReadFailures > 0)
        {
            throw new InvalidOperationException("imagesフォルダーの一覧を完全に取得できなかったため、索引を更新しませんでした。");
        }

        RecoverMissingMtimeInfoDirectories(
            treeUriString,
            imagesDirectory,
            mtimeIndex,
            infoDirectories,
            scan,
            progress,
            cancellationToken);

        var missingPreviousSources = previousSources.Values
            .Where(entry => !infoDirectories.ContainsKey(entry.SourceInfoId))
            .OrderBy(entry => entry.SourceInfoId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        scan.Total = infoDirectories.Count + missingPreviousSources.Count;
        progress?.Report(new LibrarySyncProgress(
            "差分を確認中",
            $"{infoDirectories.Count:N0} 件を検出 / 消失 {missingPreviousSources.Count:N0} 件",
            0,
            Math.Max(1, scan.Total),
            sourceIndexUnchanged ? "mtime.json 変更なし" : "追加・変更・削除を照合"));

        foreach (var pair in infoDirectories.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var infoId = pair.Key;
            var infoDirectory = pair.Value;
            previousSources.TryGetValue(infoId, out var previousSource);
            var sourceModifiedStamp = GetMtimeStamp(mtimeIndex, infoId);
            if (allowAssetReuse
                && !mtimeIndex.ReadFailed
                && previousSource is not null
                && TryReusePreviousSource(
                    infoId,
                    sourceModifiedStamp,
                    sourceIndexUnchanged,
                    previousSource,
                    previousAssets,
                    out var reusedSource,
                    out var reusedAsset))
            {
                scan.SourceEntries.Add(reusedSource);
                if (reusedAsset is not null)
                {
                    scan.Assets.Add(reusedAsset);
                    scan.ReusedAssets++;
                }

                scan.Unchanged++;
            }
            else
            {
                var readResult = await TryReadAssetAsync(
                    treeUriString,
                    infoDirectory,
                    scan,
                    sourceModifiedStamp,
                    cancellationToken);
                scan.SourceEntries.Add(new EagleSourceEntry
                {
                    SourceInfoId = infoId,
                    SourceModifiedStamp = sourceModifiedStamp,
                    State = readResult.State
                });

                var hasPreviousAsset = previousAssets.TryGetValue(infoId, out var previousAsset);
                if (readResult.Asset is not null)
                {
                    scan.Assets.Add(readResult.Asset);
                    if (hasPreviousAsset)
                    {
                        scan.Changed++;
                    }
                    else
                    {
                        scan.Added++;
                    }
                }
                else if (string.Equals(readResult.State, EagleSourceEntryState.Deleted, StringComparison.Ordinal))
                {
                    if (hasPreviousAsset)
                    {
                        scan.Deleted++;
                    }
                }
                else
                {
                    scan.RetryPending++;
                    if (hasPreviousAsset)
                    {
                        scan.Assets.Add(CloneAsset(previousAsset!));
                        scan.PreservedAssets++;
                    }
                }
            }

            scan.Processed++;
            ReportScanProgress(scan, infoDirectory.Name, progress);
        }

        foreach (var previousSource in missingPreviousSources)
        {
            if (previousAssets.ContainsKey(previousSource.SourceInfoId))
            {
                scan.Deleted++;
            }

            scan.Processed++;
            ReportScanProgress(scan, previousSource.SourceInfoId + ".info (消失)", progress);
        }

        var finalTotal = Math.Max(1, scan.Total);
        progress?.Report(new LibrarySyncProgress(
            "同期完了",
            $"{scan.Processed:N0} / {scan.Total:N0} .info",
            finalTotal,
            finalTotal,
            scan.ToDifferenceMessage()));

        return scan;
    }

    private void RecoverMissingMtimeInfoDirectories(
        string treeUriString,
        DocumentEntry imagesDirectory,
        MtimeIndex mtimeIndex,
        IDictionary<string, DocumentEntry> infoDirectories,
        AssetScanResult scan,
        IProgress<LibrarySyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (mtimeIndex.AssetModifiedAt.Count == 0)
        {
            return;
        }

        var missingIds = mtimeIndex.AssetModifiedAt.Keys
            .Where(id => !infoDirectories.ContainsKey(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        scan.MtimeMissingIds = missingIds.Count;
        if (missingIds.Count == 0)
        {
            return;
        }

        progress?.Report(new LibrarySyncProgress(
            "mtime差分を確認中",
            $"未解決 {missingIds.Count:N0} 件",
            Detail: ".infoフォルダーを直接検索"));
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
            if (infoDirectories.TryAdd(assetId, infoDirectory))
            {
                scan.InfoDirectories++;
                scan.MtimeRecoveredIds++;
            }
        }
    }

    private static bool IsEagleTrashDirectory(string name)
    {
        var normalized = name.Trim().Trim('.', '_', '-').Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "trash" or "trashed" or "recycle" or "recycled" or "recyclebin" or "deleted" or "deleteditems";
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

    private static Dictionary<string, EagleSourceEntry> CreatePreviousSourceLookup(EagleLibrary previous)
    {
        var lookup = new Dictionary<string, EagleSourceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in previous.SourceEntries ?? [])
        {
            if (!string.IsNullOrWhiteSpace(entry.SourceInfoId) && !lookup.ContainsKey(entry.SourceInfoId))
            {
                lookup[entry.SourceInfoId] = entry;
            }
        }

        return lookup;
    }

    private static bool TryReusePreviousSource(
        string infoId,
        long sourceModifiedStamp,
        bool sourceIndexUnchanged,
        EagleSourceEntry previousSource,
        IReadOnlyDictionary<string, EagleAsset> previousAssets,
        out EagleSourceEntry sourceEntry,
        out EagleAsset? asset)
    {
        sourceEntry = null!;
        asset = null;
        var stampMatches = sourceModifiedStamp > 0
            ? previousSource.SourceModifiedStamp == sourceModifiedStamp
            : sourceIndexUnchanged;
        var isActive = string.Equals(previousSource.State, EagleSourceEntryState.Active, StringComparison.Ordinal);
        var isDeleted = string.Equals(previousSource.State, EagleSourceEntryState.Deleted, StringComparison.Ordinal);
        if (!stampMatches || (!isActive && !isDeleted))
        {
            return false;
        }

        if (isActive)
        {
            if (!previousAssets.TryGetValue(infoId, out var previousAsset))
            {
                return false;
            }

            asset = CloneAsset(previousAsset);
            asset.SourceInfoId = infoId;
            if (sourceModifiedStamp > 0)
            {
                asset.SourceModifiedStamp = sourceModifiedStamp;
            }
        }

        sourceEntry = CloneSourceEntry(previousSource);
        return true;
    }

    private static EagleSourceEntry CloneSourceEntry(EagleSourceEntry source)
    {
        return new EagleSourceEntry
        {
            SourceInfoId = source.SourceInfoId,
            SourceModifiedStamp = source.SourceModifiedStamp,
            State = source.State
        };
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

    private static int? ResolveExpectedAssetCount(MtimeIndex mtimeIndex)
    {
        if (mtimeIndex.DeclaredTotal > 0)
        {
            return mtimeIndex.DeclaredTotal;
        }

        return mtimeIndex.AssetModifiedAt.Count > 0 ? mtimeIndex.AssetModifiedAt.Count : null;
    }

    private static void ReportScanProgress(
        AssetScanResult scan,
        string target,
        IProgress<LibrarySyncProgress>? progress)
    {
        if (progress is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (scan.Processed < scan.Total
            && scan.Processed % 25 != 0
            && now - scan.LastProgressAt < 250)
        {
            return;
        }

        scan.LastProgressAt = now;
        progress.Report(new LibrarySyncProgress(
            "差分を更新中",
            target,
            scan.Processed,
            Math.Max(1, scan.Total),
            scan.ToDifferenceMessage()));
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

    private async Task<AssetReadResult> TryReadAssetAsync(
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
            return AssetReadResult.ReadFailed;
        }

        var metadataEntry = children.FirstOrDefault(IsMetadataFile);
        if (metadataEntry is null)
        {
            scan.MetadataMissing++;
            return AssetReadResult.MissingMetadata;
        }

        scan.MetadataFiles++;
        try
        {
            await using var stream = _documents.OpenRead(metadataEntry.Uri);
            using var metadata = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = metadata.RootElement;
            if (ReadBoolean(root, "isDeleted", "deleted"))
            {
                return AssetReadResult.Deleted;
            }

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

            return AssetReadResult.Active(new EagleAsset
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
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            scan.AssetReadFailures++;
            return AssetReadResult.ReadFailed;
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
                    assets[TrimInfoSuffix(property.Name)] = mtime;
                }
            }

            return new MtimeIndex(true, false, assets, declaredTotal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new MtimeIndex(true, true, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), 0);
        }
    }

    private sealed record AssetReadResult(EagleAsset? Asset, string State)
    {
        public static AssetReadResult Active(EagleAsset asset) => new(asset, EagleSourceEntryState.Active);

        public static AssetReadResult Deleted { get; } = new(null, EagleSourceEntryState.Deleted);

        public static AssetReadResult MissingMetadata { get; } = new(null, EagleSourceEntryState.MissingMetadata);

        public static AssetReadResult ReadFailed { get; } = new(null, EagleSourceEntryState.ReadFailed);
    }

    private sealed class AssetScanResult
    {
        public List<EagleAsset> Assets { get; } = [];
        public List<EagleSourceEntry> SourceEntries { get; } = [];
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Deleted { get; set; }
        public int Unchanged { get; set; }
        public int Processed { get; set; }
        public int Total { get; set; }
        public int PreservedAssets { get; set; }
        public int RetryPending { get; set; }
        public long LastProgressAt { get; set; }
        public int VisitedDirectories { get; set; }
        public int DiscoveryReadFailures { get; set; }
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
        public int TrashDirectories { get; set; }

        public string ToDifferenceMessage()
        {
            return $"追加 {Added:N0} / 変更 {Changed:N0} / 削除 {Deleted:N0} / 変更なし {Unchanged:N0} / 一時保持 {PreservedAssets:N0} / 再試行 {RetryPending:N0}";
        }

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

            var trashMessage = TrashDirectories > 0 ? $", trash {TrashDirectories}" : string.Empty;
            return $"{Assets.Count:N0} 件を索引化 / {ToDifferenceMessage()} / dirs {VisitedDirectories}, .info {InfoDirectories}, metadata {MetadataFiles}, reused {ReusedAssets}, images {ImageFiles}, media {MediaFiles}, read-fail {DiscoveryReadFailures + DirectoryReadFailures + AssetReadFailures}, metadata-missing {MetadataMissing}{mtimeMessage}{trashMessage}";
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

    private static bool ReadBoolean(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number != 0;
        }

        return property.ValueKind == JsonValueKind.String
            && bool.TryParse(property.GetString(), out var parsed)
            && parsed;
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
