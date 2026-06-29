using System;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// DI wiring for the Social publishing subsystem. Call
/// <see cref="AddSocialPublishing"/> on the app's service collection (or MeshBuilder
/// wrapper) at startup. The hosting app is responsible for providing the three
/// glue implementations:
///   - <see cref="IApprovalPublishBridge"/>  (approval → publishable snapshot; stats/publish-result persistence)
///   - <see cref="IStatsRefreshSource"/>     (which published posts are due for stats refresh)
///   - <see cref="IPastPostIngestSource"/> + <see cref="IPastPostSink"/>  (history ingest target + upsert)
///
/// Options are bound from configuration section <c>Social</c>; LinkedIn/X options
/// from <c>Social:LinkedIn</c> and <c>Social:Twitter</c> respectively. Missing
/// credentials disable the corresponding publisher at DI time rather than failing
/// at runtime.
/// </summary>
public static class SocialExtensions
{
    /// <summary>
    /// Registers <see cref="LinkedInPublisher"/>, <see cref="XPublisher"/>, the in-memory
    /// publish queue, and the three hosted services (approval handler, scheduler, stats
    /// refresher, history ingest). Call once at app startup.
    /// </summary>
    public static IServiceCollection AddSocialPublishing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Options from "Social" section (optional; all fields have sensible defaults).
        var options = new SocialOptions();
        configuration.GetSection("Social").Bind(options);
        services.AddSingleton(options);

        // Shared queue (in-memory default).
        services.TryAddSingleton<IPublishQueue, InMemoryPublishQueue>();

        // Platform publishers — gated by the presence of a client id so unconfigured
        // platforms don't end up in DI. Values come from user-secrets / env vars under
        // Social:LinkedIn:* and Social:Twitter:* (no entry in appsettings required).
        //
        // Lifetime: AddHttpClient<T> registers T as TRANSIENT by default. We want a
        // single publisher instance per app, so we register the typed HttpClient (gives
        // us factory-managed HttpClient lifetime + Polly extensibility), then register
        // the publisher ITSELF as a singleton resolved lazily through the typed-client
        // factory. The IPlatformPublisher alias points at the SAME singleton, so direct
        // and via-interface resolution return the same instance.
        var anyConfigured = false;
        var linkedInClientId = configuration["Social:LinkedIn:ClientId"];
        if (!string.IsNullOrEmpty(linkedInClientId))
        {
            services.AddHttpClient(nameof(LinkedInPublisher));
            services.AddSingleton(new LinkedInOptions
            {
                ClientId = linkedInClientId!,
                ClientSecret = configuration["Social:LinkedIn:ClientSecret"] ?? ""
            });
            services.AddSingleton(sp => new LinkedInPublisher(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(LinkedInPublisher)),
                sp.GetRequiredService<LinkedInOptions>(),
                sp.GetService<ILogger<LinkedInPublisher>>()));
            services.AddSingleton<IPlatformPublisher>(sp => sp.GetRequiredService<LinkedInPublisher>());
            anyConfigured = true;
        }

        var xClientId = configuration["Social:Twitter:ClientId"];
        if (!string.IsNullOrEmpty(xClientId))
        {
            services.AddHttpClient(nameof(XPublisher));
            services.AddSingleton(new XOptions
            {
                ClientId = xClientId!,
                ClientSecret = configuration["Social:Twitter:ClientSecret"] ?? ""
            });
            services.AddSingleton(sp => new XPublisher(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(XPublisher)),
                sp.GetRequiredService<XOptions>(),
                sp.GetService<ILogger<XPublisher>>()));
            services.AddSingleton<IPlatformPublisher>(sp => sp.GetRequiredService<XPublisher>());
            anyConfigured = true;
        }

        // Hosted services run only when at least one platform is configured.
        // Otherwise they would resolve zero IPlatformPublisher instances and
        // either no-op forever (silent misconfiguration) or fault on missing
        // bridge dependencies at the first tick.
        if (anyConfigured)
        {
            services.AddHostedService<ApprovalToPublishHandler>();
            services.AddHostedService<ScheduledPostPublisher>();
            services.AddHostedService<PostStatsRefresher>();
            services.AddHostedService<PastPostIngestJob>();
        }

        return services;
    }
}
