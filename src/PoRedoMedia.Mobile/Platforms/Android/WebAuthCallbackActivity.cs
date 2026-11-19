using Android.App;
using Android.Content;
using Android.Content.PM;

namespace PoRedoMedia.Mobile;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "poredomedia",
    DataHost = "auth")]
public class WebAuthCallbackActivity : WebAuthenticatorCallbackActivity
{
}
