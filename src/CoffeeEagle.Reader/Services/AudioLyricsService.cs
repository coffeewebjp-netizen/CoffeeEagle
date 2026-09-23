using CoffeeEagle.Offline;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class AudioLyricsService(AndroidDocumentTreeService documents, GoogleDriveLibraryService drive)
{
    public async Task<byte[]?> ReadAsync(EagleAsset asset, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(asset.LyricsUri))
        {
            var path = new Uri(asset.LyricsUri).LocalPath;
            if (!File.Exists(path)) return null;
            await using var local = File.OpenRead(path);
            return await LrcLyrics.ReadAsync(local, ct);
        }
        if (GoogleDriveLibraryService.IsDriveFileUri(asset.FileUri ?? "")) return await drive.ReadLyricsAsync(asset, ct);
        if (Uri.TryCreate(asset.FileUri, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var directory = Path.GetDirectoryName(uri.LocalPath)!;
            var sibling = LrcLyrics.FindSibling(Path.GetFileName(uri.LocalPath), Directory.EnumerateFiles(directory), Path.GetFileName);
            if (sibling is null) return null;
            await using var local = File.OpenRead(sibling);
            return await LrcLyrics.ReadAsync(local, ct);
        }
        if (string.IsNullOrWhiteSpace(asset.SourceDirectoryUri))
            throw new IOException("歌詞を探すため、ライブラリを一度更新してください。");
        return await Task.Run(async () =>
        {
            var folder = Android.Net.Uri.Parse(asset.SourceDirectoryUri)!;
            var id = Android.Provider.DocumentsContract.GetDocumentId(folder)!;
            documents.RequestProviderRefresh(asset.SourceDirectoryUri);
            var files = documents.ListChildren(asset.SourceDirectoryUri, id, requireListing: true);
            ct.ThrowIfCancellationRequested();
            var original = files.FirstOrDefault(x => x.Uri == asset.FileUri);
            if (original is null) throw new IOException("音声フォルダの一覧が不完全です。接続を確認して歌詞の更新を再試行してください。");
            var sibling = LrcLyrics.FindSibling(original.Name, files.Where(x => !x.IsDirectory), x => x.Name);
            if (sibling is null) return null;
            if (sibling.Size > LrcLyrics.MaxBytes) throw new InvalidDataException("歌詞ファイルは64 KB以内にしてください。");
            await using var source = documents.OpenRead(sibling.Uri);
            return await LrcLyrics.ReadAsync(source, ct);
        }, ct);
    }
}
