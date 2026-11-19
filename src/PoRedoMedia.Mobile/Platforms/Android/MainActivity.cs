using Android.App;
using Android.Content;
using Android.Content.PM;
using PoRedoMedia.Mobile.Services;

namespace PoRedoMedia.Mobile;

// poredomedia://media/{id}: what a "your remix is ready" notification opens.
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = MobileConfig.AppScheme,
    DataHost = "media")]
public class MainActivity : MauiAppCompatActivity
{
}
