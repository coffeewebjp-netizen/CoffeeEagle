using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class GoogleDriveLibraryService
{

    private async Task<AssetScanResult> ScanAssetsAsync(
        EagleReaderState state,
        string imagesFolderId,
        MtimeIndex mtimeIndex,
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
        var canUsePreviousIndex = previous is not null
            && previous.IndexFormatVersion == EagleLibrary.CurrentIndexFormatVersion;
        var previousEntries = canUsePreviousIndex
            ? CreatePreviousSourceEntryLookup(previous)
            : new Dictionary<string, EagleSourceEntry>(StringComparer.OrdinalIgnoreCase);
        var previousAssets = CreatePreviousAssetLookup(previous);
        var sourceIndexUnchanged = canUsePreviousIndex
            && mtimeIndex.ModifiedStamp > 0
            && previous!.SourceIndexModifiedStamp == mtimeIndex.ModifiedStamp;
        var infoDirectories = new Dictionary<string, DriveEntry>(StringComparer.OrdinalIgnoreCase);
        var pendingFolders = new Queue<(string Id, string Name)>();
        pendingFolders.Enqueue((imagesFolderId, "images"));

        // The physical .info list is authoritative for existence. mtime.json is only a
        // change hint and is intentionally not used to exclude directories from this scan.
        while (pendingFolders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pendingFolders.Dequeue();
            scan.VisitedDirectories++;
            progress?.Report(new LibrarySyncProgress(
                "変更対象を探索中",
                folder.Name,
                0,
                infoDirectories.Count > 0 ? infoDirectories.Count : null,
                $"発見 .info {infoDirectories.Count:N0} / 確認済みフォルダー {scan.VisitedDirectories:N0}"));
            IReadOnlyList<DriveEntry> children;
            try
            {
                children = await ListChildrenAsync(state, folder.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                scan.DirectoryReadFailures++;
                scan.DiscoveryReadFailures++;
                continue;
            }

            foreach (var child in children)
            {
                if (!child.IsFolder)
                {
                    continue;
                }

                if (child.Name.EndsWith(".info", StringComparison.OrdinalIgnoreCase))
                {
                    var infoId = TrimInfoSuffix(child.Name);
                    infoDirectories.TryAdd(infoId, child);
                }
                else
                {
                    if (IsEagleTrashDirectory(child.Name))
                    {
                        scan.TrashDirectories++;
                        continue;
                    }

                    pendingFolders.Enqueue((child.Id, child.Name));
                }
            }
        }

        scan.InfoDirectories = infoDirectories.Count;
        if (scan.DiscoveryReadFailures > 0)
        {
            throw new InvalidOperationException(".info一覧の一部を取得できなかったため、安全のため更新を中止しました。通信状態を確認して再試行してください。");
        }

        scan.MtimeMissingIds = mtimeIndex.AssetModifiedAt.Keys.Count(id => !infoDirectories.ContainsKey(id));
        var removedEntries = previousEntries.Values
            .Where(entry => !infoDirectories.ContainsKey(entry.SourceInfoId))
            .OrderBy(entry => entry.SourceInfoId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var total = infoDirectories.Count + removedEntries.Count;
        progress?.Report(new LibrarySyncProgress(
            "差分を確認中",
            "images",
            0,
            total,
            $"発見 .info {infoDirectories.Count:N0} / 物理消失 {removedEntries.Count:N0}"));

        foreach (var pair in infoDirectories.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var infoId = pair.Key;
            var sourceModifiedStamp = GetMtimeStamp(mtimeIndex, infoId);
            previousEntries.TryGetValue(infoId, out var previousEntry);
            previousAssets.TryGetValue(infoId, out var previousAsset);

            if (allowAssetReuse
                && !mtimeIndex.ReadFailed
                && previousEntry is not null
                && ((sourceModifiedStamp > 0 && previousEntry.SourceModifiedStamp == sourceModifiedStamp)
                    || (sourceModifiedStamp == 0 && sourceIndexUnchanged)))
            {
                if (string.Equals(previousEntry.State, EagleSourceEntryState.Active, StringComparison.Ordinal)
                    && previousAsset is not null)
                {
                    var reusedAsset = CloneAsset(previousAsset);
                    reusedAsset.SourceInfoId = infoId;
                    reusedAsset.SourceModifiedStamp = sourceModifiedStamp;
                    scan.Assets.Add(reusedAsset);
                    scan.SourceEntries.Add(CreateSourceEntry(infoId, sourceModifiedStamp, EagleSourceEntryState.Active));
                    scan.ReusedAssets++;
                    scan.Unchanged++;
                    scan.Processed++;
                    ReportAssetProgress(scan, total, progress, pair.Value.Name);
                    continue;
                }

                if (string.Equals(previousEntry.State, EagleSourceEntryState.Deleted, StringComparison.Ordinal))
                {
                    scan.SourceEntries.Add(CreateSourceEntry(infoId, sourceModifiedStamp, EagleSourceEntryState.Deleted));
                    scan.ReusedAssets++;
                    scan.Unchanged++;
                    scan.Processed++;
                    ReportAssetProgress(scan, total, progress, pair.Value.Name);
                    continue;
                }
            }

            var result = await TryReadAssetAsync(
                state,
                pair.Value,
                scan,
                sourceModifiedStamp,
                cancellationToken);
            scan.SourceEntries.Add(CreateSourceEntry(infoId, sourceModifiedStamp, result.State));
            if (result.Asset is not null)
            {
                scan.Assets.Add(result.Asset);
            }
            else if (IsTemporaryReadState(result.State) && previousAsset is not null)
            {
                var retainedAsset = CloneAsset(previousAsset);
                retainedAsset.SourceInfoId = infoId;
                scan.Assets.Add(retainedAsset);
                scan.RetainedAssets++;
            }

            if (IsTemporaryReadState(result.State))
            {
                scan.Retry++;
            }
            else if (string.Equals(result.State, EagleSourceEntryState.Active, StringComparison.Ordinal))
            {
                if (previousAsset is null)
                {
                    scan.Added++;
                }
                else
                {
                    scan.Changed++;
                }
            }
            else if (string.Equals(result.State, EagleSourceEntryState.Deleted, StringComparison.Ordinal)
                && previousAsset is not null)
            {
                scan.Deleted++;
            }

            scan.Processed++;
            ReportAssetProgress(scan, total, progress, pair.Value.Name);
        }

        foreach (var previousEntry in removedEntries)
        {
            if (previousAssets.ContainsKey(previousEntry.SourceInfoId))
            {
                scan.Deleted++;
            }

            scan.Processed++;
            ReportAssetProgress(scan, total, progress, previousEntry.SourceInfoId + ".info");
        }

        var completed = total == 0 ? 1 : scan.Processed;
        var progressTotal = total == 0 ? 1 : total;
        progress?.Report(new LibrarySyncProgress(
            "更新完了",
            "images",
            completed,
            progressTotal,
            scan.ToDifferenceMessage()));
        return scan;
    }


    private static bool IsEagleTrashDirectory(string name)
    {
        var normalized = name.Trim().Trim('.', '_', '-').Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "trash" or "trashed" or "recycle" or "recycled" or "recyclebin" or "deleted" or "deleteditems";
    }


    private static Dictionary<string, EagleSourceEntry> CreatePreviousSourceEntryLookup(EagleLibrary? previous)
    {
        var lookup = new Dictionary<string, EagleSourceEntry>(StringComparer.OrdinalIgnoreCase);
        if (previous?.SourceEntries is null)
        {
            return lookup;
        }

        foreach (var entry in previous.SourceEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.SourceInfoId) && !lookup.ContainsKey(entry.SourceInfoId))
            {
                lookup[entry.SourceInfoId] = entry;
            }
        }

        return lookup;
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


    private static EagleSourceEntry CreateSourceEntry(string infoId, long sourceModifiedStamp, string state)
    {
        return new EagleSourceEntry
        {
            SourceInfoId = infoId,
            SourceModifiedStamp = sourceModifiedStamp,
            State = state
        };
    }


    private static bool IsTemporaryReadState(string state)
    {
        return string.Equals(state, EagleSourceEntryState.MissingMetadata, StringComparison.Ordinal)
            || string.Equals(state, EagleSourceEntryState.ReadFailed, StringComparison.Ordinal);
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


    private static void ReportAssetProgress(
        AssetScanResult scan,
        int total,
        IProgress<LibrarySyncProgress>? progress,
        string target)
    {
        if (scan.Processed == 1 || scan.Processed == total || scan.Processed % 10 == 0)
        {
            progress?.Report(new LibrarySyncProgress(
                "差分を更新中",
                target,
                scan.Processed,
                total,
                scan.ToDifferenceMessage()));
        }
    }


    private async Task<AssetReadResult> TryReadAssetAsync(
        EagleReaderState state,
        DriveEntry infoDirectory,
        AssetScanResult scan,
        long sourceModifiedStamp,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DriveEntry> children;
        try
        {
            children = await ListChildrenAsync(state, infoDirectory.Id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            scan.DirectoryReadFailures++;
            return new AssetReadResult(null, EagleSourceEntryState.ReadFailed);
        }

        var metadataEntry = children.FirstOrDefault(IsMetadataFile);
        if (metadataEntry is null)
        {
            scan.MetadataMissing++;
            return new AssetReadResult(null, EagleSourceEntryState.MissingMetadata);
        }

        scan.MetadataFiles++;
        try
        {
            using var metadata = await OpenJsonDocumentAsync(state, metadataEntry.Id, cancellationToken);
            var root = metadata.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("metadata.json must contain a JSON object.");
            }

            if (ReadBoolean(root, "isDeleted"))
            {
                return new AssetReadResult(null, EagleSourceEntryState.Deleted);
            }

            var files = children.Where(child => !child.IsFolder && !IsMetadataFile(child)).ToList();
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

            return new AssetReadResult(
                new EagleAsset
                {
                    Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                    Name = string.IsNullOrWhiteSpace(name) ? TrimInfoSuffix(infoDirectory.Name) : name,
                    FileName = fileName,
                    Extension = extension,
                    FileUri = primaryFile is null ? thumbnail is null ? null : BuildFileUri(thumbnail.Id, thumbnail.Name) : BuildFileUri(primaryFile.Id, primaryFile.Name),
                    ThumbnailUri = thumbnail is null ? mediaKind == EagleAssetMediaKind.Image && primaryFile is not null ? BuildFileUri(primaryFile.Id, primaryFile.Name) : null : BuildFileUri(thumbnail.Id, thumbnail.Name),
                    MediaKind = mediaKind,
                    SourceInfoId = TrimInfoSuffix(infoDirectory.Name),
                    SourceModifiedStamp = sourceModifiedStamp,
                    FolderIds = ReadStringArray(root, "folders", "folderIds", "folderId").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Tags = ReadStringArray(root, "tags", "tagNames").Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    SourceUrl = ReadString(root, "url", "source", "sourceUrl"),
                    Annotation = ReadString(root, "annotation", "note", "description"),
                    SizeBytes = primaryFile?.Size ?? thumbnail?.Size ?? ReadLong(root, "size", "sizeBytes"),
                    Width = (int)ReadLong(root, "width"),
                    Height = (int)ReadLong(root, "height"),
                    CreatedAt = ReadDate(root, "btime", "createdAt", "createTime", "birthTime") ?? infoDirectory.CreatedAt,
                    ModifiedAt = ReadDate(root, "mtime", "modifiedAt", "modificationTime", "updatedAt") ?? primaryFile?.ModifiedAt ?? infoDirectory.ModifiedAt
                },
                EagleSourceEntryState.Active);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            scan.AssetReadFailures++;
            return new AssetReadResult(null, EagleSourceEntryState.ReadFailed);
        }
    }
}
