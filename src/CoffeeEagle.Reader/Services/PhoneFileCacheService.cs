using CoffeeEagle.Offline;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class PhoneFileCacheService
{
    private readonly AndroidDocumentTreeService _documents;
    private readonly GoogleDriveLibraryService _drive;

    public PhoneFileCacheService(AndroidDocumentTreeService documents, GoogleDriveLibraryService drive)
    {
        _documents = documents;
        _drive = drive;
        Store = new PhoneFileCacheStore(
            Path.Combine(FileSystem.AppDataDirectory, "phone-file-cache-v1"),
            () =>
            {
                using var stats = new Android.OS.StatFs(FileSystem.AppDataDirectory);
                return stats.AvailableBytes;
            });
    }

    public PhoneFileCacheStore Store { get; }

    public static string ExclusionKey(EagleAsset asset) =>
        PhoneFileCacheKey.ExclusionKey(asset);

    public static string CacheIdentityFor(string sourceUri) => PhoneFileCacheKey.ProviderIdentity(sourceUri);

    public static string CacheIdentityFor(EagleAsset asset, string sourceUri) =>
        PhoneFileCacheKey.IdentityFor(asset, sourceUri);

    public static string RevisionFor(EagleAsset asset, string? sourceUri = null) => PhoneFileCacheKey.RevisionFor(asset, sourceUri);

    public bool TryGetCachedPath(EagleAsset asset, string sourceUri, out string path)
    {
        var expected = string.Equals(sourceUri, asset.FileUri, StringComparison.OrdinalIgnoreCase)
            ? asset.SizeBytes
            : asset.ThumbnailSizeBytes;
        return Store.TryGetPath(CacheIdentityFor(asset, sourceUri), RevisionFor(asset, sourceUri), ExtensionFor(sourceUri, asset.Extension),
            expected > 0 ? expected : null, out path);
    }

    public bool IsFilePathForAsset(EagleAsset asset)
    {
        var uri = asset.FileUri ?? asset.ThumbnailUri;
        return GoogleDriveLibraryService.IsDriveFileUri(uri)
            || (uri is not null && TryGetCachedPath(asset, uri, out _))
            || (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile);
    }

    public async Task<PhoneFileCachePassResult> CacheLibraryAsync(
        EagleLibrary library,
        IProgress<PhoneFileCacheProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = PhoneFileCachePlan.Build(library);
        var total = plan.Files.Count + plan.AudioAssets.Count;
        var result = new MutablePassResult
        {
            Total = total,
            Excluded = plan.ExcludedCount,
            SkippedUnverified = plan.UnverifiedCount
        };
        Report(progress, result, "端末キャッシュを確認中");

        try
        {
            foreach (var item in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var saved = await Store.SaveIfMissingAsync(
                        item.Identity,
                        item.Revision,
                        ExtensionFor(item.Uri, item.Asset.Extension),
                        token => OpenSourceAsync(item.Uri, token),
                        expectedLength: item.ExpectedLength,
                        requireNonEmpty: true,
                        cancellationToken: cancellationToken);
                    if (saved.Downloaded) result.Downloaded++;
                    else result.Reused++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (GoogleDriveReconnectRequiredException)
                {
                    throw;
                }
                catch (PhoneFileCacheStorageException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    AddWarning(result, $"{DisplayName(item.Uri)}: {ex.Message}");
                }

                result.Completed++;
                Report(progress, result, DisplayName(item.Uri));
            }

            foreach (var asset in plan.AudioAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CacheLyricsAsync(asset, result, cancellationToken);
                result.Completed++;
                Report(progress, result, asset.Name + ".lrc");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Cancelled = true;
        }
        catch (Exception ex)
        {
            result.FatalError = ex.Message;
        }

        return result.ToResult();
    }

    public async Task<PhoneCachedLyricsResult> ReadCachedLyricsAsync(EagleAsset asset, CancellationToken cancellationToken)
    {
        var audioUri = asset.FileUri;
        if (string.IsNullOrWhiteSpace(audioUri)) return PhoneCachedLyricsResult.Unknown;
        var audioRevision = RevisionFor(asset, audioUri);
        var manifest = Store.GetLyricsManifest(audioUri);
        if (manifest is null) return PhoneCachedLyricsResult.Unknown;
        if (!string.Equals(manifest.AudioRevision, audioRevision, StringComparison.Ordinal)) return PhoneCachedLyricsResult.Unknown;
        if (string.Equals(manifest.Status, PhoneLyricsCacheState.Missing, StringComparison.Ordinal))
        {
            return new PhoneCachedLyricsResult(Known: true, Bytes: null);
        }

        if (!string.Equals(manifest.Status, PhoneLyricsCacheState.Available, StringComparison.Ordinal)
            || !Store.TryGetLyricsPath(audioUri, out var path)) return PhoneCachedLyricsResult.Unknown;
        try
        {
            await using var input = File.OpenRead(path);
            return new PhoneCachedLyricsResult(true, await LrcLyrics.ReadAsync(input, cancellationToken));
        }
        catch (IOException)
        {
            return PhoneCachedLyricsResult.Unknown;
        }
        catch (InvalidDataException)
        {
            return PhoneCachedLyricsResult.Unknown;
        }
    }

    private async Task CacheLyricsAsync(EagleAsset asset, MutablePassResult result, CancellationToken cancellationToken)
    {
        var audioUri = asset.FileUri!;
        PhoneLyricsSource? source;
        try
        {
            source = GoogleDriveLibraryService.IsDriveFileUri(audioUri)
                ? await _drive.FindLyricsSourceAsync(asset, cancellationToken)
                : await FindDocumentLyricsSourceAsync(asset, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GoogleDriveReconnectRequiredException)
        {
            throw;
        }
        catch (PhoneFileCacheStorageException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            await Store.SetLyricsManifestAsync(audioUri,
                new PhoneLyricsCacheState(PhoneLyricsCacheState.Pending, AudioRevision: RevisionFor(asset, audioUri)), cancellationToken);
            result.LyricsFailed++;
            AddWarning(result, $"{asset.Name}.lrc: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            result.LyricsFailed++;
            AddWarning(result, $"{asset.Name}.lrc: {ex.Message}");
            return;
        }

        if (source is null)
        {
            await Store.RemoveLyricsAsync(audioUri, RevisionFor(asset, audioUri), cancellationToken);
            result.LyricsMissing++;
            return;
        }

        var revision = LyricsRevision(asset, source);
        var identity = PhoneFileCacheStore.LyricsIdentity(audioUri);
        var audioRevision = RevisionFor(asset, audioUri);
        var previous = Store.GetLyricsManifest(audioUri);
        if (previous is null
            || !string.Equals(previous.Status, PhoneLyricsCacheState.Available, StringComparison.Ordinal)
            || !string.Equals(previous.Revision, revision, StringComparison.Ordinal)
            || !string.Equals(previous.SourceUri, source.Uri, StringComparison.Ordinal)
            || !string.Equals(previous.AudioRevision, audioRevision, StringComparison.Ordinal))
        {
            await Store.SetLyricsManifestAsync(audioUri,
                new PhoneLyricsCacheState(PhoneLyricsCacheState.Pending, source.Uri, revision, ".lrc",
                    source.SizeBytes > 0 ? source.SizeBytes : null, audioRevision), cancellationToken);
        }
        if (source.SizeBytes > LrcLyrics.MaxBytes)
        {
            await Store.SetLyricsManifestAsync(audioUri,
                new PhoneLyricsCacheState(PhoneLyricsCacheState.Pending, source.Uri, revision, ".lrc", source.SizeBytes, audioRevision), cancellationToken);
            result.LyricsFailed++;
            AddWarning(result, $"{asset.Name}.lrc: 64 KBを超えています");
            return;
        }

        try
        {
            var saved = await Store.SaveIfMissingAsync(
                identity,
                revision,
                ".lrc",
                token => OpenSourceAsync(source.Uri, token),
                maxBytes: LrcLyrics.MaxBytes,
                expectedLength: source.SizeBytes > 0 ? source.SizeBytes : null,
                preserveOlderRevisions: true,
                validateTemporary: ValidateLyricsFileAsync,
                cancellationToken: cancellationToken);
            await Store.SetLyricsManifestAsync(audioUri,
                new PhoneLyricsCacheState(PhoneLyricsCacheState.Available, source.Uri, revision, ".lrc", saved.Length, RevisionFor(asset, audioUri)), cancellationToken);
            await Store.RemoveOlderRevisionsAsync(identity, revision, ".lrc", cancellationToken);
            if (saved.Downloaded) result.LyricsDownloaded++;
            else result.LyricsReused++;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GoogleDriveReconnectRequiredException)
        {
            throw;
        }
        catch (PhoneFileCacheStorageException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.LyricsFailed++;
            AddWarning(result, $"{asset.Name}.lrc: {ex.Message}");
        }
    }

    private async Task<PhoneLyricsSource?> FindDocumentLyricsSourceAsync(EagleAsset asset, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.SourceDirectoryUri))
            throw new IOException("歌詞を探すため、ライブラリを一度更新してください。");

        return await Task.Run(() =>
        {
            var folder = Android.Net.Uri.Parse(asset.SourceDirectoryUri)!;
            var id = Android.Provider.DocumentsContract.GetDocumentId(folder)!;
            _documents.RequestProviderRefresh(asset.SourceDirectoryUri);
            var files = _documents.ListChildren(asset.SourceDirectoryUri, id, requireListing: true);
            cancellationToken.ThrowIfCancellationRequested();
            var original = files.FirstOrDefault(file => file.Uri == asset.FileUri);
            if (original is null)
                throw new IOException("音声フォルダの一覧が不完全です。接続を確認して歌詞のキャッシュを再試行してください。");
            var sibling = LrcLyrics.FindSibling(original.Name, files.Where(file => !file.IsDirectory), file => file.Name);
            return sibling is null ? null : new PhoneLyricsSource(sibling.Uri, sibling.Name, sibling.Size, sibling.LastModified);
        }, cancellationToken);
    }

    private async Task<Stream> OpenSourceAsync(string uri, CancellationToken cancellationToken)
    {
        if (GoogleDriveLibraryService.IsDriveFileUri(uri))
        {
            return await _drive.OpenDownloadStreamAsync(uri, cancellationToken);
        }

        return await Task.Run(
            () => _documents.OpenRead(uri) ?? throw new IOException("Document provider returned no readable stream."),
            cancellationToken);
    }

    private static async Task ValidateLyricsFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(path);
        _ = await LrcLyrics.ReadAsync(source, cancellationToken);
    }

    private static string LyricsRevision(EagleAsset asset, PhoneLyricsSource source) => string.Join('\n',
        source.Uri,
        source.ModifiedStamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
        source.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string? ExtensionFor(string uri, string? fallback)
    {
        var name = uri;
        if (GoogleDriveLibraryService.IsDriveFileUri(uri))
        {
            var slash = uri.IndexOf('/', "gdrive://files/".Length);
            name = slash < 0 ? string.Empty : Uri.UnescapeDataString(uri[(slash + 1)..]);
        }
        else if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            name = parsed.LocalPath;
        }

        var extension = Path.GetExtension(name);
        return string.IsNullOrWhiteSpace(extension) ? fallback : extension;
    }

    private static string DisplayName(string uri)
    {
        if (GoogleDriveLibraryService.IsDriveFileUri(uri))
        {
            var slash = uri.IndexOf('/', "gdrive://files/".Length);
            return slash < 0 ? "Drive file" : Uri.UnescapeDataString(uri[(slash + 1)..]);
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            ? Path.GetFileName(parsed.LocalPath)
            : uri;
    }

    private static void AddWarning(MutablePassResult result, string warning)
    {
        if (result.Warnings.Count < 5) result.Warnings.Add(warning);
    }

    private static void Report(IProgress<PhoneFileCacheProgress>? progress, MutablePassResult result, string? target) =>
        progress?.Report(new PhoneFileCacheProgress(result.Completed, result.Total, target, result.ToDetail()));

    private sealed class MutablePassResult
    {
        public int Completed;
        public int Total;
        public int Downloaded;
        public int Reused;
        public int Excluded;
        public int SkippedUnverified;
        public int Failed;
        public int LyricsDownloaded;
        public int LyricsReused;
        public int LyricsMissing;
        public int LyricsFailed;
        public bool Cancelled;
        public string? FatalError;
        public List<string> Warnings { get; } = [];

        public string ToDetail() =>
            $"取得 {Downloaded:N0} / 再利用 {Reused:N0} / 除外 {Excluded:N0} / 失敗 {Failed + LyricsFailed:N0}";

        public PhoneFileCachePassResult ToResult() => new(
            Completed, Total, Downloaded, Reused, Excluded, Failed, LyricsDownloaded,
            LyricsReused, LyricsMissing, LyricsFailed, SkippedUnverified, Cancelled, FatalError, Warnings.ToArray());
    }
}

public sealed record PhoneFileCacheProgress(int Completed, int Total, string? Target, string Detail);

public sealed record PhoneFileCachePassResult(
    int Completed,
    int Total,
    int Downloaded,
    int Reused,
    int Excluded,
    int Failed,
    int LyricsDownloaded,
    int LyricsReused,
    int LyricsMissing,
    int LyricsFailed,
    int SkippedUnverified,
    bool Cancelled,
    string? FatalError,
    IReadOnlyList<string> Warnings)
{
    public bool Succeeded => !Cancelled && FatalError is null && Failed == 0 && LyricsFailed == 0;

    public string ToStatusDetail()
    {
        var detail = $"保存 {Downloaded:N0} / 再利用 {Reused:N0} / 除外 {Excluded:N0} / 未確認 {SkippedUnverified:N0} / 失敗 {Failed + LyricsFailed:N0}";
        if (LyricsDownloaded > 0 || LyricsReused > 0 || LyricsMissing > 0)
            detail += $" / LRC 新規 {LyricsDownloaded:N0}・再利用 {LyricsReused:N0}・なし {LyricsMissing:N0}";
        if (Cancelled) detail += " / 未保存分は次回の更新で再試行します";
        if (!string.IsNullOrWhiteSpace(FatalError)) detail += " / 停止: " + FatalError;
        if (Warnings.Count > 0) detail += " / " + string.Join("; ", Warnings);
        return detail;
    }
}

public sealed record PhoneCachedLyricsResult(bool Known, byte[]? Bytes)
{
    public static PhoneCachedLyricsResult Unknown { get; } = new(false, null);
}
