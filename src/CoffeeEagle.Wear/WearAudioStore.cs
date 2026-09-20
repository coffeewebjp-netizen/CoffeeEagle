using Android.App;
using Android.OS;
using CoffeeEagle.Offline;

namespace CoffeeEagle.Wear;

internal static class WearAudioStore
{
    private static readonly Lazy<OfflineAudioStore> Instance = new(() =>
    {
        var files = Application.Context.FilesDir!.AbsolutePath;
        return new(Path.Combine(files, "offline-audio-v1"), 2 * OfflineAudioStore.GiB, () =>
        {
            using var stats = new StatFs(files);
            return stats.AvailableBytes;
        });
    });
    public static OfflineAudioStore Current => Instance.Value;
}
