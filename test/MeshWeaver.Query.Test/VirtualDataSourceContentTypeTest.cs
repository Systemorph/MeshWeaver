using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// Two compilations of "the same" content class — the @@-include shape from agentic-pensions#12: the
// fact NodeType's own assembly vs the dashboard's included copy. Same short name, same JSON shape,
// DIFFERENT CLR identity (different namespaces model different dynamic assemblies).
namespace MeshWeaver.Query.Test.OwnerCopy
{
    public record SharedEntry
    {
        public string Position { get; init; } = string.Empty;
        public double Amount { get; init; }
    }
}

namespace MeshWeaver.Query.Test.ConsumerCopy
{
    public record SharedEntry
    {
        public string Position { get; init; } = string.Empty;
        public double Amount { get; init; }
    }
}

namespace MeshWeaver.Query.Test
{
    /// <summary>
    /// End-to-end regression for the atioz "BalanceSheet dashboards render empty" outage
    /// (agentic-pensions#12). The production corridor: fact nodes flow through the shared query
    /// pipeline with their Content TYPED as the fact NodeType's own compilation of the content class;
    /// the consuming NodeType's virtual-data-source loader projects them via
    /// <c>ContentAs&lt;T&gt;</c> against its OWN @@-included compilation — a different CLR type.
    /// Pre-fix, <c>ContentAs</c> silently returned null for every node and the loader's
    /// <c>Content is not null</c> filter emptied the whole collection; the store still initialized
    /// "successfully", so the dashboard rendered its empty state with zero errors anywhere.
    ///
    /// <para>Setup mirrors <see cref="SyncedQueryDataSourceTest"/>: a static Subscriber node whose
    /// <c>HubConfiguration</c> registers the virtual data source exactly the way the production
    /// NodeType (BalanceSheetConfig.ConfigureBalanceSheet) does, with the loader shape of
    /// BalanceSheetData.LoadEntries.</para>
    /// </summary>
    public class VirtualDataSourceContentTypeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
    {
        private const string SubscriberPath = $"{TestPartition}/EntrySubscriber";
        private const string EntriesNamespace = $"{TestPartition}/Entries";
        private const string EntryCollection = nameof(EntryProjection);

        /// <summary>Workspace projection of a fact node — the EntryNode shape from the outage.</summary>
        public record EntryProjection
        {
            [Key]
            public string Path { get; init; } = string.Empty;
            public ConsumerCopy.SharedEntry Content { get; init; } = null!;
        }

        protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
            => base.ConfigureMesh(builder)
                .AddMeshNodes(new MeshNode("EntrySubscriber", TestPartition)
                {
                    Name = "Entry Subscriber",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                    HubConfiguration = config => config
                        .AddData(data => data.WithVirtualDataSource("$entries",
                            vs => vs.WithVirtualType<EntryProjection>(LoadEntries)))
                });

        /// <summary>The exact loader shape of BalanceSheetData.LoadEntries (agentic-pensions).</summary>
        private static IObservable<IEnumerable<EntryProjection>> LoadEntries(IWorkspace workspace)
            => workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{EntriesNamespace} nodeType:Markdown state:Active"))
                .Select(change => change.Items
                    .Select(n => new EntryProjection
                    {
                        Path = n.Path,
                        Content = n.ContentAs<ConsumerCopy.SharedEntry>(workspace.Hub.JsonSerializerOptions)!,
                    })
                    .Where(x => x.Content is not null && x.Content.Position.Length > 0)
                    .ToImmutableList()
                    .AsEnumerable());

        /// <summary>
        /// Fact nodes whose Content is typed with the OWNER's compilation must still populate the
        /// consumer's virtual collection. Pre-fix this stayed empty forever — the read below is the
        /// same grain-workspace read (<c>GetDataRequest</c> + <c>CollectionReference</c>) that showed
        /// EntryNode at 0 of 200 on atioz while every query and probe reported healthy data.
        /// </summary>
        [Fact]
        public async Task TypedForeignContent_StillPopulatesVirtualCollection()
        {
            foreach (var i in Enumerable.Range(0, 3))
                await NodeFactory.CreateNode(new MeshNode($"entry-{i}", EntriesNamespace)
                {
                    Name = $"Entry {i}",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                    Content = new OwnerCopy.SharedEntry { Position = $"Position/P{i}", Amount = i * 10.0 },
                }).Should().Emit();

            var client = GetClient();

            // The subscriber activates on first request; its virtual data source may see the creates in
            // its Initial or as later change-feed updates — re-read until the collection converges. The
            // response Data crosses back serialized, so it lands as a JsonElement keyed by entry path.
            var collection = await Observable.Interval(TimeSpan.FromMilliseconds(200))
                .StartWith(0L)
                .SelectMany(_ => client
                    .Observe<GetDataResponse>(
                        new GetDataRequest(new CollectionReference(EntryCollection)),
                        o => o.WithTarget(new Address(SubscriberPath)))
                    .Take(1))
                .Select(d => d.Message.Data is JsonElement { ValueKind: JsonValueKind.Object } je
                    ? je
                    : (JsonElement?)null)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(je => je != null && Entries(je.Value).Count == 3);

            var entries = Entries(collection!.Value);
            entries.Should().HaveCount(3,
                "every fact node's typed-foreign Content must be recovered into the consumer's copy");
            entries.Select(e => e.GetProperty("content").GetProperty("position").GetString())
                .OrderBy(p => p).Should()
                .Equal("Position/P0", "Position/P1", "Position/P2");
            entries.Sum(e => e.GetProperty("content").TryGetProperty("amount", out var amount)
                    ? amount.GetDouble()
                    : 0.0)
                .Should().Be(30.0);
        }

        /// <summary>The instances of a serialized InstanceCollection payload (path-keyed; $type stripped).</summary>
        private static List<JsonElement> Entries(JsonElement collection)
            => collection.EnumerateObject()
                .Where(p => p.Name != "$type")
                .Select(p => p.Value)
                .ToList();
    }
}
