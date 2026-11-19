using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using PoRedoMedia.Mobile.Pages;
using PoRedoMedia.Mobile.Services;

namespace PoRedoMedia.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureLifecycleEvents(ForwardLinks)
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<MobileConfig>();
        builder.Services.AddSingleton<SecureSessionStore>();
        builder.Services.AddSingleton<EntraAuthService>();
        builder.Services.AddSingleton(new CookieContainer());
        builder.Services.AddSingleton<CookieJar>();
        builder.Services.AddTransient<AuthDelegatingHandler>();
        builder.Services.AddSingleton(sp =>
        {
            var handler = sp.GetRequiredService<AuthDelegatingHandler>();
            handler.InnerHandler = new HttpClientHandler { CookieContainer = sp.GetRequiredService<CookieContainer>(), UseCookies = true };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoRedoMedia.Mobile/1.0");
            return http;
        });
        builder.Services.AddSingleton<ApiClient>();
        builder.Services.AddSingleton<Session>();
        builder.Services.AddSingleton<Transfers>();
#if ANDROID
        builder.Services.AddSingleton<IBackgroundWork, AndroidBackgroundWork>();
        builder.Services.AddSingleton<IGallery, AndroidGallery>();
        builder.Services.AddSingleton<IDownloader, AndroidDownloader>();
        builder.Services.AddSingleton<IVideoPlayer, AndroidVideoPlayer>();
        builder.Services.AddSingleton<IImagePrep, AndroidImagePrep>();
#elif IOS
        builder.Services.AddSingleton<IBackgroundWork, IosBackgroundWork>();
        builder.Services.AddSingleton<IGallery, IosGallery>();
        builder.Services.AddSingleton<IDownloader, IosDownloader>();
        builder.Services.AddSingleton<IVideoPlayer, IosVideoPlayer>();
        builder.Services.AddSingleton<IImagePrep, IosImagePrep>();
#endif

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<CreatePage>();
        builder.Services.AddTransient<ActivityPage>();
        builder.Services.AddTransient<LibraryPage>();
        builder.Services.AddTransient<MediaPage>();
        builder.Services.AddTransient<SettingsPage>();

        MauiApp app = builder.Build();
        app.Services.GetRequiredService<CookieJar>().RestoreAsync().GetAwaiter().GetResult();

        // The queue moves whenever the network comes back, and once at launch for anything a restart interrupted.
        Transfers transfers = app.Services.GetRequiredService<Transfers>();
        Connectivity.Current.ConnectivityChanged += (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                _ = transfers.PumpAsync();
            }
        };
        _ = transfers.PumpAsync();
        return app;
    }

    private static void ForwardLinks(ILifecycleBuilder events)
    {
#if ANDROID
        events.AddAndroid(android => android
            .OnCreate((activity, _) => Forward(activity.Intent))
            .OnNewIntent((_, intent) => Forward(intent)));

        static void Forward(Android.Content.Intent? intent)
        {
            if (intent?.Action == Android.Content.Intent.ActionView && intent.DataString is { } data && Uri.TryCreate(data, UriKind.Absolute, out var uri))
            {
                Application.Current?.SendOnAppLinkRequestReceived(uri);
            }
        }
#endif
    }
}
