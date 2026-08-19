using System.Globalization;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class EagleLibraryIndexer
{

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
}
