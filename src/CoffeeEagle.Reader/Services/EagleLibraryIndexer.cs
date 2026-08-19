using System.Globalization;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class EagleLibraryIndexer
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
}
