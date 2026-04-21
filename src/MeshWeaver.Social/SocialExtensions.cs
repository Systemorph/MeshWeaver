using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
        // Options from "Social" section.
        var socialSection = configuration.GetSection("Social");
        var options = new SocialOptions();
        socialSection.Bind(options);
        services.AddSingleton(options);

        // Shared queue (in-memory default).
        services.TryAddSingleton<IPublishQueue, InMemoryPublishQueue>();

        // Platform publishers — each gated by its config section so unconfigured
        // platforms don't end up in DI (the scheduler falls back to "no publisher
        // for this platform, drop" on missing entries, which is the desired UX).
        var liSection = socialSection.GetSection("LinkedIn");
        if (!string.IsNullOrEmpty(liSection["ClientId"]))
        {
            services.AddHttpClient<LinkedInPublisher>();
            var liOpts = new LinkedInOptions
            {
                ClientId = liSection["ClientId"]!,
                ClientSecret = liSection["ClientSecret"] ?? ""
            };
            services.AddSingleton(liOpts);
            services.AddSingleton<IPlatformPublisher>(sp => sp.GetRequiredService<LinkedInPublisher>());
        }

        var xSection = socialSection.GetSection("Twitter");
        if (!string.IsNullOrEmpty(xSection["ClientId"]))
        {
            services.AddHttpClient<XPublisher>();
            var xOpts = new XOptions
            {
                ClientId = xSection["ClientId"]!,
                ClientSecret = xSection["ClientSecret"] ?? ""
            };
            services.AddSingleton(xOpts);
            services.AddSingleton<IPlatformPublisher>(sp => sp.GetRequiredService<XPublisher>());
        }

        // Hosted services. Each is optional in the sense that its dependencies
        // (the three bridge interfaces) must be provided by the app; if any is
        // missing at resolution time, DI throws at startup — which is the
        // intended behavior: social publishing is all-or-nothing.
        services.AddHostedService<ApprovalToPublishHandler>();
        services.AddHostedService<ScheduledPostPublisher>();
        services.AddHostedService<PostStatsRefresher>();
        services.AddHostedService<PastPostIngestJob>();

        return services;
    }
}
