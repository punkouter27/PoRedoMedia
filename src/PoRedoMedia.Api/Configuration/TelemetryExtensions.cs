using System.Globalization;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

namespace PoRedoMedia.Api.Configuration;

public static class TelemetryExtensions
{
    /// <summary>
    /// Serilog is the host's logger: levels come from the <c>Serilog</c> section of appsettings,
    /// every event carries the app's name, and it writes to the console. Call it before <see cref="AddPoTelemetry"/>:
    /// the default providers are cleared here, and Application Insights' own log provider, added after, still gets
    /// every event (<c>writeToProviders</c>). The static <see cref="Log.Logger"/> is left alone (it is Program's
    /// startup logger), so test hosts can each build their own.
    /// </summary>
    public static void AddPoSerilog(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "PoRedoMedia")
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture),
            preserveStaticLogger: true, writeToProviders: true);
    }

    /// <summary>
    /// Sends requests, dependencies, logs, and the app's own run traces and metrics to Application
    /// Insights. Off when no connection string is configured, which is every test and most local runs.
    /// </summary>
    public static void AddPoTelemetry(this WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration[ConfigKeys.ApplicationInsightsConnectionString]
            ?? builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString) || builder.Environment.IsEnvironment(PoEnvironments.Test))
            return;

        builder.Services.AddOpenTelemetry()
            .UseAzureMonitor(o => o.ConnectionString = connectionString)
            .WithTracing(t => t.AddSource(PoTelemetry.Name))
            .WithMetrics(m => m.AddMeter(PoTelemetry.Name));
    }
}
