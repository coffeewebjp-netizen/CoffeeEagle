using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class EagleImageSourceService
{
    private readonly AndroidDocumentTreeService _documents;
    private readonly GoogleDriveLibraryService _drive;

    public EagleImageSourceService(AndroidDocumentTreeService documents, GoogleDriveLibraryService drive)
    {
        _documents = documents;
        _drive = drive;
    }

    public ImageSource? CreateThumbnailSource(EagleAsset asset)
    {
        var uri = asset.MediaKind == EagleAssetMediaKind.Image
            ? asset.ThumbnailUri ?? asset.FileUri
            : asset.ThumbnailUri;
        return CreateContentSource(uri);
    }

    public async Task<string> GetPlaybackPathAsync(EagleAsset asset, CancellationToken cancellationToken = default)
    {
        var uri = asset.FileUri ?? asset.ThumbnailUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new InvalidOperationException("開けるファイルがありません。");
        }

        if (GoogleDriveLibraryService.IsDriveFileUri(uri))
        {
            return await _drive.GetCachedFilePathAsync(uri, cancellationToken);
        }

        return uri;
    }

    public async Task PreloadAsync(EagleAsset asset, CancellationToken cancellationToken = default)
    {
        var uri = asset.FileUri ?? asset.ThumbnailUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        if (GoogleDriveLibraryService.IsDriveFileUri(uri))
        {
            await _drive.GetCachedFilePathAsync(uri, cancellationToken);
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

    public ImageSource? CreateFullSource(EagleAsset asset)
    {
        return CreateContentSource(asset.FileUri ?? asset.ThumbnailUri);
    }

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

    private ImageSource? CreateContentSource(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return ImageSource.FromStream(() =>
        {
            try
            {
                return GoogleDriveLibraryService.IsDriveFileUri(uri)
                    ? _drive.OpenRead(uri)
                    : _documents.OpenRead(uri);
            }
            catch
            {
                return Stream.Null;
            }
        });
    }
}