using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>In a search control, a typed word narrows the list</b> (2026-10-18). A search control
/// (<c>Controls.MeshSearch</c>, the home catalog) appends what the viewer types to its hidden
/// structural query; the query language reads the bare words as <see cref="ParsedQuery.TextSearch"/>.
/// That term is a FILTER — every word a case-insensitive substring of the node's path, name,
/// description or content strings — and an authored <c>sort:</c> orders the filtered set. It is
/// never a ranking that hands back the whole catalog with the match moved to the top, which is
/// what the semantic SQL route did on production until the Postgres provider was brought into line
/// with this evaluator (Doc/Architecture/VectorSearch, "Free text filters").
///
/// <para>This pins the reference behaviour — the in-memory provider every FileSystem / static /
/// in-memory host runs, and the executor the live relevance gate evaluates — in the exact shape a
/// catalog composes: hidden query, then the typed word, then (on a sort tab) <c>sort:</c>.</para>
/// </summary>
public class FreeTextNarrowsACatalogQueryTest
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Hidden = "namespace:Sales/Deals nodeType:Deal";

    private static MeshNode Deal(string id, string name, string contact, int minutes) => new(id, "Sales/Deals")
    {
        Name = name,
        NodeType = "Deal",
        State = MeshNodeState.Active,
        LastModified = Epoch.AddMinutes(minutes),
        Content = JsonSerializer.Deserialize<object>($$"""{"contact":"{{contact}}"}""", Options),
    };

    private static async Task<IMeshQueryCore> CatalogAsync(CancellationToken ct)
    {
        var store = new InMemoryStorageAdapter();
        await store.Write(new MeshNode("Deals", "Sales") { Name = "Deals", NodeType = "Group", State = MeshNodeState.Active }, Options)
            .Await(ct);
        MeshNode[] deals =
        [
            Deal("D-1-Wingtip-Toys", "Wingtip Toys — renewal", "Jane Roe", 3),
            Deal("D-2-Fabrikam", "Fabrikam Logistics — renewal", "Max Muster", 1),
            Deal("D-3-Wingtip-Air", "Wingtip Air — new business", "Max Muster", 2),
            Deal("D-4-Contoso", "Contoso Foods — renewal", "Max Muster", 4),
        ];
        foreach (var deal in deals)
            await store.Write(deal, Options).Await(ct);
        return new MeshQuery([new StorageAdapterMeshQueryProvider(persistence: store)], hub: null!);
    }

    private static async Task<IReadOnlyList<string>> Ids(IMeshQueryCore query, string typed, CancellationToken ct)
    {
        var change = await query.Query<MeshNode>(new MeshQueryRequest { Query = $"{Hidden} {typed}".Trim() }, Options)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        return change.Items.Select(n => n.Id).ToList();
    }

    [Fact]
    public async Task TheHiddenQueryAlone_ListsTheWholeCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Ids(await CatalogAsync(ct), "", ct)).Should().HaveCount(4);
    }

    [Fact]
    public async Task ATypedWord_NarrowsToTheNodesThatContainIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await CatalogAsync(ct);

        (await Ids(query, "wingtip", ct)).Order().Should().Equal(["D-1-Wingtip-Toys", "D-3-Wingtip-Air"]);
        (await Ids(query, "WINGTIP toys", ct)).Should().Equal(["D-1-Wingtip-Toys"], "every word must match");
        (await Ids(query, "roe", ct)).Should().Equal(["D-1-Wingtip-Toys"], "a content string value is text too");
        (await Ids(query, "zz-none-zz", ct)).Should().BeEmpty("the empty state, never the whole catalog");
    }

    [Fact]
    public async Task AnAuthoredSort_OrdersTheNarrowedSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await CatalogAsync(ct);

        (await Ids(query, "renewal sort:name", ct)).Should().Equal(
            ["D-4-Contoso", "D-2-Fabrikam", "D-1-Wingtip-Toys"],
            "sort:name orders the three renewals — it neither widens the set nor loses the filter");
        (await Ids(query, "wingtip sort:lastModified-desc", ct)).Should().Equal(
            ["D-1-Wingtip-Toys", "D-3-Wingtip-Air"]);
    }
}
