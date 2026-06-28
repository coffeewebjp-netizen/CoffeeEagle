using Android.App;
using Android.Content;
using Android.Content.PM;

namespace CoffeeEagle.Reader;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "net.coffeewebjp.coffeeeagle.reader",
    DataPath = "/oauth2redirect")]
public sealed class WebAuthenticationCallbackActivity : WebAuthenticatorCallbackActivity
{
}