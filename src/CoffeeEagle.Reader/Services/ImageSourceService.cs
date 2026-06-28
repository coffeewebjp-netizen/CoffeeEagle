using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class EagleImageSourceService
{
    private readonly AndroidDocumentTreeService _documents;

    public EagleImageSourceService(AndroidDocumentTreeService documents)
    {
        _documents = documents;
    }

    public ImageSource? CreateThumbnailSource(EagleAsset asset)
    {
        var uri = asset.MediaKind == EagleAssetMediaKind.Image
            ? asset.ThumbnailUri ?? asset.FileUri
            : asset.ThumbnailUri;
        return CreateContentSource(uri);
    }

    public Task OpenExternalAsync(EagleAsset asset)
    {
        var uri = asset.FileUri ?? asset.ThumbnailUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new InvalidOperationException("開けるファイルがありません。");
        }

        _documents.OpenExternal(uri, ResolveMimeType(asset));
        return Task.CompletedTask;
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
                return _documents.OpenRead(uri);
            }
            catch
            {
                return Stream.Null;
            }
        });
    }
}
