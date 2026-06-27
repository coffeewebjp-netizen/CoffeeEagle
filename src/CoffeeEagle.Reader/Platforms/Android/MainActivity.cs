using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidUri = Android.Net.Uri;
using Android.OS;

namespace CoffeeEagle.Reader;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int OpenDocumentTreeRequestCode = 7310;
    private static TaskCompletionSource<AndroidUri?>? pendingDocumentTree;

    public static MainActivity? Current { get; private set; }

    public static Task<AndroidUri?> PickDocumentTreeAsync()
    {
        var activity = Current ?? throw new InvalidOperationException("Android activity is not ready.");

        pendingDocumentTree?.TrySetCanceled();
        pendingDocumentTree = new TaskCompletionSource<AndroidUri?>();

        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(
            ActivityFlags.GrantReadUriPermission
            | ActivityFlags.GrantPersistableUriPermission
            | ActivityFlags.GrantPrefixUriPermission);
        activity.StartActivityForResult(intent, OpenDocumentTreeRequestCode);
        return pendingDocumentTree.Task;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != OpenDocumentTreeRequestCode)
        {
            return;
        }

        var completion = pendingDocumentTree;
        pendingDocumentTree = null;

        if (completion is null)
        {
            return;
        }

        if (resultCode != Result.Ok || data?.Data is null)
        {
            completion.TrySetResult(null);
            return;
        }

        try
        {
            ContentResolver?.TakePersistableUriPermission(data.Data, ActivityFlags.GrantReadUriPermission);
        }
        catch
        {
            // Some providers grant a transient tree URI only. The current session can still index it.
        }

        completion.TrySetResult(data.Data);
    }
}


