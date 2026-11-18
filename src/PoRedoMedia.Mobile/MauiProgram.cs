using Microsoft.Extensions.Logging;
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
        builder.Services.AddSingleton<OfflineMediaQueue>();
        builder.Services.AddTransient<AuthDelegatingHandler>();
        builder.Services.AddSingleton(sp =>
        {
            var handler = sp.GetRequiredService<AuthDelegatingHandler>();
            handler.InnerHandler = new HttpClientHandler();
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoRedoMedia.Mobile/1.0");
            return http;
        });
        builder.Services.AddSingleton<PoRedoMediaMobileClient>();

        builder.Services.AddTransient<CreatePage>();
        builder.Services.AddTransient<RunsPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
