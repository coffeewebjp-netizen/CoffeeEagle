using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class GoogleDriveLibraryService
{
    public const string DefaultClientId = "327808944898-qr1qd5imhe3ddp56feng1kmpkpqnq10c.apps.googleusercontent.com";

    private const string DriveReadonlyScope = "https://www.googleapis.com/auth/drive.readonly";
    private const string BrowserRedirectUri = "net.coffeewebjp.coffeeeagle.reader:/oauth2redirect";
    private const string AuthorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string DriveFilesUrl = "https://www.googleapis.com/drive/v3/files";
    private const string RefreshTokenKey = "coffee-eagle-google-drive-refresh-token";

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

    private readonly EagleLibraryStore _store;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public GoogleDriveLibraryService(EagleLibraryStore store)
    {
        _store = store;
    }

    public static bool IsDriveApiUri(string? uri)
    {
        return !string.IsNullOrWhiteSpace(uri)
            && uri.StartsWith("gdrive://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveFileUri(string? uri)
    {
        return !string.IsNullOrWhiteSpace(uri)
            && uri.StartsWith("gdrive://files/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveFolderUri(string? uri)
    {
        return !string.IsNullOrWhiteSpace(uri)
            && uri.StartsWith("gdrive://folders/", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildFolderUri(string folderId) => $"gdrive://folders/{folderId}";

    private static string BuildFileUri(string fileId, string fileName)
    {
        return $"gdrive://files/{fileId}/{Uri.EscapeDataString(fileName)}";
    }

    public static string ExtractFolderId(string input)
    {
        var value = input.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        const string uriPrefix = "gdrive://folders/";
        if (value.StartsWith(uriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[uriPrefix.Length..];
        }

        const string marker = "/folders/";
        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            value = value[(markerIndex + marker.Length)..];
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = ExtractQueryValue(uri.Query, "id");
        }

        var cutIndex = value.IndexOfAny(['?', '/', '&', '#']);
        if (cutIndex >= 0)
        {
            value = value[..cutIndex];
        }

        return value.Trim();
    }

    public async Task<bool> HasRefreshTokenAsync()
    {
        return !string.IsNullOrWhiteSpace(await GetRefreshTokenAsync());
    }

    public async Task AuthorizeWithBrowserAsync(
        EagleReaderState state,
        IProgress<LibrarySyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state.GoogleDriveClientId))
        {
            throw new InvalidOperationException("Google OAuth Client ID が未設定です。");
        }

        var codeVerifier = CreateCodeVerifier();
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var authUri = CreateAuthorizationUri(state.GoogleDriveClientId, codeChallenge);

        progress?.Report(new LibrarySyncProgress("Googleログインを開いています", "Google OAuth", Detail: "accounts.google.com"));
        var result = await WebAuthenticator.Default.AuthenticateAsync(authUri, new Uri(BrowserRedirectUri));
        if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            var error = result.Properties.TryGetValue("error", out var errorValue) ? errorValue : "authorization_failed";
            throw new InvalidOperationException($"Google認証に失敗しました: {error}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new LibrarySyncProgress("認証トークンを取得中", "OAuthアクセストークン", Detail: "oauth2.googleapis.com"));
        var form = new Dictionary<string, string>
        {
            ["client_id"] = state.GoogleDriveClientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = BrowserRedirectUri
        };
        if (!string.IsNullOrWhiteSpace(state.GoogleDriveClientSecret))
        {
            form["client_secret"] = state.GoogleDriveClientSecret;
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(TokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google認証に失敗しました: {GetErrorMessage(body)}");
        }

        await SaveTokenResponseAsync(state, body, cancellationToken);
    }

    public Stream OpenRead(string uri)
    {
        var path = GetCachedFilePathAsync(uri).GetAwaiter().GetResult();
        return File.OpenRead(path);
    }

    public async Task<string> GetCachedFilePathAsync(string uri, CancellationToken cancellationToken = default)
    {
        var fileId = ExtractFileId(uri);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new InvalidOperationException("Google DriveのファイルIDを取得できませんでした。");
        }

        var fileName = ExtractFileName(uri);
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        var cacheDirectory = Path.Combine(FileSystem.CacheDirectory, "drive-files");
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, fileId + extension.ToLowerInvariant());
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        var state = await _store.LoadAsync(cancellationToken);
        var accessToken = await GetValidAccessTokenAsync(state, cancellationToken);
        var tempPath = path + ".tmp";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{DriveFilesUrl}/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Google Driveからファイルを取得できませんでした: HTTP {(int)response.StatusCode} / {GetErrorMessage(body)}");
        }

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(tempPath))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
        return path;
    }
    public async Task<EagleLibrary> IndexAsync(
        EagleReaderState state,
        EagleLibrary? previous = null,
        IProgress<LibrarySyncProgress>? progress = null,
        bool allowAssetReuse = true,
        CancellationToken cancellationToken = default)
    {
        var folderId = ExtractFolderId(state.GoogleDriveFolderId ?? string.Empty);
        if (string.IsNullOrWhiteSpace(folderId))
        {
            throw new InvalidOperationException("Google DriveのEAGLE .libraryフォルダIDが未設定です。");
        }

        progress?.Report(new LibrarySyncProgress("ライブラリ情報を取得中", folderId, Detail: "Drive files.get"));
        var root = await GetFileAsync(state, folderId, cancellationToken);
        progress?.Report(new LibrarySyncProgress("ルート一覧を取得中", root.Name, Detail: "Drive files.list"));
        var rootChildren = await ListChildrenAsync(state, folderId, cancellationToken);
        var rootMetadata = rootChildren.FirstOrDefault(IsMetadataFile);
        var mtimeEntry = rootChildren.FirstOrDefault(entry => !entry.IsFolder && string.Equals(entry.Name, "mtime.json", StringComparison.OrdinalIgnoreCase));
        var imagesDirectory = rootChildren.FirstOrDefault(entry => entry.IsFolder && string.Equals(entry.Name, "images", StringComparison.OrdinalIgnoreCase));
        if (imagesDirectory is null)
        {
            throw new InvalidOperationException("EAGLEライブラリの images フォルダが見つかりません。");
        }

        var libraryName = CleanLibraryName(root.Name);
        var folders = new List<EagleFolder>();
        if (rootMetadata is not null)
        {
            progress?.Report(new LibrarySyncProgress("フォルダー構成を取得中", rootMetadata.Name, Detail: "ライブラリmetadata.json"));
            using var metadata = await OpenJsonDocumentAsync(state, rootMetadata.Id, cancellationToken);
            libraryName = ReadString(metadata.RootElement, "name", "title", "libraryName") ?? libraryName;
            folders.AddRange(ReadFolders(metadata.RootElement));
        }

        NormalizeFolderPaths(folders);
        if (mtimeEntry is not null)
        {
            progress?.Report(new LibrarySyncProgress("更新一覧を取得中", mtimeEntry.Name, Detail: "変更件数を確認"));
        }

        var mtimeIndex = await ReadMtimeIndexAsync(state, mtimeEntry, cancellationToken);
        progress?.Report(new LibrarySyncProgress(
            "変更対象を探索中",
            imagesDirectory.Name,
            0,
            null,
            "Drive APIで.info一覧を取得"));
        var scan = await ScanAssetsAsync(state, imagesDirectory.Id, mtimeIndex, previous, allowAssetReuse, progress, cancellationToken);
        EnsureReferencedFolders(folders, scan.Assets);
        NormalizeFolderPaths(folders);

        return new EagleLibrary
        {
            Id = previous?.Id ?? Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(libraryName) ? "EAGLE Library" : libraryName,
            SourceKind = EagleLibrarySourceKind.GoogleDriveApi,
            SourceLabel = "Google Drive API",
            TreeUri = BuildFolderUri(folderId),
            RootDocumentId = folderId,
            IndexMessage = scan.ToMessage(),
            IndexedAt = DateTimeOffset.UtcNow,
            IndexFormatVersion = EagleLibrary.CurrentIndexFormatVersion,
            SourceIndexModifiedStamp = mtimeIndex.ModifiedStamp,
            Folders = folders
                .OrderBy(folder => folder.Path, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            Assets = scan.Assets
                .OrderByDescending(asset => asset.ModifiedAt ?? asset.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SourceEntries = scan.SourceEntries
                .OrderBy(entry => entry.SourceInfoId, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private async Task<AssetScanResult> ScanAssetsAsync(
        EagleReaderState state,
        string imagesFolderId,
        MtimeIndex mtimeIndex,
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
        var canUsePreviousIndex = previous is not null
            && previous.IndexFormatVersion == EagleLibrary.CurrentIndexFormatVersion;
        var previousEntries = canUsePreviousIndex
            ? CreatePreviousSourceEntryLookup(previous)
            : new Dictionary<string, EagleSourceEntry>(StringComparer.OrdinalIgnoreCase);
        var previousAssets = CreatePreviousAssetLookup(previous);
        var sourceIndexUnchanged = canUsePreviousIndex
            && mtimeIndex.ModifiedStamp > 0
            && previous!.SourceIndexModifiedStamp == mtimeIndex.ModifiedStamp;
        var infoDirectories = new Dictionary<string, DriveEntry>(StringComparer.OrdinalIgnoreCase);
        var pendingFolders = new Queue<(string Id, string Name)>();
        pendingFolders.Enqueue((imagesFolderId, "images"));

        // The physical .info list is authoritative for existence. mtime.json is only a
        // change hint and is intentionally not used to exclude directories from this scan.
        while (pendingFolders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pendingFolders.Dequeue();
            scan.VisitedDirectories++;
            progress?.Report(new LibrarySyncProgress(
                "変更対象を探索中",
                folder.Name,
                0,
                infoDirectories.Count > 0 ? infoDirectories.Count : null,
                $"発見 .info {infoDirectories.Count:N0} / 確認済みフォルダー {scan.VisitedDirectories:N0}"));
            IReadOnlyList<DriveEntry> children;
            try
            {
                children = await ListChildrenAsync(state, folder.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                scan.DirectoryReadFailures++;
                scan.DiscoveryReadFailures++;
                continue;
            }

            foreach (var child in children)
            {
                if (!child.IsFolder)
                {
                    continue;
                }

                if (child.Name.EndsWith(".info", StringComparison.OrdinalIgnoreCase))
                {
                    var infoId = TrimInfoSuffix(child.Name);
                    infoDirectories.TryAdd(infoId, child);
                }
                else
                {
                    if (IsEagleTrashDirectory(child.Name))
                    {
                        scan.TrashDirectories++;
                        continue;
                    }

                    pendingFolders.Enqueue((child.Id, child.Name));
                }
            }
        }

        scan.InfoDirectories = infoDirectories.Count;
        if (scan.DiscoveryReadFailures > 0)
        {
            throw new InvalidOperationException(".info一覧の一部を取得できなかったため、安全のため更新を中止しました。通信状態を確認して再試行してください。");
        }

        scan.MtimeMissingIds = mtimeIndex.AssetModifiedAt.Keys.Count(id => !infoDirectories.ContainsKey(id));
        var removedEntries = previousEntries.Values
            .Where(entry => !infoDirectories.ContainsKey(entry.SourceInfoId))
            .OrderBy(entry => entry.SourceInfoId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var total = infoDirectories.Count + removedEntries.Count;
        progress?.Report(new LibrarySyncProgress(
            "差分を確認中",
            "images",
            0,
            total,
            $"発見 .info {infoDirectories.Count:N0} / 物理消失 {removedEntries.Count:N0}"));

        foreach (var pair in infoDirectories.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var infoId = pair.Key;
            var sourceModifiedStamp = GetMtimeStamp(mtimeIndex, infoId);
            previousEntries.TryGetValue(infoId, out var previousEntry);
            previousAssets.TryGetValue(infoId, out var previousAsset);

            if (allowAssetReuse
                && !mtimeIndex.ReadFailed
                && previousEntry is not null
                && ((sourceModifiedStamp > 0 && previousEntry.SourceModifiedStamp == sourceModifiedStamp)
                    || (sourceModifiedStamp == 0 && sourceIndexUnchanged)))
            {
                if (string.Equals(previousEntry.State, EagleSourceEntryState.Active, StringComparison.Ordinal)
                    && previousAsset is not null)
                {
                    var reusedAsset = CloneAsset(previousAsset);
                    reusedAsset.SourceInfoId = infoId;
                    reusedAsset.SourceModifiedStamp = sourceModifiedStamp;
                    scan.Assets.Add(reusedAsset);
                    scan.SourceEntries.Add(CreateSourceEntry(infoId, sourceModifiedStamp, EagleSourceEntryState.Active));
                    scan.ReusedAssets++;
                    scan.Unchanged++;
                    scan.Processed++;
                    ReportAssetProgress(scan, total, progress, pair.Value.Name);
                    continue;
                }

                if (string.Equals(previousEntry.State, EagleSourceEntryState.Deleted, StringComparison.Ordinal))
                {
                    scan.SourceEntries.Add(CreateSourceEntry(infoId, sourceModifiedStamp, EagleSourceEntryState.Deleted));
                    scan.ReusedAssets++;
                    scan.Unchanged++;
                    scan.Processed++;
                    ReportAssetProgress(scan, total, progress, pair.Value.Name);
                    continue;
                }
            }

            var result = await TryReadAssetAsync(
                state,
                pair.Value,
                scan,
                sourceModifiedStamp,
                cancellationToken);
            scan.SourceEntries.Add(CreateSourceEntry(infoId, sourceModifiedStamp, result.State));
            if (result.Asset is not null)
            {
                scan.Assets.Add(result.Asset);
            }
            else if (IsTemporaryReadState(result.State) && previousAsset is not null)
            {
                var retainedAsset = CloneAsset(previousAsset);
                retainedAsset.SourceInfoId = infoId;
                scan.Assets.Add(retainedAsset);
                scan.RetainedAssets++;
            }

            if (IsTemporaryReadState(result.State))
            {
                scan.Retry++;
            }
            else if (string.Equals(result.State, EagleSourceEntryState.Active, StringComparison.Ordinal))
            {
                if (previousAsset is null)
                {
                    scan.Added++;
                }
                else
                {
                    scan.Changed++;
                }
            }
            else if (string.Equals(result.State, EagleSourceEntryState.Deleted, StringComparison.Ordinal)
                && previousAsset is not null)
            {
                scan.Deleted++;
            }

            scan.Processed++;
            ReportAssetProgress(scan, total, progress, pair.Value.Name);
        }

        foreach (var previousEntry in removedEntries)
        {
            if (previousAssets.ContainsKey(previousEntry.SourceInfoId))
            {
                scan.Deleted++;
            }

            scan.Processed++;
            ReportAssetProgress(scan, total, progress, previousEntry.SourceInfoId + ".info");
        }

        var completed = total == 0 ? 1 : scan.Processed;
        var progressTotal = total == 0 ? 1 : total;
        progress?.Report(new LibrarySyncProgress(
            "更新完了",
            "images",
            completed,
            progressTotal,
            scan.ToDifferenceMessage()));
        return scan;
    }

    private static bool IsEagleTrashDirectory(string name)
    {
        var normalized = name.Trim().Trim('.', '_', '-').Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "trash" or "trashed" or "recycle" or "recycled" or "recyclebin" or "deleted" or "deleteditems";
    }

    private static Dictionary<string, EagleSourceEntry> CreatePreviousSourceEntryLookup(EagleLibrary? previous)
    {
        var lookup = new Dictionary<string, EagleSourceEntry>(StringComparer.OrdinalIgnoreCase);
        if (previous?.SourceEntries is null)
        {
            return lookup;
        }

        foreach (var entry in previous.SourceEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.SourceInfoId) && !lookup.ContainsKey(entry.SourceInfoId))
            {
                lookup[entry.SourceInfoId] = entry;
            }
        }

        return lookup;
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

    private static EagleSourceEntry CreateSourceEntry(string infoId, long sourceModifiedStamp, string state)
    {
        return new EagleSourceEntry
        {
            SourceInfoId = infoId,
            SourceModifiedStamp = sourceModifiedStamp,
            State = state
        };
    }

    private static bool IsTemporaryReadState(string state)
    {
        return string.Equals(state, EagleSourceEntryState.MissingMetadata, StringComparison.Ordinal)
            || string.Equals(state, EagleSourceEntryState.ReadFailed, StringComparison.Ordinal);
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

    private static void ReportAssetProgress(
        AssetScanResult scan,
        int total,
        IProgress<LibrarySyncProgress>? progress,
        string target)
    {
        if (scan.Processed == 1 || scan.Processed == total || scan.Processed % 10 == 0)
        {
            progress?.Report(new LibrarySyncProgress(
                "差分を更新中",
                target,
                scan.Processed,
                total,
                scan.ToDifferenceMessage()));
        }
    }

    private async Task<AssetReadResult> TryReadAssetAsync(
        EagleReaderState state,
        DriveEntry infoDirectory,
        AssetScanResult scan,
        long sourceModifiedStamp,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DriveEntry> children;
        try
        {
            children = await ListChildrenAsync(state, infoDirectory.Id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            scan.DirectoryReadFailures++;
            return new AssetReadResult(null, EagleSourceEntryState.ReadFailed);
        }

        var metadataEntry = children.FirstOrDefault(IsMetadataFile);
        if (metadataEntry is null)
        {
            scan.MetadataMissing++;
            return new AssetReadResult(null, EagleSourceEntryState.MissingMetadata);
        }

        scan.MetadataFiles++;
        try
        {
            using var metadata = await OpenJsonDocumentAsync(state, metadataEntry.Id, cancellationToken);
            var root = metadata.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("metadata.json must contain a JSON object.");
            }

            if (ReadBoolean(root, "isDeleted"))
            {
                return new AssetReadResult(null, EagleSourceEntryState.Deleted);
            }

            var files = children.Where(child => !child.IsFolder && !IsMetadataFile(child)).ToList();
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

            return new AssetReadResult(
                new EagleAsset
                {
                    Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                    Name = string.IsNullOrWhiteSpace(name) ? TrimInfoSuffix(infoDirectory.Name) : name,
                    FileName = fileName,
                    Extension = extension,
                    FileUri = primaryFile is null ? thumbnail is null ? null : BuildFileUri(thumbnail.Id, thumbnail.Name) : BuildFileUri(primaryFile.Id, primaryFile.Name),
                    ThumbnailUri = thumbnail is null ? mediaKind == EagleAssetMediaKind.Image && primaryFile is not null ? BuildFileUri(primaryFile.Id, primaryFile.Name) : null : BuildFileUri(thumbnail.Id, thumbnail.Name),
                    MediaKind = mediaKind,
                    SourceInfoId = TrimInfoSuffix(infoDirectory.Name),
                    SourceModifiedStamp = sourceModifiedStamp,
                    FolderIds = ReadStringArray(root, "folders", "folderIds", "folderId").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Tags = ReadStringArray(root, "tags", "tagNames").Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    SourceUrl = ReadString(root, "url", "source", "sourceUrl"),
                    Annotation = ReadString(root, "annotation", "note", "description"),
                    SizeBytes = primaryFile?.Size ?? thumbnail?.Size ?? ReadLong(root, "size", "sizeBytes"),
                    Width = (int)ReadLong(root, "width"),
                    Height = (int)ReadLong(root, "height"),
                    CreatedAt = ReadDate(root, "btime", "createdAt", "createTime", "birthTime") ?? infoDirectory.CreatedAt,
                    ModifiedAt = ReadDate(root, "mtime", "modifiedAt", "modificationTime", "updatedAt") ?? primaryFile?.ModifiedAt ?? infoDirectory.ModifiedAt
                },
                EagleSourceEntryState.Active);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            scan.AssetReadFailures++;
            return new AssetReadResult(null, EagleSourceEntryState.ReadFailed);
        }
    }
    private async Task<IReadOnlyList<DriveEntry>> ListChildrenAsync(EagleReaderState state, string folderId, CancellationToken cancellationToken)
    {
        var accessToken = await GetValidAccessTokenAsync(state, cancellationToken);
        var entries = new List<DriveEntry>();
        string? pageToken = null;
        do
        {
            var query = $"'{folderId}' in parents and trashed = false";
            var url = $"{DriveFilesUrl}?pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true&orderBy=folder,name_natural&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime,createdTime)&q={Uri.EscapeDataString(query)}";
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Google Drive一覧の取得に失敗しました: {GetErrorMessage(body)}");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            pageToken = root.TryGetProperty("nextPageToken", out var tokenElement) ? tokenElement.GetString() : null;
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            entries.AddRange(files.EnumerateArray().Select(ReadDriveEntry));
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return entries;
    }

    private async Task<DriveEntry> GetFileAsync(EagleReaderState state, string fileId, CancellationToken cancellationToken)
    {
        var accessToken = await GetValidAccessTokenAsync(state, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{DriveFilesUrl}/{Uri.EscapeDataString(fileId)}?supportsAllDrives=true&fields=id,name,mimeType,size,modifiedTime,createdTime");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Driveファイルの取得に失敗しました: {GetErrorMessage(body)}");
        }

        using var document = JsonDocument.Parse(body);
        return ReadDriveEntry(document.RootElement);
    }

    private async Task<JsonDocument> OpenJsonDocumentAsync(EagleReaderState state, string fileId, CancellationToken cancellationToken)
    {
        var accessToken = await GetValidAccessTokenAsync(state, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{DriveFilesUrl}/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using var reader = new StreamReader(stream);
            var body = await reader.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Google Driveファイルの読み込みに失敗しました: {GetErrorMessage(body)}");
        }

        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<MtimeIndex> ReadMtimeIndexAsync(EagleReaderState state, DriveEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return MtimeIndex.Empty;
        }

        try
        {
            var assets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var declaredTotal = 0;
            var modifiedStamp = entry.ModifiedAt?.ToUnixTimeMilliseconds() ?? 0;
            using var document = await OpenJsonDocumentAsync(state, entry.Id, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new MtimeIndex(true, true, assets, 0, modifiedStamp);
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

            return new MtimeIndex(true, false, assets, declaredTotal, modifiedStamp);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new MtimeIndex(
                true,
                true,
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                0,
                entry.ModifiedAt?.ToUnixTimeMilliseconds() ?? 0);
        }
    }

    private async Task<string> GetValidAccessTokenAsync(EagleReaderState state, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken)
            && DateTimeOffset.UtcNow < _accessTokenExpiresAt.AddSeconds(-60))
        {
            return _accessToken;
        }

        if (string.IsNullOrWhiteSpace(state.GoogleDriveClientId))
        {
            throw new InvalidOperationException("Google OAuth Client ID が未設定です。");
        }

        var refreshToken = await GetRefreshTokenAsync();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new GoogleDriveReconnectRequiredException("Google Driveに接続してください。");
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = state.GoogleDriveClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };
        if (!string.IsNullOrWhiteSpace(state.GoogleDriveClientSecret))
        {
            form["client_secret"] = state.GoogleDriveClientSecret;
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(TokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (string.Equals(GetErrorCode(body), "invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                await SecureStorage.Default.SetAsync(RefreshTokenKey, string.Empty);
                throw new GoogleDriveReconnectRequiredException("Google Driveの認証期限が切れたか、Google側で取り消されています。もう一度Google Driveに接続してください。");
            }

            throw new InvalidOperationException($"Google Driveの再接続に失敗しました: {GetErrorMessage(body)}");
        }

        await SaveTokenResponseAsync(state, body, cancellationToken, keepExistingRefreshToken: true);
        return _accessToken ?? throw new InvalidOperationException("Google Driveのアクセストークンを取得できませんでした。");
    }

    private async Task SaveTokenResponseAsync(
        EagleReaderState state,
        string body,
        CancellationToken cancellationToken,
        bool keepExistingRefreshToken = false)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        _accessToken = GetString(root, "access_token");
        var expiresIn = GetInt32(root, "expires_in", 3600);
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        var refreshToken = GetString(root, "refresh_token");
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await SecureStorage.Default.SetAsync(RefreshTokenKey, refreshToken);
        }
        else if (!keepExistingRefreshToken)
        {
            throw new InvalidOperationException("Google Driveの更新トークンを取得できませんでした。");
        }

        state.GoogleDriveConnectedAt = DateTimeOffset.UtcNow;
    }

    private static async Task<string?> GetRefreshTokenAsync()
    {
        try
        {
            var token = await SecureStorage.Default.GetAsync(RefreshTokenKey);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }
    private static Uri CreateAuthorizationUri(string clientId, string codeChallenge)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = BrowserRedirectUri,
            ["response_type"] = "code",
            ["scope"] = DriveReadonlyScope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        var query = string.Join("&", parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"{AuthorizationUrl}?{query}");
    }

    private static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static DriveEntry ReadDriveEntry(JsonElement file)
    {
        return new DriveEntry(
            GetString(file, "id"),
            GetString(file, "name"),
            GetString(file, "mimeType"),
            GetInt64(file, "size") ?? 0,
            ReadIsoDate(file, "createdTime"),
            ReadIsoDate(file, "modifiedTime"));
    }

    private static DriveEntry? SelectThumbnail(IReadOnlyList<DriveEntry> files)
    {
        return files
            .Where(file => file.Name.Contains("thumb", StringComparison.OrdinalIgnoreCase)
                || file.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
                || file.Name.StartsWith("cover", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Size <= 0 ? long.MaxValue : file.Size)
            .FirstOrDefault();
    }

    private static DriveEntry? SelectPrimaryMedia(
        IReadOnlyList<DriveEntry> allFiles,
        IReadOnlyList<DriveEntry> supportedFiles,
        DriveEntry? thumbnail,
        JsonElement metadata)
    {
        var candidates = supportedFiles
            .Where(file => thumbnail is null || !string.Equals(file.Id, thumbnail.Id, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count > 0)
        {
            return candidates
                .OrderBy(file => IsImageFile(file) && IsLikelyThumbnail(file) ? 1 : 0)
                .ThenByDescending(file => file.Size)
                .First();
        }

        var metadataFileName = ReadString(metadata, "fileName", "filename");
        var metadataExtension = NormalizeExtension(ReadString(metadata, "ext", "extension") ?? Path.GetExtension(metadataFileName ?? string.Empty));
        var fallback = allFiles
            .Where(file => thumbnail is null || !string.Equals(file.Id, thumbnail.Id, StringComparison.Ordinal))
            .Where(file => !IsLikelyThumbnail(file))
            .ToList();

        if (!string.IsNullOrWhiteSpace(metadataFileName))
        {
            var byName = fallback.FirstOrDefault(file => string.Equals(file.Name, metadataFileName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        if (IsKnownMediaExtension(metadataExtension))
        {
            return fallback
                .OrderBy(file => IsImageFile(file) ? 1 : 0)
                .ThenByDescending(file => file.Size)
                .FirstOrDefault()
                ?? thumbnail;
        }

        return thumbnail;
    }

    private static bool IsMetadataFile(DriveEntry entry)
    {
        return !entry.IsFolder && string.Equals(entry.Name, "metadata.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedMediaFile(DriveEntry entry) => IsImageFile(entry) || IsAudioFile(entry) || IsVideoFile(entry);

    private static bool IsImageFile(DriveEntry entry) => entry.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ImageExtensions.Contains(Path.GetExtension(entry.Name));

    private static bool IsAudioFile(DriveEntry entry) => entry.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || AudioExtensions.Contains(Path.GetExtension(entry.Name));

    private static bool IsVideoFile(DriveEntry entry) => entry.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || VideoExtensions.Contains(Path.GetExtension(entry.Name));

    private static bool IsLikelyThumbnail(DriveEntry entry)
    {
        return entry.Name.Contains("thumb", StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
            || entry.Name.StartsWith("cover", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownMediaExtension(string? extension)
    {
        return !string.IsNullOrWhiteSpace(extension)
            && (ImageExtensions.Contains(extension) || AudioExtensions.Contains(extension) || VideoExtensions.Contains(extension));
    }

    private static string ResolveMediaKind(DriveEntry? primaryFile, JsonElement metadata)
    {
        var extension = NormalizeExtension(ReadString(metadata, "ext", "extension") ?? Path.GetExtension(primaryFile?.Name ?? string.Empty));
        if (primaryFile is not null)
        {
            if (IsAudioFile(primaryFile)) return EagleAssetMediaKind.Audio;
            if (IsVideoFile(primaryFile)) return EagleAssetMediaKind.Video;
            if (IsImageFile(primaryFile)) return EagleAssetMediaKind.Image;
        }

        if (!string.IsNullOrWhiteSpace(extension))
        {
            if (AudioExtensions.Contains(extension)) return EagleAssetMediaKind.Audio;
            if (VideoExtensions.Contains(extension)) return EagleAssetMediaKind.Video;
            if (ImageExtensions.Contains(extension)) return EagleAssetMediaKind.Image;
        }

        return EagleAssetMediaKind.Other;
    }

    private static List<EagleFolder> ReadFolders(JsonElement root)
    {
        if (!TryGetProperty(root, out var foldersElement, "folders"))
        {
            return [];
        }

        var folders = new List<EagleFolder>();
        ReadFolderElement(foldersElement, null, folders, 0);
        return folders.GroupBy(folder => folder.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }

    private static int ReadFolderElement(JsonElement element, string? parentId, List<EagleFolder> folders, int sortOrder)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                sortOrder = ReadFolderElement(item, parentId, folders, sortOrder);
            }

            return sortOrder;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return sortOrder;
        }

        var id = ReadString(element, "id", "uuid", "folderId");
        var name = ReadString(element, "name", "title");
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
        {
            folders.Add(new EagleFolder { Id = id, Name = name, ParentId = parentId, SortOrder = sortOrder++ });
            parentId = id;
        }

        if (TryGetProperty(element, out var children, "children", "folders"))
        {
            sortOrder = ReadFolderElement(children, parentId, folders, sortOrder);
        }

        return sortOrder;
    }

    private static void EnsureReferencedFolders(List<EagleFolder> folders, IEnumerable<EagleAsset> assets)
    {
        var knownFolderIds = folders.Select(folder => folder.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folderId in assets.SelectMany(asset => asset.FolderIds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (knownFolderIds.Contains(folderId))
            {
                continue;
            }

            folders.Add(new EagleFolder { Id = folderId, Name = folderId, SortOrder = folders.Count });
            knownFolderIds.Add(folderId);
        }
    }

    private static void NormalizeFolderPaths(List<EagleFolder> folders)
    {
        var byId = folders.ToDictionary(folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            folder.Path = BuildFolderPath(folder, byId, []);
        }
    }

    private static string BuildFolderPath(EagleFolder folder, IReadOnlyDictionary<string, EagleFolder> byId, HashSet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(folder.ParentId)
            || !byId.TryGetValue(folder.ParentId, out var parent)
            || !visited.Add(folder.Id))
        {
            return folder.Name;
        }

        return string.Join(" / ", BuildFolderPath(parent, byId, visited), folder.Name);
    }
    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return [];
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            return string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var value = ReadString(item, "id", "name", "title");
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
            }
        }

        return values;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static long ReadLong(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return 0;
        }

        return ReadLongValue(property);
    }

    private static bool ReadBoolean(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number != 0;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            return bool.TryParse(value, out var boolean)
                ? boolean
                : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number != 0;
        }

        return false;
    }

    private static long ReadLongValue(JsonElement property)
    {
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                return parsedDate;
            }

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumber))
            {
                return FromUnixTime(parsedNumber);
            }
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return FromUnixTime(number);
        }

        return null;
    }

    private static DateTimeOffset? ReadIsoDate(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? FromUnixTime(long value)
    {
        try
        {
            return value > 9_999_999_999
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement property, params string[] names)
    {
        property = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in names)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private static string CleanLibraryName(string name)
    {
        return name.EndsWith(".library", StringComparison.OrdinalIgnoreCase) ? name[..^".library".Length] : name;
    }

    private static string TrimInfoSuffix(string name)
    {
        return name.EndsWith(".info", StringComparison.OrdinalIgnoreCase) ? name[..^".info".Length] : name;
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var normalized = extension.Trim();
        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }

    private static string ExtractFileId(string contentUri)
    {
        const string prefix = "gdrive://files/";
        if (!contentUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var value = contentUri[prefix.Length..];
        var cutIndex = value.IndexOfAny(['?', '/', '&', '#']);
        return cutIndex >= 0 ? value[..cutIndex] : value;
    }

    private static string ExtractFileName(string contentUri)
    {
        const string prefix = "gdrive://files/";
        if (!contentUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var value = contentUri[prefix.Length..];
        var slashIndex = value.IndexOf('/');
        if (slashIndex < 0 || slashIndex >= value.Length - 1)
        {
            return string.Empty;
        }

        return Uri.UnescapeDataString(value[(slashIndex + 1)..]);
    }

    private static string ExtractQueryValue(string query, string key)
    {
        var normalized = query.TrimStart('?');
        foreach (var part in normalized.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && string.Equals(Uri.UnescapeDataString(pieces[0]), key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pieces[1]);
            }
        }

        return string.Empty;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt32(JsonElement element, string propertyName, int fallback)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(value.GetString(), out var parsed) ? parsed : fallback;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static string GetErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return GetString(document.RootElement, "error");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var description = GetString(root, "error_description");
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? body;
                }

                if (error.ValueKind == JsonValueKind.Object)
                {
                    var message = GetString(error, "message");
                    return string.IsNullOrWhiteSpace(message) ? body : message;
                }
            }
        }
        catch
        {
            // The response may be plain text or HTML.
        }

        return body.Length > 300 ? body[..300] : body;
    }

    private sealed class AssetScanResult
    {
        public List<EagleAsset> Assets { get; } = [];
        public List<EagleSourceEntry> SourceEntries { get; } = [];
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Deleted { get; set; }
        public int Unchanged { get; set; }
        public int Retry { get; set; }
        public int Processed { get; set; }
        public int VisitedDirectories { get; set; }
        public int DirectoryReadFailures { get; set; }
        public int DiscoveryReadFailures { get; set; }
        public int InfoDirectories { get; set; }
        public int MetadataFiles { get; set; }
        public int MetadataMissing { get; set; }
        public int ImageFiles { get; set; }
        public int MediaFiles { get; set; }
        public int AssetReadFailures { get; set; }
        public int ReusedAssets { get; set; }
        public int RetainedAssets { get; set; }
        public bool MtimeFound { get; set; }
        public bool MtimeReadFailed { get; set; }
        public int MtimeAssetIds { get; set; }
        public int MtimeDeclaredTotal { get; set; }
        public int MtimeMissingIds { get; set; }
        public int TrashDirectories { get; set; }

        public string ToDifferenceMessage()
        {
            return $"追加 {Added:N0} / 変更 {Changed:N0} / 削除 {Deleted:N0} / 変更なし {Unchanged:N0} / 再試行 {Retry:N0} / 一時保持 {RetainedAssets:N0}";
        }

        public string ToMessage()
        {
            var mtimeMessage = ", mtime none";
            if (MtimeFound)
            {
                var mtimeTotal = MtimeDeclaredTotal > 0 ? MtimeDeclaredTotal : MtimeAssetIds;
                mtimeMessage = MtimeMissingIds > 0
                    ? $", mtime {mtimeTotal}, hints {MtimeAssetIds}, not-found {MtimeMissingIds}"
                    : $", mtime {mtimeTotal}, ok";
            }

            if (MtimeReadFailed)
            {
                mtimeMessage += ", mtime-read-fail";
            }

            var trashMessage = TrashDirectories > 0 ? $", trash {TrashDirectories}" : string.Empty;
            return $"{Assets.Count} 件をDrive APIで索引化 / {ToDifferenceMessage()} / dirs {VisitedDirectories}, .info {InfoDirectories}, metadata {MetadataFiles}, reused {ReusedAssets}, images {ImageFiles}, media {MediaFiles}, read-fail {DirectoryReadFailures + AssetReadFailures}, metadata-missing {MetadataMissing}{mtimeMessage}{trashMessage}";
        }
    }

    private sealed record AssetReadResult(EagleAsset? Asset, string State);

    private sealed record MtimeIndex(
        bool Found,
        bool ReadFailed,
        IReadOnlyDictionary<string, long> AssetModifiedAt,
        int DeclaredTotal,
        long ModifiedStamp)
    {
        public static MtimeIndex Empty { get; } = new(
            false,
            false,
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            0,
            0);
    }

    private sealed record DriveEntry(string Id, string Name, string MimeType, long Size, DateTimeOffset? CreatedAt, DateTimeOffset? ModifiedAt)
    {
        public bool IsFolder => string.Equals(MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal);
    }
}

public sealed class GoogleDriveReconnectRequiredException(string message) : InvalidOperationException(message);
