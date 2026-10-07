using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// <see cref="MeshModuleEndpointExtensions.MapMeshModuleEndpoints"/> applies.</para>
///
/// <para>The refusal is scoped PER MODULE (#6128 review). Each module's endpoints are published
/// together with the generation that mapped them, and only a module whose CURRENT generation differs
/// from its published one is re-mapped. A module whose new generation throws while mapping, or whose
/// routes collide with another endpoint the host serves (another module's included), keeps serving its
/// previously published endpoints. It is logged at Critical, naming the module and the collision. Every
/// other module's change is still published, so one module's bad generation never freezes endpoint
/// updates for the rest. A refused module is retried on every later swap, because its current
/// generation still differs from its published one. The pending maps are first decided together, so
/// two modules that exchange routes across swap waves are published at once rather than each refused
/// against the other's stale map. A refused module keeps its previous generation loaded, because its
/// served endpoints hold that generation's types: one retained generation per refused module, until a
/// later map is accepted.</para>
/// </summary>
public sealed class ModuleEndpointDataSource : EndpointDataSource, IDisposable
{
    private readonly IServiceProvider services;
    private readonly ModuleContexts contexts;
    private readonly Func<IApplicationBuilder> createApplicationBuilder;
    private readonly ILogger? logger;
    private readonly object gate = new();
    private readonly object remapGate = new();
    private IReadOnlyList<Endpoint> endpoints = [];
    private ImmutableDictionary<string, Published> published = ImmutableDictionary.Create<string, Published>(StringComparer.Ordinal);
    private CancellationTokenSource changed = new();
    private IDisposable? versionChanged;

    /// <summary>A module's endpoints as they are being served, with the generation that mapped them.</summary>
    private sealed record Published(ModuleGeneration Generation, IReadOnlyList<Endpoint> Endpoints);

    /// <summary>Creates the source over the mesh's module registry, and maps the current generations.</summary>
    public ModuleEndpointDataSource(
        IServiceProvider services, ModuleContexts contexts, Func<IApplicationBuilder> createApplicationBuilder, ILogger? logger)
        : this(services, contexts, createApplicationBuilder, logger, afterSnapshot: null)
    {
    }

    /// <summary>The constructor with a test seam: <paramref name="afterSnapshot"/> runs between the boot
    /// snapshot and the subscription, exactly where a concurrent live-swap commit could land unseen.</summary>
    internal ModuleEndpointDataSource(
        IServiceProvider services, ModuleContexts contexts, Func<IApplicationBuilder> createApplicationBuilder, ILogger? logger,
        Action? afterSnapshot)
    {
        this.services = services;
        this.contexts = contexts;
        this.createApplicationBuilder = createApplicationBuilder;
        this.logger = logger;
        // The version is read BEFORE the snapshot (#6128 review): VersionChanged is a plain subject that
        // replays nothing, so a commit landing between the snapshot and the subscription below would
        // bump it with no subscriber — and this source would serve the pre-commit endpoints, silently,
        // until some later swap. The check after subscribing closes that window.
        var seen = contexts.Version;
        // Boot maps every held module; a boot collision is refused by the host's startup check, which
        // reads the composite endpoint table this source is part of.
        published = contexts.Generations.ToImmutableDictionary(
            g => g.Name, g => new Published(g, MapModule(g)), StringComparer.Ordinal);
        endpoints = Flatten(published);
        afterSnapshot?.Invoke();
        // Bound to the HOST, not the registry (#6128 review): the registry may outlive this host — a
        // host rebuilt over a live registry, or several route builders on one mesh — and a source
        // left subscribed would keep re-mapping and firing tokens no matcher reads, rooted by the
        // registry's subject. So the subscription ends when the host stops, or when this source is
        // disposed, whichever comes first.
        versionChanged = contexts.VersionChanged.Subscribe(_ => Remap());
        // A commit that landed after `seen` was read but before the subscription is caught here. Remap
        // is idempotent: when the snapshot already holds the current generations it changes nothing.
        if (contexts.Version != seen)
            Remap();
        services.GetService<IHostApplicationLifetime>()?.ApplicationStopping.Register(Dispose);
    }

    /// <summary>Ends the subscription to module swaps; the endpoints already published keep being
    /// served as they are. Called when the host stops.</summary>
    public void Dispose() => Interlocked.Exchange(ref versionChanged, null)?.Dispose();

    /// <summary>How many endpoints the held modules currently contribute.</summary>
    public int Count => Volatile.Read(ref endpoints).Count;

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
        lock (remapGate)
        {
            var previous = published;
            var current = contexts.Generations.OrderBy(g => g.Name, StringComparer.Ordinal).ToArray();
            // Start from what is served: an unchanged module keeps its endpoints, a changed one keeps its
            // previous endpoints until its new map is accepted, and a module no longer held drops out.
            var choice = current
                .Where(g => previous.ContainsKey(g.Name))
                .ToImmutableDictionary(g => g.Name, g => previous[g.Name], StringComparer.Ordinal);
            var pending = current
                .Where(g => !(previous.TryGetValue(g.Name, out var served) && ReferenceEquals(served.Generation, g)))
                .ToArray();
            var ours = previous.Values.SelectMany(p => p.Endpoints).ToImmutableHashSet(ReferenceEqualityComparer.Instance);
            var foreign = services.GetService<EndpointDataSource>()?.Endpoints
                .Where(e => !ours.Contains(e)).ToArray() ?? [];

            var candidates = ImmutableDictionary.Create<string, Published>(StringComparer.Ordinal);
            foreach (var generation in pending)
            {
                try
                {
                    candidates = candidates.SetItem(generation.Name, new Published(generation, MapModule(generation)));
                }
                catch (Exception exception)
                {
                    logger?.LogCritical(exception,
                        "[ModuleLiveUpdate] re-mapping module {Module}'s endpoints after a swap FAILED — its previous endpoints keep serving; other modules' changes are still published",
                        generation.Name);
                }
            }

            // The pending maps are decided TOGETHER first (#6128 review): two modules that EXCHANGE
            // routes across swap waves collide only with each other's stale, still-served map, so
            // measuring each against the others' served endpoints would refuse both on every later swap
            // — for good. If every mapped candidate fits with the foreign routes and the unchanged
            // modules, all are published at once. Only when that combined set collides is each candidate
            // decided ALONE, against the others' served maps, so one bad generation is still refused
            // without holding back the rest.
            var settled = choice.RemoveRange(candidates.Keys);
            if (MeshModuleEndpointExtensions.FindRouteCollisions(
                    foreign.Concat(settled.Values.SelectMany(p => p.Endpoints)).Concat(candidates.Values.SelectMany(p => p.Endpoints))) is null)
            {
                choice = choice.SetItems(candidates);
            }
            else
            {
                foreach (var (name, candidate) in candidates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    // The same refusal boot applies — never two registrations on one (verb, pattern) —
                    // measured against every endpoint the host would serve with this module's new map.
                    var others = choice.Where(kv => kv.Key != name).SelectMany(kv => kv.Value.Endpoints);
                    if (MeshModuleEndpointExtensions.FindRouteCollisions(foreign.Concat(others).Concat(candidate.Endpoints)) is { } collision)
                    {
                        logger?.LogCritical(
                            "[ModuleLiveUpdate] module {Module}'s swapped endpoints collide with existing routes — NOT published, its previous endpoints keep serving; other modules' changes are still published: {Collision}",
                            name, collision);
                        continue;
                    }
                    choice = choice.SetItem(name, candidate);
                }
            }

            var unchanged = choice.Count == previous.Count
                && choice.All(kv => previous.TryGetValue(kv.Key, out var served) && ReferenceEquals(served, kv.Value));
            if (unchanged)
                return;
            published = choice;
            Volatile.Write(ref endpoints, Flatten(choice));
        }
        CancellationTokenSource fire;
        lock (gate)
        {
            fire = changed;
            changed = new CancellationTokenSource();
        }
        // Cancelled, never disposed: its token was handed out through GetChangeToken, and a consumer
        // that registers on it after a Dispose would get ObjectDisposedException. Once `changed` is
        // swapped nothing references the old source, so it is simply collected — the shape the
        // framework's own endpoint data sources use.
        fire.Cancel();
    }

    private static IReadOnlyList<Endpoint> Flatten(ImmutableDictionary<string, Published> modules) =>
        modules.OrderBy(kv => kv.Key, StringComparer.Ordinal).SelectMany(kv => kv.Value.Endpoints).ToImmutableArray();

    private IReadOnlyList<Endpoint> MapModule(ModuleGeneration generation)
    {
        var builder = new PrivateRouteBuilder(services, createApplicationBuilder);
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
