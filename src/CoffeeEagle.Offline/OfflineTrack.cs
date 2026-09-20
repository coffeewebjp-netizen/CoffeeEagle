using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CoffeeEagle.Offline;

public sealed record AudioRequest(string Key, string Title, string Revision, string Extension, long Length = -1, string? Sha256 = null)
{
    public static string KeyFor(string library, string asset) => Hash(library + "\n" + asset);
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wav", ".amr" };

    public void Validate(bool transferred = false)
    {
        if (!IsHash(Key) || !IsHash(Revision) || string.IsNullOrWhiteSpace(Title) || Title.Length > 256 ||
            !SupportedExtensions.Contains(Extension) || Length == 0 || Length < -1 || Length > OfflineAudioStore.MaxFileBytes ||
            (Sha256 is not null && !IsHash(Sha256)) || (transferred && (Length <= 0 || Sha256 is null)))
            throw new InvalidDataException("音声情報の形式またはファイルサイズが対応範囲外です。");
    }

    public static bool IsHash(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
}

public sealed record OfflineTrack(string Key, string Title, string Revision, string Extension, long Length, string Sha256, DateTimeOffset SavedAt)
{
    public AudioRequest Request => new(Key, Title, Revision, Extension, Length, Sha256);
    public string FileName => $"{Key}-{Sha256}{Extension.ToLowerInvariant()}";
}

public sealed record OfflineSnapshot(long LimitBytes, IReadOnlyList<OfflineTrack> Tracks)
{
    public long UsedBytes => Tracks.Sum(x => x.Length);
}

public sealed record AudioProgress(long Bytes, long Total);
