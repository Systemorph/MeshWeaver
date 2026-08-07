using MeshWeaver.Domain;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Observability;

/// <summary>
/// Entry point for red-log ticketing: the mesh side of the log watcher. Registers the
/// <c>LogIncident</c> node type, the ingest + filing services, and the control plane that walks an
/// incident from "just seen" to "issue filed".
///
/// <para>The detector itself lives OUTSIDE the portal (a small service in the cluster's
/// <c>monitoring</c> namespace, polling Loki). That split is the point: the component that notices
/// the portal is unhealthy must not be hosted by the portal. See
/// <c>Doc/Architecture/LogWatchTriage.md</c>.</para>
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Registers red-log ticketing on the mesh builder.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="configuration">Configuration to bind <see cref="LogWatchOptions"/> from
    /// (the <c>LogWatch</c> section). When null, the defaults apply and nothing is filed until a
    /// destination is configured.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddLogWatch<TBuilder>(this TBuilder builder, IConfiguration? configuration = null)
        where TBuilder : MeshBuilder
        => (TBuilder)builder
            .AddLogIncidentType()
            // Platform state, not pickable content: an incident is never a node a user links to.
            .AddAutocompleteExcludedTypes(LogIncidentNodeType.NodeType)
            .ConfigureServices(services =>
            {
                if (configuration is not null)
                    services.Configure<LogWatchOptions>(
                        configuration.GetSection(LogWatchOptions.SectionName));
                else
                    services.Configure<LogWatchOptions>(_ => { });

                return services
                    .AddSingleton<LogIncidentIngestService>()
                    // Explicit factory, not AddSingleton<T>(): the GitHub dependencies are OPTIONAL.
                    // Ingest is useful without them (incidents are recorded and visible), so a host
                    // that has not wired MeshWeaver.GitSync must still start — the filer then says
                    // exactly why it cannot file instead of the container failing to activate.
                    .AddSingleton(sp => new LogIncidentFiler(
                        sp.GetService<IGitHubRepoClient>(),
                        sp.GetService<GitHubAppTokenService>(),
                        sp.GetRequiredService<IOptions<LogWatchOptions>>(),
                        sp.GetService<ILogger<LogIncidentFiler>>()))
                    // Mesh-scoped singleton so its subscriptions and in-flight set live and die
                    // with the mesh (Doc/Architecture/NoStaticState.md); the IHostedService
                    // forward is what actually STARTS it — a bare singleton would never subscribe.
                    .AddSingleton<LogIncidentControlPlane>()
                    .AddSingleton<IHostedService>(sp => sp.GetRequiredService<LogIncidentControlPlane>());
            })
            .ConfigureHub(config =>
            {
                config.TypeRegistry.AddObservabilityTypes();
                return config;
            })
            .ConfigureDefaultNodeHub(config =>
            {
                config.TypeRegistry.AddObservabilityTypes();
                return config;
            });

    /// <summary>
    /// Registers the observability content types under their short names, so incident content
    /// round-trips between the mesh hub and the per-node hub that owns it.
    /// </summary>
    /// <param name="typeRegistry">The type registry to register into.</param>
    /// <returns>The same registry, for chaining.</returns>
    public static ITypeRegistry AddObservabilityTypes(this ITypeRegistry typeRegistry)
    {
        ArgumentNullException.ThrowIfNull(typeRegistry);
        typeRegistry.WithType(typeof(LogIncident), nameof(LogIncident));
        typeRegistry.WithType(typeof(LogIncidentDraft), nameof(LogIncidentDraft));
        typeRegistry.WithType(typeof(LogSample), nameof(LogSample));
        typeRegistry.WithType(typeof(LogSeverity), nameof(LogSeverity));
        typeRegistry.WithType(typeof(LogIncidentStatus), nameof(LogIncidentStatus));
        typeRegistry.WithType(typeof(LogIncidentRequest), nameof(LogIncidentRequest));
        return typeRegistry;
    }
}
