using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class GoogleDriveLibraryService
{

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
