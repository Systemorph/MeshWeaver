using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using MeshWeaver.Hosting.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MeshWeaver.Portal.ServiceDefaults;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class ServiceDefaults
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.AddAppInsights();
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });
        builder.Services.AddRequestTimeouts();
        builder.Services.AddOutputCache();

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter("Microsoft.Orleans")
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource("Microsoft.Orleans.Runtime");
                tracing.AddSource("Microsoft.Orleans.Application");
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing2 =>
                        // Don't trace requests to the health endpoint to avoid filling the dashboard with noise
                        tracing2.Filter = httpContext =>
                            !(httpContext.Request.Path.StartsWithSegments("/health")
                              || httpContext.Request.Path.StartsWithSegments("/alive"))
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static void AddAppInsights(this IHostApplicationBuilder builder)
    {
        var appInsightsConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
        if (!string.IsNullOrEmpty(appInsightsConnectionString))
        {
            builder.Logging.AddApplicationInsights(config =>
            {
                config.ConnectionString = appInsightsConnectionString;
            }, options =>
            {
                options.TrackExceptionsAsExceptionTelemetry = true;
                options.IncludeScopes = true;
                options.FlushOnDispose = true;
            });
        }
    }
    private static void AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
        if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            builder.Services.AddOpenTelemetry()
                .UseAzureMonitor();
        }
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddRequestTimeouts(
            configure: static timeouts =>
                timeouts.AddPolicy("HealthChecks", TimeSpan.FromSeconds(20)));

        builder.Services.AddOutputCache(
            configureOptions: static caching =>
                caching.AddPolicy("HealthChecks",
                    build: static policy => policy.Expire(TimeSpan.FromSeconds(20))));

        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.UseRequestTimeouts();

        // All health checks must pass for app to be considered ready to accept traffic after starting
        app.MapHealthChecks("/health");

        // Only health checks tagged with the "live" tag must pass for app to be considered alive
        app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        return app;
    }

    public static void ConfigurePostgreSqlContext(this IHostApplicationBuilder builder)
    {
        // First, set up the Npgsql data source with the password provider
        builder.AddNpgsqlDataSource(
            "meshweaverdb",
            configureDataSourceBuilder: (dataSourceBuilder) =>
            {
                if (string.IsNullOrEmpty(dataSourceBuilder.ConnectionStringBuilder.Password))
                {
                    var credentials = new DefaultAzureCredential();
                    var tokenRequest = new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]);

                    dataSourceBuilder.UsePasswordProvider(
                        passwordProvider: _ => credentials.GetToken(tokenRequest).Token,
                        passwordProviderAsync: async (_, ct) =>
                            (await credentials.GetTokenAsync(tokenRequest, ct)).Token);
                }
            });

        builder.Services.AddDbContextPool<MeshWeaverDbContext>((serviceProvider, dbContextOptionsBuilder) =>
        {
            var dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();

            dbContextOptionsBuilder.UseNpgsql(dataSource,
                builder =>
                {
                    builder.EnableRetryOnFailure();
                });
        });
    }
}
