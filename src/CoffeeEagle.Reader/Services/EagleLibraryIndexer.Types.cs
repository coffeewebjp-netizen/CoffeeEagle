using System.Globalization;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class EagleLibraryIndexer
{

    private sealed record AssetReadResult(EagleAsset? Asset, string State)
    {
        public static AssetReadResult Active(EagleAsset asset) => new(asset, EagleSourceEntryState.Active);

        public static AssetReadResult Deleted { get; } = new(null, EagleSourceEntryState.Deleted);

        public static AssetReadResult MissingMetadata { get; } = new(null, EagleSourceEntryState.MissingMetadata);

        public static AssetReadResult ReadFailed { get; } = new(null, EagleSourceEntryState.ReadFailed);
    }


    private sealed class AssetScanResult
    {
        public List<EagleAsset> Assets { get; } = [];
        public List<EagleSourceEntry> SourceEntries { get; } = [];
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Deleted { get; set; }
        public int Unchanged { get; set; }
        public int Processed { get; set; }
        public int Total { get; set; }
        public int PreservedAssets { get; set; }
        public int RetryPending { get; set; }
        public long LastProgressAt { get; set; }
        public int VisitedDirectories { get; set; }
        public int DiscoveryReadFailures { get; set; }
        public int DirectoryReadFailures { get; set; }
        public int InfoDirectories { get; set; }
        public int MetadataFiles { get; set; }
        public int MetadataMissing { get; set; }
        public int ImageFiles { get; set; }
        public int MediaFiles { get; set; }
        public int AssetReadFailures { get; set; }
        public int ReusedAssets { get; set; }
        public bool MtimeFound { get; set; }
        public bool MtimeReadFailed { get; set; }
        public int MtimeAssetIds { get; set; }
        public int MtimeDeclaredTotal { get; set; }
        public int MtimeMissingIds { get; set; }
        public int MtimeDirectHits { get; set; }
        public int MtimeDirectFailures { get; set; }
        public int MtimeRecoveredIds { get; set; }
        public int TrashDirectories { get; set; }

        public string ToDifferenceMessage()
        {
            return $"追加 {Added:N0} / 変更 {Changed:N0} / 削除 {Deleted:N0} / 変更なし {Unchanged:N0} / 一時保持 {PreservedAssets:N0} / 再試行 {RetryPending:N0}";
        }

        public string ToMessage()
        {
            var mtimeMessage = ", mtime none";
            if (MtimeFound)
            {
                var mtimeTotal = MtimeDeclaredTotal > 0 ? MtimeDeclaredTotal : MtimeAssetIds;
                var unresolved = Math.Max(0, MtimeMissingIds - MtimeRecoveredIds);
                mtimeMessage = unresolved > 0 || MtimeRecoveredIds > 0
                    ? $", mtime {mtimeTotal}, unresolved {unresolved}, recovered {MtimeRecoveredIds}"
                    : $", mtime {mtimeTotal}, ok";
            }

            if (MtimeReadFailed)
            {
                mtimeMessage += ", mtime-read-fail";
            }

            var trashMessage = TrashDirectories > 0 ? $", trash {TrashDirectories}" : string.Empty;
            return $"{Assets.Count:N0} 件を索引化 / {ToDifferenceMessage()} / dirs {VisitedDirectories}, .info {InfoDirectories}, metadata {MetadataFiles}, reused {ReusedAssets}, images {ImageFiles}, media {MediaFiles}, read-fail {DiscoveryReadFailures + DirectoryReadFailures + AssetReadFailures}, metadata-missing {MetadataMissing}{mtimeMessage}{trashMessage}";
        }
    }


    private sealed record MtimeIndex(
        bool Found,
        bool ReadFailed,
        IReadOnlyDictionary<string, long> AssetModifiedAt,
        int DeclaredTotal)
    {
        public static MtimeIndex Empty { get; } = new(false, false, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), 0);
    }
}
