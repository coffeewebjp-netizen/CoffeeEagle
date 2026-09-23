using System.Net.Http.Headers;
using System.Text.Json;
using CoffeeEagle.Offline;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class GoogleDriveLibraryService
{
    public async Task<byte[]?> ReadLyricsAsync(EagleAsset asset, CancellationToken ct)
    {
        var state = await _store.LoadAsync(ct);
        var folderId = ExtractFolderId(asset.SourceDirectoryUri ?? "");
        if (string.IsNullOrWhiteSpace(folderId))
        {
            // Older catalogs have no parent locator. Resolve the actual Drive parent once per read.
            var fileId = ExtractFileId(asset.FileUri ?? "");
            if (string.IsNullOrWhiteSpace(fileId)) throw new IOException("ライブラリを更新してから歌詞を読み直してください。");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{DriveFilesUrl}/{Uri.EscapeDataString(fileId)}?supportsAllDrives=true&fields=parents");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetValidAccessTokenAsync(state, ct));
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!data.RootElement.TryGetProperty("parents", out var parents) || parents.GetArrayLength() != 1)
                throw new IOException("音声の保存フォルダを確認できません。ライブラリを更新してください。");
            folderId = parents[0].GetString()!;
        }
        var files = await ListChildrenAsync(state, folderId, ct);
        var original = files.FirstOrDefault(x => x.Id == ExtractFileId(asset.FileUri ?? ""));
        if (original is null) throw new IOException("音声フォルダの一覧が不完全です。接続を確認して歌詞の更新を再試行してください。");
        var sibling = LrcLyrics.FindSibling(original.Name, files.Where(x => !x.IsFolder), x => x.Name);
        if (sibling is null) return null;
        if (sibling.Size > LrcLyrics.MaxBytes) throw new InvalidDataException("歌詞ファイルは64 KB以内にしてください。");
        await using var input = await OpenDownloadStreamAsync(BuildFileUri(sibling.Id, sibling.Name), ct);
        return await LrcLyrics.ReadAsync(input, ct);
    }
}
