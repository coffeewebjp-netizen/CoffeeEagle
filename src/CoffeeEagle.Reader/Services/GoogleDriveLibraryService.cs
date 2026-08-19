using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class GoogleDriveLibraryService
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
}

public sealed class GoogleDriveReconnectRequiredException(string message) : InvalidOperationException(message);
