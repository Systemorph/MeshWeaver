using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 THE MEASUREMENT for "the type registry is recursive" (<c>Doc/Architecture/TypeRegistryLayers</c>):
/// on a realistic population — Markdown node hubs activated through <c>GetMeshNodeStream</c>, with their
/// hosted <c>sync/</c> stream hubs — every entry a hub's registry holds itself must be an OVERRIDE of its
/// parent chain: a name the parent does not resolve, or resolves to a different definition. An entry
/// whose name the parent resolves to the very same definition is a copy, and "only overrides are
/// re-registered" forbids it.
///
/// <para>Measured with this test on the registry before the overlay: 84 hubs (12 node hubs, 69
/// <c>sync/</c> hubs), 5,751 own entries — 344 per node hub, 19 per <c>sync/</c> hub — of which 1,152
/// were the parent's very definition and 4,392 a re-built copy of it. After: 0 own entries on every
/// hub. Run with <c>-showLiveOutput</c> for the census.</para>
/// </summary>
public class HubTypeRegistryHoldsOnlyItsOverridesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const int NodeCount = 12;

    private sealed record HubCensus(IMessageHub Hub, IMessageHub Parent, int Own, int Copies, int DistinctOwnDefinitions,
        ImmutableArray<string> CopyNames, ImmutableArray<string> EquivalentNames);

    [Fact(Timeout = 180_000)]
    public async Task EveryHubsOwnLayer_HoldsOnlyItsOverrides()
    {
        var service = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var paths = new List<string>();
        for (var i = 0; i < NodeCount; i++)
        {
            var created = await service
                .CreateNode(new MeshNode($"RegistryCensus{i}", TestPartition)
                {
                    Name = $"census {i}",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = $"# census {i}" },
                })
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            paths.Add(created.Path);
        }

        // Activate every node hub through the live single-node read, and HOLD the subscriptions — the
        // sync/ stream hubs live exactly as long as someone reads.
        var subscriptions = paths
            .Select(p => Mesh.GetWorkspace().GetMeshNodeStream(p).Where(n => n is not null).Replay(1))
            .Select(stream => (Stream: stream, Connection: stream.Connect()))
            .ToArray();
        try
        {
            foreach (var subscription in subscriptions)
                await subscription.Stream.FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

            var census = Walk(Mesh).ToArray();
            var nodeHubs = census.Where(c => paths.Contains(c.Hub.Address.ToString())).ToArray();
            var syncHubs = census.Where(c => c.Hub.Address.ToString().Contains("sync/")).ToArray();

            Output.WriteLine($"hubs walked: {census.Length} — node hubs {nodeHubs.Length}, sync/ hubs {syncHubs.Length}");
            Output.WriteLine($"own entries per hub: total {census.Sum(c => c.Own)}, "
                + $"node hubs avg {Average(nodeHubs, c => c.Own):F1}, sync/ hubs avg {Average(syncHubs, c => c.Own):F1}");
            Output.WriteLine($"own definitions per hub (distinct objects): node hubs avg {Average(nodeHubs, c => c.DistinctOwnDefinitions):F1}, "
                + $"sync/ hubs avg {Average(syncHubs, c => c.DistinctOwnDefinitions):F1}");
            Output.WriteLine($"copies of a parent definition: {census.Sum(c => c.Copies)}; same type and name as the parent but another object: {census.Sum(c => c.EquivalentNames.Length)}");
            foreach (var c in census.Where(c => c.Own > 0).Take(40))
                Output.WriteLine($"  {c.Hub.Address} (parent {c.Parent.Address}): own {c.Own}, copies {c.Copies}"
                    + (c.CopyNames.IsEmpty ? "" : $" [{string.Join(", ", c.CopyNames.Take(8))}]")
                    + $", re-built {c.EquivalentNames.Length}"
                    + (c.EquivalentNames.IsEmpty ? "" : $" [{string.Join(", ", c.EquivalentNames.Take(8))}]")
                    + $" | own: {string.Join(", ", c.Hub.TypeRegistry.OwnTypes.Select(t => t.Key).Take(12))}");

            nodeHubs.Should().HaveCount(NodeCount, "the precondition: every node hub is live");
            syncHubs.Should().NotBeEmpty("the precondition: the reads hosted their sync/ stream hubs");
            census.Should().AllSatisfy(c => c.Copies.Should().Be(0,
                $"{c.Hub.Address} must hold only overrides of its parent chain, but re-holds [{string.Join(", ", c.CopyNames)}]"));
            census.Should().AllSatisfy(c => c.EquivalentNames.Should().BeEmpty(
                $"{c.Hub.Address} re-built definitions its parent already holds under the same name"));
            syncHubs.Should().AllSatisfy(c => c.Own.Should().Be(0,
                $"a sync/ stream hub overrides nothing, so its layer is empty ({c.Hub.Address})"));
            nodeHubs.Should().AllSatisfy(c => c.Own.Should().Be(0,
                $"a Markdown node hub overrides nothing of the mesh, so its layer is empty ({c.Hub.Address})"));
        }
        finally
        {
            foreach (var subscription in subscriptions)
                subscription.Connection.Dispose();
        }
    }

    private static double Average(HubCensus[] hubs, Func<HubCensus, int> value) =>
        hubs.Length == 0 ? 0 : hubs.Average(value);

    private static IEnumerable<HubCensus> Walk(IMessageHub parent)
    {
        foreach (var child in parent.ServiceProvider.GetRequiredService<HostedHubsCollection>().Hubs)
        {
            if (!ReferenceEquals(child.TypeRegistry, parent.TypeRegistry))
                yield return Census(child, parent);
            foreach (var descendant in Walk(child))
                yield return descendant;
        }
    }

    private static HubCensus Census(IMessageHub hub, IMessageHub parent)
    {
        var own = hub.TypeRegistry.OwnTypes.ToArray();
        var parentRegistry = parent.TypeRegistry;
        var copies = own
            .Where(entry => ReferenceEquals(parentRegistry.GetTypeDefinition(entry.Key), entry.Value))
            .Select(entry => entry.Key)
            .ToImmutableArray();
        var distinct = own.Select(e => e.Value).Distinct(ReferenceEqualityComparer.Instance).Count();
        // Same type under the same name as the parent's, but a different object: either a re-built copy
        // (the duplication this test exists for) or a genuine override of the key (a type source's
        // WithKey). Printed, so the census names them; only the strict copies are asserted.
        var equivalent = own
            .Where(entry => parentRegistry.GetTypeDefinition(entry.Key) is { } inherited
                            && !ReferenceEquals(inherited, entry.Value)
                            && inherited.Type == entry.Value.Type
                            && inherited.CollectionName == entry.Value.CollectionName)
            .Select(entry => entry.Key)
            .ToImmutableArray();
        return new HubCensus(hub, parent, own.Length, copies.Length, distinct, copies, equivalent);
    }
}
