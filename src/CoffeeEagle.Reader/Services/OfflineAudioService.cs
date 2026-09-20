using CoffeeEagle.Offline;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class OfflineAudioService
{
    public OfflineAudioStore Store { get; }
    private readonly GoogleDriveLibraryService _drive;
    private readonly AndroidDocumentTreeService _documents;

    public OfflineAudioService(GoogleDriveLibraryService drive, AndroidDocumentTreeService documents)
    {
        _drive = drive;
        _documents = documents;
        var root = Path.Combine(FileSystem.AppDataDirectory, "offline-audio-v1");
        Store = new(root, 20 * OfflineAudioStore.GiB, () =>
        {
            using var stats = new Android.OS.StatFs(FileSystem.AppDataDirectory);
            return stats.AvailableBytes;
        });
    }

    public static bool IsSupported(EagleAsset asset) => asset.MediaKind == EagleAssetMediaKind.Audio &&
        AudioRequest.SupportedExtensions.Contains(Path.GetExtension(asset.FileName).ToLowerInvariant());

    public static AudioRequest RequestFor(EagleLibrary library, EagleAsset asset)
    {
        var title = string.IsNullOrWhiteSpace(asset.Name) ? asset.FileName : asset.Name;
        return new(AudioRequest.KeyFor(library.TreeUri, asset.Id), title[..Math.Min(256, title.Length)],
            AudioRequest.Hash($"{asset.FileUri}\n{asset.SourceModifiedStamp}\n{asset.ModifiedAt:O}\n{asset.SizeBytes}"),
            Path.GetExtension(asset.FileName).ToLowerInvariant(), asset.SizeBytes > 0 ? asset.SizeBytes : -1);
    }

    public Task<OfflineTrack> SaveAsync(EagleLibrary library, EagleAsset asset, IProgress<AudioProgress>? progress, CancellationToken ct)
    {
        if (!IsSupported(asset)) throw new InvalidOperationException("この音声形式は持ち出し保存の対象外です。MP3などをご利用ください。");
        var uri = asset.FileUri ?? throw new InvalidOperationException("音声ファイルがありません。");
        return Store.SaveAsync(RequestFor(library, asset), async token =>
        {
            if (GoogleDriveLibraryService.IsDriveFileUri(uri)) return await _drive.OpenDownloadStreamAsync(uri, token);
            return await Task.Run(() => _documents.OpenRead(uri), token);
        }, progress, ct);
    }
}
