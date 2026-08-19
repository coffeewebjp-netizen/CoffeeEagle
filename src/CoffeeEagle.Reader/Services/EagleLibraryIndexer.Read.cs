using System.Globalization;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class EagleLibraryIndexer
{

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
}
