using CoffeeEagle.Offline;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class OfflineAudioService
{
    public OfflineAudioStore Store { get; }
    public WatchTargetStore Targets { get; }
    private readonly GoogleDriveLibraryService _drive;
    private readonly AndroidDocumentTreeService _documents;

    public OfflineAudioService(GoogleDriveLibraryService drive, AndroidDocumentTreeService documents)
    {
        _drive = drive;
        _documents = documents;
        var root = Path.Combine(FileSystem.AppDataDirectory, "offline-audio-v1");
        Targets = new(Path.Combine(FileSystem.AppDataDirectory, "watch-targets-v1.json"));
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

    public async Task<OfflineTrack> SaveAsync(EagleLibrary library, EagleAsset asset, IProgress<AudioProgress>? progress, CancellationToken ct)
    {
        if (!IsSupported(asset)) throw new InvalidOperationException("この音声形式は持ち出し保存の対象外です。MP3などをご利用ください。");
        var uri = asset.FileUri ?? throw new InvalidOperationException("音声ファイルがありません。");
        var track = await Store.SaveAsync(RequestFor(library, asset), async token =>
        {
            if (GoogleDriveLibraryService.IsDriveFileUri(uri)) return await _drive.OpenDownloadStreamAsync(uri, token);
            return await Task.Run(() => _documents.OpenRead(uri), token);
        }, progress, ct);
        await SaveArtworkAsync(asset, track, ct);
        try
        {
            var lyrics = await new AudioLyricsService(_documents, _drive).ReadAsync(asset, ct);
            await Store.Lyrics.SaveAsync(track.Key, track.Revision, lyrics, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"音声は保存済みですが、「{asset.Name}」の歌詞を更新できませんでした。再試行してください。\n{ex.Message}", ex);
        }
        return track;
    }

    private async Task SaveArtworkAsync(EagleAsset asset, OfflineTrack track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset.ThumbnailUri)) return;
        try
        {
            if (await Store.Artwork.ReadAsync(track.Key, track.Revision, ct) is not null) return;
            await using var source = GoogleDriveLibraryService.IsDriveFileUri(asset.ThumbnailUri)
                ? await _drive.OpenDownloadStreamAsync(asset.ThumbnailUri, ct) : _documents.OpenRead(asset.ThumbnailUri);
            using var encoded = new MemoryStream();
            var buffer = new byte[16384]; int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                if (encoded.Length + read > 4 * 1024 * 1024) return;
                encoded.Write(buffer, 0, read);
            }
            var jpeg = await Task.Run(() => CreateArtwork(encoded.ToArray()), ct);
            if (jpeg is not null) await Store.Artwork.SaveAsync(track.Key, track.Revision, jpeg, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* Missing/broken artwork uses a media tile. */ }
    }

    private static byte[]? CreateArtwork(byte[] encoded)
    {
        using var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
        Android.Graphics.BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, bounds)?.Dispose();
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0 || bounds.OutWidth > 30000 || bounds.OutHeight > 30000) return null;
        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > 320) sample *= 2;
        using var options = new Android.Graphics.BitmapFactory.Options { InSampleSize = sample };
        using var bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, options);
        if (bitmap is null) return null;
        foreach (var side in new[] { 160, 96 })
        {
            var scale = (double)side / Math.Max(bitmap.Width, bitmap.Height);
            var scaled = Android.Graphics.Bitmap.CreateScaledBitmap(bitmap, Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)), true);
            using var jpeg = new MemoryStream();
            try { scaled.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 65, jpeg); }
            finally { if (!ReferenceEquals(scaled, bitmap)) scaled.Dispose(); }
            if (jpeg.Length <= OfflineArtworkStore.MaxImageBytes) return jpeg.ToArray();
        }
        return null;
    }
}
