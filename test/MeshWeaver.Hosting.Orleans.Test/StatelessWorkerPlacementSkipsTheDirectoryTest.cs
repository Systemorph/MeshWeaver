#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 Issue #5037 — placing <c>routing/default</c> must not depend on the cluster's grain directory.
///
/// <para><b>The incident.</b> On 2026-09-20 22:25:14Z one memex pod rejected 331 distinct
/// <c>IRoutingGrain.RouteMessage</c> deliveries inside ~50 ms with
/// <c>Grain placement operation timed out for grain routing/default</c> (Polly, 30 s), raised in
/// <c>PlacementService.PlacementWorker.ExecutePlacementAsync</c>. <c>RoutingGrain</c> is a
/// <c>[StatelessWorker(1)]</c> that <c>StatelessWorkerDirector</c> places on the calling silo, and the
/// ticket's reading was that such a placement makes "no directory lookup and no remote hop".</para>
///
/// <para><b>What Orleans 10.3.1 actually does.</b> <c>ExecutePlacementAsync</c> starts EVERY placement
/// with <c>GrainLocator.Lookup(grainId)</c>, stateless worker or not. On the portal's default directory
/// that is <c>LocalGrainDirectory.LookupAsync</c>, which forwards to the silo owning the grain id's hash
/// on the directory ring — for an answer that is always "not registered", since a stateless worker is
/// never registered (<c>StatelessWorkerPlacement.IsUsingGrainDirectory == false</c>). When that owner
/// could not answer, every message queued on the one placement work item was rejected together.</para>
///
/// <para><b>How this reproduces it.</b> <see cref="UnreachableOwnerDirectoryResolver"/> stands in for
/// "the directory partition that owns these ids cannot answer": for any grain id carrying
/// <see cref="UnreachableOwnerDirectory.Marker"/> in its key, the directory refuses with the transient
/// <see cref="SiloUnavailableException"/> a departed owner produces; every other id is served by the
/// cluster's real directory. It is registered AFTER the production resolver, so it answers only for the
/// types production leaves to the directory — exactly the position the cluster directory holds.</para>
///
/// <para><b>Controls.</b> <see cref="A_directory_backed_grain_under_an_unreachable_owner_cannot_be_placed"/>
/// proves the refusal really reaches placement (so the routing fact cannot pass because the stand-in is
/// inert). <b>Negative control, run when this was written:</b> with the
/// <c>StatelessWorkerGrainDirectoryResolver</c> registration removed from
/// <c>ConfigureMeshWeaverServer</c>, the routing fact failed with the refusal below — the #5037 shape.</para>
/// </summary>
public class StatelessWorkerPlacementSkipsTheDirectoryTest(ITestOutputHelper output)
    : OrleansMeshTestBase(output)
{
    protected override Type SiloConfiguratorType => typeof(UnreachableDirectoryOwnerSiloConfigurator);

    private static readonly TimeSpan Budget = TestTimeouts.Convergence;

    // A SILO's grain factory: the incident's caller was the hosted client INSIDE the silo process, which
    // addresses through that silo's own placement service.
    private IGrainFactory SiloGrains => SiloServices().GetRequiredService<IGrainFactory>();

    private Address SiloMeshAddress => SiloServices().GetRequiredService<IMessageHub>().Address;

    private static IMessageDelivery Ping(Address sender, Address target) =>
        new MessageDelivery<PingRequest>(sender, target, new PingRequest(),
            System.Text.Json.JsonSerializerOptions.Default);

    /// <summary>
    /// The resolver keys on the placement strategy NAME Orleans writes into the grain manifest, which
    /// Orleans keeps internal. Pin the literal against a live silo's manifest for the real grain type, so
    /// an Orleans rename fails here instead of silently restoring the directory lookup.
    /// </summary>
    [Fact]
    public void RoutingGrain_manifest_declares_the_stateless_worker_placement_the_resolver_keys_on()
    {
        var grainType = SiloGrains.GetGrain<IRoutingGrain>("default").GetGrainId().Type;

        SiloServices().GetRequiredService<GrainPropertiesResolver>()
            .TryGetGrainProperties(grainType, out var properties)
            .Should().BeTrue("the silo hosts RoutingGrain, so its manifest must describe the type");

        StatelessWorkerGrainDirectoryResolver.IsStatelessWorker(properties).Should().BeTrue(
            $"the manifest's '{WellKnownGrainTypeProperties.PlacementStrategy}' for {grainType} must read "
            + $"'{StatelessWorkerGrainDirectoryResolver.StatelessWorkerPlacementName}' — otherwise the resolver "
            + "never claims routing/default and every placement goes back to the directory lookup of #5037");
    }

    /// <summary>
    /// Positive control: a single-activation grain ([PreferLocalPlacement], directory-registered) whose
    /// id the stand-in directory cannot answer for is NOT placeable — the refusal reaches placement.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task A_directory_backed_grain_under_an_unreachable_owner_cannot_be_placed()
    {
        var address = new Address("portal", $"{UnreachableOwnerDirectory.Marker}{Guid.NewGuid():N}");

        var failure = await Record.ExceptionAsync(() =>
            SiloGrains.GetGrain<IPodHubGrain>(address.ToString())
                .Deliver(Ping(SiloMeshAddress, address))
                .WaitAsync(Budget, TestContext.Current.CancellationToken));

        failure.Should().NotBeNull(
            "placing a directory-backed grain must consult the directory, and the directory's owner for "
            + "this id is unreachable — if this call succeeded the stand-in is inert and the routing fact "
            + "below would pass without testing anything");
        failure!.ToString().Should().Contain(UnreachableOwnerDirectory.Refusal,
            $"the call must have failed on the directory refusal, not on something else; it failed with {failure}");
    }

    /// <summary>
    /// The subject: a routing grain whose id the directory cannot answer for is placed and answers anyway,
    /// because a stateless worker's placement no longer asks the directory at all.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task A_routing_grain_is_placed_although_the_directory_owner_cannot_answer()
    {
        var ghost = new Address("client", $"placement-ghost-{Guid.NewGuid():N}");

        var ack = await SiloGrains
            .GetGrain<IRoutingGrain>($"{UnreachableOwnerDirectory.Marker}{Guid.NewGuid():N}")
            .RouteMessage(Ping(SiloMeshAddress, ghost))
            .WaitAsync(Budget, TestContext.Current.CancellationToken);

        ack.Should().NotBeNull(
            "routing grain placement must not wait on the grain directory: its answer for a stateless worker "
            + "is always 'not registered', and when the owning partition cannot give it (#5037) every "
            + "delivery queued on that placement is rejected");
    }
}

/// <summary>
/// The production silo configuration plus <see cref="UnreachableOwnerDirectoryResolver"/>, registered
/// after it — see <see cref="StatelessWorkerPlacementSkipsTheDirectoryTest"/>.
/// </summary>
public class UnreachableDirectoryOwnerSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
        siloBuilder.ConfigureServices(services =>
            services.AddSingleton<IGrainDirectoryResolver, UnreachableOwnerDirectoryResolver>());
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddPartitionedInMemoryPersistence()
            .ConfigurePortalMesh();
    }
}

/// <summary>
/// Hands every grain type it is asked about to <see cref="UnreachableOwnerDirectory"/>. Orleans asks the
/// resolvers in registration order, so the production resolver (registered first, by
/// <c>ConfigureMeshWeaverServer</c>) keeps the types it claims and this one sees only the rest — the
/// types the cluster directory would serve.
/// </summary>
internal sealed class UnreachableOwnerDirectoryResolver(IServiceProvider services) : IGrainDirectoryResolver
{
    private readonly UnreachableOwnerDirectory directory = new(services);

    public bool TryResolveGrainDirectory(
        GrainType grainType,
        GrainProperties? properties,
        [NotNullWhen(true)] out IGrainDirectory? grainDirectory)
    {
        grainDirectory = directory;
        return true;
    }
}

/// <summary>
/// The cluster's own default directory, except that it cannot answer for ids carrying
/// <see cref="Marker"/>: for those it refuses the way a departed partition owner does. Every other call —
/// including every overload Orleans reaches through default interface methods — goes to the real one.
/// </summary>
internal sealed class UnreachableOwnerDirectory(IServiceProvider services) : IGrainDirectory
{
    public const string Marker = "unanswerable-";

    public const string Refusal = "#5037 stand-in: the directory partition owning this grain id is unreachable";

    // The TestCluster registers its directory as the keyed "default" one; resolved lazily, because this
    // directory is itself built while Orleans assembles the resolver chain.
    private IGrainDirectory Inner => services.GetRequiredKeyedService<IGrainDirectory>("default");

    private static bool IsMarked(GrainId grainId) =>
        grainId.Key.ToString()!.Contains(Marker, StringComparison.Ordinal);

    private static Task<GrainAddress?> Refuse() =>
        Task.FromException<GrainAddress?>(new SiloUnavailableException(Refusal));

    public Task<GrainAddress?> Lookup(GrainId grainId) =>
        IsMarked(grainId) ? Refuse() : Inner.Lookup(grainId);

    public Task<GrainAddress?> Lookup(GrainId grainId, CancellationToken cancellationToken) =>
        IsMarked(grainId) ? Refuse() : Inner.Lookup(grainId, cancellationToken);

    public Task<GrainAddress?> Register(GrainAddress address) =>
        IsMarked(address.GrainId) ? Refuse() : Inner.Register(address);

    public Task<GrainAddress?> Register(GrainAddress address, CancellationToken cancellationToken) =>
        IsMarked(address.GrainId) ? Refuse() : Inner.Register(address, cancellationToken);

    public Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress) =>
        IsMarked(address.GrainId) ? Refuse() : Inner.Register(address, previousAddress);

    public Task<GrainAddress?> Register(
        GrainAddress address, GrainAddress? previousAddress, CancellationToken cancellationToken) =>
        IsMarked(address.GrainId) ? Refuse() : Inner.Register(address, previousAddress, cancellationToken);

    public Task Unregister(GrainAddress address) => Inner.Unregister(address);

    public Task Unregister(GrainAddress address, CancellationToken cancellationToken) =>
        Inner.Unregister(address, cancellationToken);

    public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Inner.UnregisterSilos(siloAddresses);

    public Task UnregisterSilos(List<SiloAddress> siloAddresses, CancellationToken cancellationToken) =>
        Inner.UnregisterSilos(siloAddresses, cancellationToken);
}
