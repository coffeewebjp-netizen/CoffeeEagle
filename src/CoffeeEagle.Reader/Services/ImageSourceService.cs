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
        return CreateContentSource(asset.ThumbnailUri ?? asset.FileUri);
    }

    public ImageSource? CreateFullSource(EagleAsset asset)
    {
        return CreateContentSource(asset.FileUri ?? asset.ThumbnailUri);
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

