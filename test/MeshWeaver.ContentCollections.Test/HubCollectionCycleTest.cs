using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// MeshWeaver#5388 (the 2026-10-30 recurrence): a hub-backed content collection whose remote answer
/// is another hub-backed config naming a hop already asked. The host here has no collection called
/// <c>inherited</c> of its own, so its content service falls back to the mesh's, which holds a
/// <c>Hub</c> config naming the host itself — exactly what a portal's <c>/content</c> page registers
/// on a shared content service. Before the fix <see cref="HubStreamProviderFactory"/> resolved that
/// answer by asking the host again, forever: each round was one more asynchronous request and one
/// more nested <c>SelectMany</c> inner observer, until a terminal signal unwound all of them on one
/// stack (a <see cref="StackOverflowException"/> in production, 130,840 levels deep).
/// </summary>
/// <remarks>
/// The two facts are each other's control. <see cref="A_hub_collection_that_resolves_back_to_itself_is_refused_by_name"/>
/// fails without the cycle check (the resolution never answers, so the bounded wait expires);
/// <see cref="A_hub_collection_the_host_serves_itself_still_resolves"/> fails if the check refuses a
/// legitimate one-hop resolution — a check that refused every <c>Hub</c> source would pass the first.
/// </remarks>
public class HubCollectionCycleTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Served = "served";
    private const string Inherited = "inherited";
    // Collection names are case-SENSITIVE: "Served" is a different collection from "served", here a
    // Hub config on the host that redirects to it.
    private const string ServedRedirect = "Served";

    private readonly string _servedPath = Path.Combine(AppContext.BaseDirectory, "Files", "HubCollectionCycle");

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf).AddContentCollections();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        Directory.CreateDirectory(_servedPath);
        return base.ConfigureHost(configuration)
            .AddContentCollections()
            .AddFileSystemContentCollection(Served, _ => _servedPath)
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = ServedRedirect,
                SourceType = HubStreamProviderFactory.SourceType,
                Address = CreateHostAddress(),
                Settings = new Dictionary<string, string> { ["CollectionName"] = Served }
            });
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddContentCollections();

    private IStreamProviderFactory HubFactory(IMessageHub client)
        => client.ServiceProvider.GetRequiredKeyedService<IStreamProviderFactory>(HubStreamProviderFactory.SourceType);

    /// <summary>The resolution's terminal verdict as a value: the provider, or the error it ended with.</summary>
    private static IObservable<(IStreamProvider? Provider, Exception? Error)> Verdict(IObservable<IStreamProvider> resolution)
        => resolution
            .Take(1)
            .Select(p => ((IStreamProvider?)p, (Exception?)null))
            .Catch<(IStreamProvider?, Exception?), Exception>(ex => Observable.Return(((IStreamProvider?)null, (Exception?)ex)));

    /// <summary>A Hub config that leads back to the hop it started from errors with the chain, instead of asking forever.</summary>
    [Fact]
    public async Task A_hub_collection_that_resolves_back_to_itself_is_refused_by_name()
    {
        var host = GetHost();
        var client = GetClient();

        // What a shared (parent) content service holds after a /content page has been opened on the
        // host's collection: a Hub config naming the host. The host inherits it.
        Mesh.ServiceProvider.GetRequiredService<IContentService>().AddConfiguration(new ContentCollectionConfig
        {
            Name = Inherited,
            SourceType = HubStreamProviderFactory.SourceType,
            Address = host.Address
        });

        var (provider, error) = await Verdict(HubFactory(client).Create(new ContentCollectionConfig
            {
                Name = Inherited,
                SourceType = HubStreamProviderFactory.SourceType,
                Address = host.Address
            }))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a cycle has no answer; it must be refused on the hop that closes it, not asked forever",
                TestContext.Current.CancellationToken);

        Output.WriteLine(error?.Message ?? "<no error>");
        provider.Should().BeNull();
        var cycle = error.Should().BeOfType<HubCollectionCycleException>().Subject;
        var hop = HubCollectionCycleException.Describe(host.Address, Inherited);
        cycle.Chain.Should().Equal(hop, hop);
    }

    /// <summary>Control: a Hub config the host answers with its own FileSystem collection resolves to a provider.</summary>
    [Fact]
    public async Task A_hub_collection_the_host_serves_itself_still_resolves()
    {
        var host = GetHost();
        var client = GetClient();

        var (provider, error) = await Verdict(HubFactory(client).Create(new ContentCollectionConfig
            {
                Name = Served,
                SourceType = HubStreamProviderFactory.SourceType,
                Address = host.Address
            }))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a one-hop Hub resolution to a collection the host declares is not a cycle",
                TestContext.Current.CancellationToken);

        error.Should().BeNull();
        provider.Should().NotBeNull();
    }

    /// <summary>
    /// Control: two hops on the SAME node that differ only in the case of the collection name are two
    /// different collections (the content service's lookup is ordinal), so a redirect from one to the
    /// other is not a cycle and resolves.
    /// </summary>
    [Fact]
    public async Task A_redirect_to_a_collection_differing_only_in_case_is_not_a_cycle()
    {
        var host = GetHost();
        var client = GetClient();

        var (provider, error) = await Verdict(HubFactory(client).Create(new ContentCollectionConfig
            {
                Name = ServedRedirect,
                SourceType = HubStreamProviderFactory.SourceType,
                Address = host.Address
            }))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("'Served' on the host is a Hub config naming 'served' on the same host — a different collection",
                TestContext.Current.CancellationToken);

        Output.WriteLine(error?.Message ?? "<no error>");
        error.Should().BeNull();
        provider.Should().NotBeNull();
    }
}
