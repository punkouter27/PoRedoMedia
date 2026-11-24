using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using PoRedoMedia.Api.Common.Health;
using PoRedoMedia.Api.Components;
using PoRedoMedia.Api.Configuration;
using PoRedoMedia.Api.Features.Auth;
using PoRedoMedia.Api.Features.BulkStyles;
using PoRedoMedia.Api.Features.Comics;
using PoRedoMedia.Api.Features.Video;
using PoRedoMedia.Api.Features.Housekeeping;
using PoRedoMedia.Api.Features.Media;
using PoRedoMedia.Api.Features.MemeCaption;
using PoRedoMedia.Api.Features.Movies;
using PoRedoMedia.Api.Features.Preferences;
using PoRedoMedia.Api.Features.Publishing;
using PoRedoMedia.Api.Features.Quota;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.Runs;
using PoRedoMedia.Api.Features.Sharing;
using FluentValidation;
using PoRedoMedia.Shared.Models;
using PoRedoMedia.Shared.Validation;
using Serilog;

// CLI verb: dotnet run --project src/PoRedoMedia.Api -- seed-sounds [--seeds-dir <path>]
// Fills the sound library and exits, without starting the web host.
if (args.Length > 0 && args[0] == "seed-sounds")
{
    var seedConfiguration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.Development.json", optional: true)
        .AddEnvironmentVariables()
        .Build();
    return await SeedSoundsCommand.RunAsync(args[1..], seedConfiguration);
}

// Serilog: this console logger covers startup until the host's own (AddPoSerilog) takes over,
// and records why the host could not start.
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // The static web assets manifest is wired automatically only in Development. Test runs from
    // build output too, and without this every asset answers empty and the WASM app never boots.
    if (builder.Environment.IsEnvironment(PoEnvironments.Test))
        builder.WebHost.UseStaticWebAssets();

    builder.AddPoRedoMediaKeyVault();
    builder.AddPoSerilog();
    builder.AddPoTelemetry();

    // Errors leave as RFC 7807 problem details; an unhandled exception is a 500 problem with nothing of the exception in it.
    builder.Services.AddProblemDetails();
    builder.Services.AddPoHealthChecks();
    builder.Services.AddRazorComponents()
        .AddInteractiveWebAssemblyComponents()
        // Every claim goes to the client, so the header can show the account's email (Microsoft's name claim is the display name).
        .AddAuthenticationStateSerialization(o => o.SerializeAllClaims = true);
    builder.Services.AddSingleton<StorageClients>();
    builder.Services.AddSingleton<BlobStorageService>();
    builder.Services.AddSingleton<BlobDelivery>();
    builder.Services.AddSingleton<FFmpegProcess>();
    builder.Services.AddSingleton<IMediaRepository, MediaTableRepository>();
    builder.Services.AddSingleton<Thumbnails>();
    builder.Services.AddSingleton<PoRedoMedia.Api.Features.Media.StorageUsage>();
    builder.Services.AddSingleton<MediaOutputs>();
    builder.Services.AddSingleton<ShareLinkStore>();
    builder.Services.AddSingleton<PreferencesRepository>();
    builder.Services.AddSingleton<IShareLinks>(services => services.GetRequiredService<ShareLinkStore>());
    builder.Services.AddFunctions(builder.Configuration, builder.Environment);
    builder.Services.AddMovies();
    builder.Services.AddComics(builder.Configuration, builder.Environment);
    builder.Services.AddPublishing(builder.Configuration, builder.Environment);
    builder.Services.AddSingleton(TimeProvider.System);
    // The form rules (Shared/Validation), one registration each by type; endpoints take IValidator<T>.
    builder.Services.AddScoped<IValidator<ComicRequest>, ComicRequestValidator>();
    builder.Services.AddScoped<IValidator<MovieLessonRequest>, MovieLessonRequestValidator>();
    builder.Services.AddScoped<IValidator<MovieUploadRequest>, MovieUploadRequestValidator>();
    builder.Services.AddScoped<IValidator<PublishRequest>, PublishRequestValidator>();
    builder.Services.AddSingleton<IRenderQuota, RenderQuotaService>();
    builder.Services.AddSingleton<IRunRepository, RunTableRepository>();
    builder.Services.AddSingleton<IRunNotifier, RunHubNotifier>();
    builder.Services.AddSingleton<RunExecutor>();
    builder.Services.AddSingleton<RunDispatcher>();
    builder.Services.AddHostedService(services => services.GetRequiredService<RunDispatcher>());
    builder.Services.AddHostedService<HousekeepingService>();
    builder.Services.AddHostedService<SoundLibrarySeeder>();
    builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    // Wire enums travel as their names, matching the client's source-generated JSON.
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddUploadRateLimit();
    builder.Services.AddPoAntiforgery(builder.Environment);
    builder.Services.AddPoRedoMediaAuth(builder.Configuration, builder.Environment);

    // Deployed, the app sits behind the platform's proxy. Without its forwarded scheme the sign-in
    // redirect and the share links would say http.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

    var app = builder.Build();

    app.UseExceptionHandler();

    // A browser that asks for a page that does not exist gets the app's own "Page not found" (Layout/NotFound) inside
    // the shell, with a real 404. Before this it got an empty 404, which Chrome shows as its own error page.
    // An unmatched page navigation is routed again as /not-found (which serves the app; the browser keeps its address,
    // so the client router shows NotFound) and answers 404. Not a re-execute: Blazor leaves the signed-in user out of
    // a re-executed page, so the header would show signed out. Only page navigations: an unknown /api path answers
    // JSON (the /api catch-all below), and files, hubs and probes keep their plain status. An endpoint's own empty 404
    // to a page navigation (a revoked share link) is re-executed to the same page.
    app.UseStatusCodePagesWithReExecute("/not-found");
    app.UseRouting();
    app.Use((context, next) =>
    {
        if (!IsPageNavigation(context.Request))
        {
            if (context.Features.Get<IStatusCodePagesFeature>() is { } pages)
                pages.Enabled = false;
        }
        else if (context.GetEndpoint() is null && context.Request.Path != "/not-found")
        {
            context.Request.Path = "/not-found";
            context.Response.OnStarting(() =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        }
        return next();
    });
    // Matches again only when the request above was sent to /not-found (an already matched request is left alone).
    app.UseRouting();

    if (!app.Environment.IsDevOrTest())
    {
        app.UseForwardedHeaders();
        app.UseHsts();
    }

    // The commit this build came from (SDK-stamped as "<version>+<sha>"), so a deploy can prove which
    // build is answering: CI checks /health/live for X-Commit equal to the pushed sha.
    var commit = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+') is [_, var sha, ..] ? sha : "unknown";
    app.Use((context, next) =>
    {
        var headers = context.Response.Headers;
        headers["X-Commit"] = commit;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        return next();
    });

    app.UseSerilogRequestLogging(o => o.GetLevel = (context, _, ex) =>
        ex is not null || context.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
        : context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/_framework") ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Information);

    app.UseAuthentication();

    // The WASM boot assets under /_framework must load for signed-out users, or the login page cannot
    // start. Most are static files, but the runtime-generated boot manifest is not, so
    // MapStaticAssets().AllowAnonymous() misses it and the fallback policy would redirect it to /login.
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/_framework")
            && context.GetEndpoint() is { } endpoint
            && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
        {
            context.SetEndpoint(new Endpoint(
                endpoint.RequestDelegate,
                new EndpointMetadataCollection([.. endpoint.Metadata, new AllowAnonymousAttribute()]),
                endpoint.DisplayName));
        }

        await next();
    });

    app.UseAuthorization();
    app.UseRateLimiter();
    app.UseAntiforgery();

    app.MapHealth();
    app.MapAntiforgeryToken();
    app.MapAppConfig();
    app.MapAuthEndpoints();
    app.MapMedia();
    app.MapDeviceResults();
    app.MapQuota();
    app.MapRuns();
    app.MapMemeTemplates();
    app.MapBulkPrompts();
    app.MapExports();
    app.MapSharing();
    app.MapPreferences();
    app.MapMovies();
    app.MapComics();
    app.MapPublishing();
    app.MapClientErrors();
    app.MapHub<RunHub>(RunHub.Path);
    // Any other /api path is a JSON 404, never the app's HTML page.
    app.Map("/api/{**rest}", () => Results.Problem(detail: "There is no such API route.", statusCode: StatusCodes.Status404NotFound));
    app.MapStaticAssets().AllowAnonymous();
    app.MapRazorComponents<App>()
        .AddInteractiveWebAssemblyRenderMode()
        .AddAdditionalAssemblies(typeof(PoRedoMedia.Client.Routes).Assembly)
        .AllowAnonymous();

    // The browser uploads to and reads from blob storage directly, so storage must accept the app's
    // origin. Any origin is allowed: every such request already needs a signed, expiring link.
    if (app.Services.GetRequiredService<StorageClients>() is { IsConfigured: true } storage)
    {
        try
        {
            await storage.AllowBrowserAccessAsync("*");
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or AggregateException)
        {
            app.Logger.LogWarning(ex, "Storage is not reachable; uploads will fail until it is and the app is restarted");
        }
    }

    app.Run();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "PoRedoMedia could not start");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>A browser asking for a page: a GET for HTML, outside the API, hubs, probes and files with an extension.</summary>
static bool IsPageNavigation(HttpRequest request) =>
    HttpMethods.IsGet(request.Method)
    && !request.Path.StartsWithSegments("/api")
    && !request.Path.StartsWithSegments("/hubs")
    && !request.Path.StartsWithSegments("/health")
    && !request.Path.StartsWithSegments("/_framework")
    && !request.Path.StartsWithSegments("/_content")
    && !Path.HasExtension(request.Path.Value)
    && request.Headers.Accept.Any(a => a?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);

public partial class Program;
