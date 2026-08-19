using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class GoogleDriveLibraryService
{
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
}
