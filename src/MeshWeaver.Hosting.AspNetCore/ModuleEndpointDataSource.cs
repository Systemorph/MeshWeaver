using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace MeshWeaver.Hosting.AspNetCore;

/// <summary>
/// The HTTP endpoints of every module held in its own load context — re-built from the modules'
/// CURRENT generations whenever one is swapped live (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c> → "Endpoints").
///
/// <para>ASP.NET Core's endpoint map is built from <see cref="EndpointDataSource"/>s that announce
/// change through a change token; routing rebuilds its matcher when it fires. So a module's endpoints
/// are mapped onto a PRIVATE route builder per generation, exposed here, and on
/// <see cref="ModuleContexts.VersionChanged"/> re-mapped from the new generation and announced — the
/// same authenticated-by-default group and module marker metadata
/// <see cref="MeshModuleEndpointExtensions.MapMeshModuleEndpoints"/> applies. A re-map whose routes
/// collide with another endpoint is not published: the previous map stays, and the collision is
/// logged at Critical, naming both.</para>
/// </summary>
public sealed class ModuleEndpointDataSource : EndpointDataSource
{
    private readonly IServiceProvider services;
    private readonly ModuleContexts contexts;
    private readonly Func<IApplicationBuilder> createApplicationBuilder;
    private readonly ILogger? logger;
    private readonly object gate = new();
    private IReadOnlyList<Endpoint> endpoints = [];
    private CancellationTokenSource changed = new();

    /// <summary>Creates the source over the mesh's module registry, and maps the current generations.</summary>
    public ModuleEndpointDataSource(
        IServiceProvider services, ModuleContexts contexts, Func<IApplicationBuilder> createApplicationBuilder, ILogger? logger)
    {
        this.services = services;
        this.contexts = contexts;
        this.createApplicationBuilder = createApplicationBuilder;
        this.logger = logger;
        endpoints = Map();
        // Lives as long as the registry — the same mesh as the host.
        contexts.VersionChanged.Subscribe(_ => Remap());
    }

    /// <summary>How many endpoints the held modules currently contribute.</summary>
    public int Count => endpoints.Count;

    /// <inheritdoc />
    public override IReadOnlyList<Endpoint> Endpoints => Volatile.Read(ref endpoints);

    /// <inheritdoc />
    public override IChangeToken GetChangeToken()
    {
        lock (gate)
            return new CancellationChangeToken(changed.Token);
    }

    private void Remap()
    {
        IReadOnlyList<Endpoint> mapped;
        try
        {
            mapped = Map();
        }
        catch (Exception exception)
        {
            logger?.LogCritical(exception,
                "[ModuleLiveUpdate] re-mapping module endpoints after a swap FAILED — the previous endpoints keep serving");
            return;
        }
        // The same refusal boot applies — never two registrations on one (verb, pattern) — measured
        // against every OTHER endpoint the host serves; a colliding re-map is not published.
        var others = services.GetService<EndpointDataSource>()?.Endpoints
            .Where(e => !Volatile.Read(ref endpoints).Contains(e)) ?? [];
        if (MeshModuleEndpointExtensions.FindRouteCollisions(others.Concat(mapped)) is { } collision)
        {
            logger?.LogCritical(
                "[ModuleLiveUpdate] the swapped module's endpoints collide with existing routes — NOT published, the previous endpoints keep serving: {Collision}",
                collision);
            return;
        }
        Volatile.Write(ref endpoints, mapped);
        CancellationTokenSource fire;
        lock (gate)
        {
            fire = changed;
            changed = new CancellationTokenSource();
        }
        fire.Cancel();
        fire.Dispose();
    }

    private IReadOnlyList<Endpoint> Map()
    {
        var builder = new PrivateRouteBuilder(services, createApplicationBuilder);
        foreach (var generation in contexts.Generations.OrderBy(g => g.Name, StringComparer.Ordinal))
            foreach (var attribute in generation.Assembly.GetCustomAttributes<MeshEndpointProviderAttribute>())
            {
                var group = builder.MapGroup(string.Empty).RequireAuthorization()
                    .WithMetadata(new MeshModuleEndpointMetadata(generation.Name));
                foreach (var configure in attribute.EndpointConfigurations)
                    configure(group);
            }
        return builder.DataSources.SelectMany(source => source.Endpoints).ToImmutableArray();
    }

    /// <summary>An <see cref="IEndpointRouteBuilder"/> of our own, so a generation's endpoints land
    /// in data sources this type owns and can replace wholesale.</summary>
    private sealed class PrivateRouteBuilder(IServiceProvider services, Func<IApplicationBuilder> createApplicationBuilder)
        : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => services;

        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => createApplicationBuilder();
    }
}
