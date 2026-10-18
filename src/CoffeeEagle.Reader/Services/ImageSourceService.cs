using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class EagleImageSourceService
{
    private readonly AndroidDocumentTreeService _documents;
    private readonly GoogleDriveLibraryService _drive;
    private readonly PhoneFileCacheService _phoneCache;

    public Task<byte[]?> ReadLyricsAsync(EagleAsset asset, CancellationToken ct) =>
        new AudioLyricsService(_documents, _drive, _phoneCache).ReadAsync(asset, ct);

    public EagleImageSourceService(AndroidDocumentTreeService documents, GoogleDriveLibraryService drive, PhoneFileCacheService phoneCache)
    {
        _documents = documents;
        _drive = drive;
        _phoneCache = phoneCache;
    }

    public ImageSource? CreateThumbnailSource(EagleAsset asset)
    {
        var uri = asset.MediaKind == EagleAssetMediaKind.Image
            ? asset.ThumbnailUri ?? asset.FileUri
            : asset.ThumbnailUri;
        return CreateContentSource(asset, uri);
    }

    public async Task<string> GetPlaybackPathAsync(EagleAsset asset, CancellationToken cancellationToken = default)
    {
        var uri = asset.FileUri ?? asset.ThumbnailUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new InvalidOperationException("開けるファイルがありません。");
        }

        if (_phoneCache.TryGetCachedPath(asset, uri, out var cachedPath)) return cachedPath;

        if (GoogleDriveLibraryService.IsDriveFileUri(uri))
        {
            return await _drive.GetCachedFilePathAsync(uri, PhoneFileCacheService.RevisionFor(asset, uri), cancellationToken);
        }

        if (Uri.TryCreate(uri, UriKind.Absolute, out var localUri) && localUri.IsFile) return localUri.LocalPath;
        return uri;
    }

    public async Task PreloadAsync(EagleAsset asset, CancellationToken cancellationToken = default)
    {
        var uri = asset.FileUri ?? asset.ThumbnailUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        if (_phoneCache.TryGetCachedPath(asset, uri, out _)) return;

        if (GoogleDriveLibraryService.IsDriveFileUri(uri))
        {
            await _drive.GetCachedFilePathAsync(uri, PhoneFileCacheService.RevisionFor(asset, uri), cancellationToken);
        }
    }

    public async Task OpenExternalAsync(EagleAsset asset)
    {
        var uri = asset.FileUri ?? asset.ThumbnailUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new InvalidOperationException("開けるファイルがありません。");
        }

        if (GoogleDriveLibraryService.IsDriveFileUri(uri))
        {
            throw new InvalidOperationException("Drive API由来ファイルの外部アプリ連携はまだ未対応です。音声はアプリ内プレイヤーで再生できます。");
        }

        _documents.OpenExternal(uri, ResolveMimeType(asset));
        await Task.CompletedTask;
    }

    public async Task OpenVideoAsync(EagleAsset asset, CancellationToken cancellationToken = default)
    {
        var uri = asset.FileUri ?? asset.ThumbnailUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new InvalidOperationException("再生できる動画ファイルがありません。");
        }

        var playbackPath = await GetPlaybackPathAsync(asset, cancellationToken);
        var isLocalPath = GoogleDriveLibraryService.IsDriveFileUri(uri)
            || _phoneCache.TryGetCachedPath(asset, uri, out _)
            || (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile);
        VideoPlayerActivity.Start(playbackPath, asset.Name, isLocalPath);
    }

    public ImageSource? CreateFullSource(EagleAsset asset)
    {
        return CreateContentSource(asset, asset.FileUri ?? asset.ThumbnailUri);
    }

    public bool IsFilePathForAsset(EagleAsset asset) => IsFilePathForAsset(asset, asset.FileUri ?? asset.ThumbnailUri);

    public bool IsFilePathForAsset(EagleAsset asset, string? uri) =>
        !string.IsNullOrWhiteSpace(uri)
        && (GoogleDriveLibraryService.IsDriveFileUri(uri)
            || _phoneCache.TryGetCachedPath(asset, uri, out _)
            || (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile));

    private static string ResolveMimeType(EagleAsset asset)
    {
        return asset.MediaKind switch
        {
            EagleAssetMediaKind.Audio => "audio/*",
            EagleAssetMediaKind.Video => "video/*",
            EagleAssetMediaKind.Image => "image/*",
            _ => "*/*"
        };
    }

    private ImageSource? CreateContentSource(EagleAsset asset, string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return ImageSource.FromStream(() =>
        {
            try
            {
                if (_phoneCache.TryGetCachedPath(asset, uri, out var cachedPath)) return File.OpenRead(cachedPath);
                return GoogleDriveLibraryService.IsDriveFileUri(uri)
                    ? _drive.OpenRead(uri, PhoneFileCacheService.RevisionFor(asset, uri))
                    : _documents.OpenRead(uri);
            }
            catch
            {
                return Stream.Null;
            }
        });
    }
}
