using System.Net.Http.Headers;

namespace CoffeeEagle.Reader.Services;

public sealed partial class GoogleDriveLibraryService
{
    // Offline saves bypass the browsing cache so a new source revision cannot reuse old bytes.
    public async Task<Stream> OpenDownloadStreamAsync(string uri, CancellationToken ct)
    {
        var id = ExtractFileId(uri);
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("DriveのファイルIDがありません。");
        var state = await _store.LoadAsync(ct);
        var token = await GetValidAccessTokenAsync(state, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{DriveFilesUrl}/{Uri.EscapeDataString(id)}?alt=media&supportsAllDrives=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (!response.IsSuccessStatusCode) throw new IOException($"Driveから音声を取得できませんでした (HTTP {(int)response.StatusCode})。");
            return new DownloadStream(await response.Content.ReadAsStreamAsync(ct), response);
        }
        catch { response.Dispose(); throw; }
    }

    private sealed class DownloadStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
