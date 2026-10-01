using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The catalog's ALL page against real install records in the isolated test mesh: the orphaned
/// record's Remove and the card's update-policy choices are ROW-SCOPED actions (Doc/GUI/DataBinding →
/// "Row-scoped actions") — each declared ONCE in its row template, and the click carries the row it
/// was clicked in. Clicking in row k acts on row k, also after the rows changed underneath; a click
/// with no row acts on nothing. Package files are local fixture content only.
/// </summary>
public class CatalogOrphanActionIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CatalogNode = "OrphanIdentityCatalog";
    private const string Area = CatalogLayoutAreas.CatalogArea;

    private static string RemoveArea => $"{Area}/orphans/{ItemTemplateControl.ViewArea}/remove";

    private static string AutoArea => $"{Area}/cards/{ItemTemplateControl.ViewArea}/updatePolicy/choices/auto";

    private static readonly ImmutableList<PackageManifest> Listed = [Package("ListedA", "Listed A"), Package("ListedB", "Listed B")];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog().AddMeshNodes(
            new MeshNode("OrphanIdentityCatalogType")
            {
                HubConfiguration = config => config.AddDefaultLayoutAreas().AddLayout(layout =>
                    layout.WithView(Area, (host, _) =>
                        CatalogLayoutAreas.RenderFromSource(host, new FixtureSource(), "HEAD", null, "fixture")))
            },
            new MeshNode(CatalogNode) { NodeType = "OrphanIdentityCatalogType" });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static PackageManifest Package(string id, string name) => new()
    {
        Id = id, Name = name, Version = "1.0.0", Kind = PackageKind.Content,
        Category = "Fixtures", TargetPartition = id, SourceFolder = id,
    };

    private Task<InstallResult> Install(PackageManifest manifest)
        => PackageInstaller.Install(Mesh, manifest,
                [new PackageFile($"{manifest.Id}/Doc.md", $"# {manifest.Name}")], "HEAD")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// Three orphaned records; the person clicks Remove on B. Before the click a neighbour is
    /// inserted above (or removed), so B's slot changes — and the click still removes B, and only B.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRemoveClickedInRowK_RemovesThatOrphan_AlsoAfterANeighbourChanged(bool insert)
    {
        var first = Package("OrphanA", "Package A");
        var intended = Package("OrphanB", "Package B");
        var last = Package("OrphanC", "Package C");
        await Install(intended);
        await Install(last);
        if (!insert)
            await Install(first);

        var stream = OpenAll();
        var before = await Rows(stream, CatalogLayoutAreas.OrphansDataId, insert ? 2 : 3);
        var clicked = RowOf(before, CatalogLayoutAreas.OrphansDataId, intended.Id);

        if (insert)
            await Install(first);
        else
            Assert.True(await PackageInstaller.RemoveInstalledRecord(Mesh, first.Id)
                .Should().Within(TestTimeouts.Convergence).Emit());
        var after = await Rows(stream, CatalogLayoutAreas.OrphansDataId, insert ? 3 : 2);
        Assert.NotEqual(clicked.Index, RowOf(after, CatalogLayoutAreas.OrphansDataId, intended.Id).Index);

        var records = Records();
        Submit(stream, RemoveArea, clicked);
        var remaining = await records.Where(items => items.All(item => item.Id != intended.Id))
            .Should().Within(TestTimeouts.Convergence).Emit();
        Assert.Contains(remaining, item => item.Id == last.Id);
        if (insert)
            Assert.Contains(remaining, item => item.Id == first.Id);
        // Removing the record must leave the package's installed content in place.
        Assert.NotNull(await Mesh.GetMeshNode(intended.Id + "/Doc")
            .Should().Within(TestTimeouts.Convergence).Emit());
    }

    /// <summary>NEGATIVE CONTROL: the Remove button clicked with no row removes nothing.</summary>
    [Fact]
    public async Task ARemoveClickWithNoRow_RemovesNothing()
    {
        var orphan = Package("OrphanSolo", "Solo");
        await Install(orphan);
        var stream = OpenAll();
        await Rows(stream, CatalogLayoutAreas.OrphansDataId, 1);

        var records = Records();
        Submit(stream, RemoveArea, row: null);
        await records.Where(items => items.All(item => item.Id != orphan.Id))
            .Should().NotEmit(1.Seconds(), "with no row the action cannot know which record, and must not guess",
                TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The update-policy choices are row-scoped too: "Auto" clicked on card k re-pins package k, for
    /// every k, and leaves the other card's policy as it was.
    /// </summary>
    [Fact]
    public async Task TheAutoChoiceInRowK_SetsPackageKsPolicy()
    {
        foreach (var package in Listed)
            await Install(package);
        var stream = OpenAll();
        var cards = await Rows(stream, CatalogLayoutAreas.CardsDataId, Listed.Count);
        var records = Records();
        await records.Where(items => Listed.All(p => items.Any(i => i.Id == p.Id
                && i.EffectiveUpdatePolicy != PackageUpdatePolicy.Auto)))
            .Should().Within(TestTimeouts.Convergence).Emit();

        for (var k = 0; k < Listed.Count; k++)
        {
            var id = Listed[k].Id;
            Submit(stream, AutoArea, RowOf(cards, CatalogLayoutAreas.CardsDataId, id));
            var now = await records.Where(items => items.Any(i => i.Id == id && i.EffectiveUpdatePolicy == PackageUpdatePolicy.Auto))
                .Should().Within(TestTimeouts.Convergence).Emit();
            foreach (var other in Listed.Skip(k + 1))
                Assert.NotEqual(PackageUpdatePolicy.Auto, now!.Single(i => i.Id == other.Id).EffectiveUpdatePolicy);
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private ISynchronizationStream<JsonElement> OpenAll()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(CatalogNode),
            new LayoutAreaReference(Area) { Id = $"{Area}?{CatalogLayoutAreas.AllParam}=true" });
        return stream;
    }

    // The install registry as the catalog reads it, live; the Removed / Updated changes are its own signal.
    private IObservable<IReadOnlyList<PackageManifest>> Records()
    {
        var records = Mesh.ServiceProvider.GetRequiredService<IMeshService>().Query<MeshNode>(MeshQueryRequest.FromQuery(CatalogLayoutAreas.AllInstalledQuery))
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, (map, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    return change.Items.ToImmutableDictionary(n => n.Path);
                foreach (var item in change.Items)
                    map = change.ChangeType == QueryChangeType.Removed ? map.Remove(item.Path) : map.SetItem(item.Path, item);
                return map;
            })
            .Select(map => (IReadOnlyList<PackageManifest>)map.Values
                .Select(n => n.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
                .Where(m => m is not null)
                .Select(m => m!)
                .ToList())
            .Replay(1);
        records.Connect();
        return records;
    }

    private static async Task<ImmutableArray<JsonElement>> Rows(
        ISynchronizationStream<JsonElement> stream, string dataId, int count)
    {
        var rows = await stream.GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(dataId)))
            .Should().Within(TestTimeouts.Convergence).Match(
                r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == count,
                $"the client mirror holds the {count} rows of {dataId}",
                TestContext.Current.CancellationToken);
        return [.. rows.EnumerateArray().Select(r => r.Clone())];
    }

    /// <summary>The row the client renders for <paramref name="id"/>: its pointer, index and value.</summary>
    private static RowContext RowOf(ImmutableArray<JsonElement> rows, string dataId, string id)
    {
        var k = rows.Select((r, i) => (r, i)).Single(x => x.r.GetProperty("id").GetString() == id).i;
        return new RowContext
        {
            Pointer = $"{LayoutAreaReference.GetDataPointer(dataId)}/{k}",
            Index = k,
            Value = rows[k].Clone(),
        };
    }

    private static void Submit(ISynchronizationStream<JsonElement> stream, string area, RowContext? row)
        => stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId) { Row = row },
            actingUser: null, onRefused: null, onAccepted: null);

    private sealed class FixtureSource : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
            => Observable.Return<IReadOnlyList<PackageManifest>>(Listed);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
            => Observable.Throw<IReadOnlyList<PackageFile>>(new InvalidOperationException("These actions must never fetch package files."));
    }
}
