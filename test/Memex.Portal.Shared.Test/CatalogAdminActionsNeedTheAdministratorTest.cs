using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 The catalog's two administrator actions — the orphaned record's Remove and the card's
/// update-policy choice — run as System (the partition policy denies them to every user identity),
/// so nothing downstream of the click can refuse a non-administrator. The gate is the ACTION:
/// it acts only on what the server's current page offers (an offer set that is empty for a viewer
/// who is not a global administrator) and only when the acting identity is that administrator.
///
/// <para>This mesh grants NO public admin (<c>ConfigureMeshBase</c>): the DevLogin identity is the
/// one global administrator, and a second identity holds Viewer on the catalog node and nothing
/// else. Each refusal is followed by the same click from the administrator, which acts — so the
/// silence before it is the gate and not a click that could never have worked.</para>
/// </summary>
public class CatalogAdminActionsNeedTheAdministratorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CatalogNode = "AdminGateCatalog";
    private const string Area = CatalogLayoutAreas.CatalogArea;

    private static readonly AccessContext Viewer = new()
    {
        ObjectId = "catalog-viewer", Name = "Catalog Viewer", Email = "catalog-viewer@meshweaver.io",
    };

    private static string RemoveArea => $"{Area}/orphans/{ItemTemplateControl.ViewArea}/remove";

    private static string AutoArea => $"{Area}/cards/{ItemTemplateControl.ViewArea}/updatePolicy/choices/auto";

    private static readonly ImmutableList<PackageManifest> Listed = [Package("GateListed", "Gate Listed")];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddPluginCatalog().AddMeshNodes(
            new MeshNode("AdminGateCatalogType")
            {
                HubConfiguration = config => config.AddDefaultLayoutAreas().AddLayout(layout =>
                    layout.WithView(Area, (host, _) =>
                        CatalogLayoutAreas.RenderFromSource(host, new FixtureSource(), "HEAD", null, "fixture")))
            },
            new MeshNode(CatalogNode) { NodeType = "AdminGateCatalogType" },
            // The viewer may OPEN the catalog and nothing more: not a global administrator.
            new MeshNode($"{Viewer.ObjectId}_Access", $"{CatalogNode}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = "Catalog viewer",
                Content = new AccessAssignment
                {
                    AccessObject = Viewer.ObjectId,
                    DisplayName = Viewer.Name,
                    Roles = [new RoleAssignment { Role = "Viewer" }],
                },
                MainNode = CatalogNode,
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// A viewer who is not a global administrator opens the ALL page and clicks Remove on a real
    /// orphan row and "Auto" on a real card row. Neither acts. The administrator's same clicks then
    /// remove the record and set the policy.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ANonAdministrator_CannotRemoveAnInstallOrSetAnUpdatePolicy()
    {
        var orphan = Package("GateOrphan", "Gate Orphan");
        await Install(orphan);
        await Install(Listed[0]);
        var records = Records();
        await records.Where(items => HasPolicyOtherThanAuto(items, Listed[0].Id) && items.Any(i => i.Id == orphan.Id))
            .Should().Within(TestTimeouts.Convergence).Emit();

        ActAs(TestUsers.Admin);
        var adminStream = OpenAll();
        var orphanRow = RowOf(await Rows(adminStream, CatalogLayoutAreas.OrphansDataId, 1),
            CatalogLayoutAreas.OrphansDataId, orphan.Id);
        var cardRow = RowOf(await Rows(adminStream, CatalogLayoutAreas.CardsDataId, Listed.Count),
            CatalogLayoutAreas.CardsDataId, Listed[0].Id);

        ActAs(Viewer);
        var viewerStream = OpenAll();
        await Rows(viewerStream, CatalogLayoutAreas.CardsDataId, Listed.Count);

        await Click(viewerStream, RemoveArea, orphanRow, Viewer);
        await Click(viewerStream, AutoArea, cardRow, Viewer);
        await records.Where(items => items.All(i => i.Id != orphan.Id) || !HasPolicyOtherThanAuto(items, Listed[0].Id))
            .Should().NotEmit(1.Seconds(),
                "a viewer who is not a global administrator is offered neither action, so the "
                + "click must remove nothing and re-pin nothing",
                TestContext.Current.CancellationToken);

        // The control: the administrator's same two clicks act.
        ActAs(TestUsers.Admin);
        await Click(adminStream, RemoveArea, orphanRow, TestUsers.Admin);
        await Click(adminStream, AutoArea, cardRow, TestUsers.Admin);
        await records.Where(items => items.All(i => i.Id != orphan.Id) && !HasPolicyOtherThanAuto(items, Listed[0].Id))
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the administrator's clicks remove the orphaned record and set the policy",
                TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// On a page whose offers were computed for the administrator, a click made under another
    /// identity acts on nothing — the page's stream accepts input only from its subscriber and
    /// answers the click with a refusal — and the administrator's own click acts.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheAdministratorActions_ActOnlyForTheAdministrator()
    {
        var orphan = Package("GateBorrowedOrphan", "Borrowed Orphan");
        await Install(orphan);
        await Install(Listed[0]);
        var records = Records();
        await records.Where(items => HasPolicyOtherThanAuto(items, Listed[0].Id) && items.Any(i => i.Id == orphan.Id))
            .Should().Within(TestTimeouts.Convergence).Emit();

        ActAs(TestUsers.Admin);
        var adminStream = OpenAll();
        var orphanRow = RowOf(await Rows(adminStream, CatalogLayoutAreas.OrphansDataId, 1),
            CatalogLayoutAreas.OrphansDataId, orphan.Id);
        var cardRow = RowOf(await Rows(adminStream, CatalogLayoutAreas.CardsDataId, Listed.Count),
            CatalogLayoutAreas.CardsDataId, Listed[0].Id);

        (await Answer(adminStream, RemoveArea, orphanRow, Viewer)).Should().NotBeNull(
            "the page was opened by the administrator, so a click under another identity is refused");
        (await Answer(adminStream, AutoArea, cardRow, Viewer)).Should().NotBeNull(
            "the page was opened by the administrator, so a click under another identity is refused");
        await records.Where(items => items.All(i => i.Id != orphan.Id) || !HasPolicyOtherThanAuto(items, Listed[0].Id))
            .Should().NotEmit(1.Seconds(),
                "the acting identity is not the administrator, so neither action runs",
                TestContext.Current.CancellationToken);

        // The control: the same clicks, on the same stream, by the administrator.
        await Click(adminStream, RemoveArea, orphanRow, TestUsers.Admin);
        await Click(adminStream, AutoArea, cardRow, TestUsers.Admin);
        await records.Where(items => items.All(i => i.Id != orphan.Id) && !HasPolicyOtherThanAuto(items, Listed[0].Id))
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the administrator's own clicks act", TestContext.Current.CancellationToken);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static PackageManifest Package(string id, string name) => new()
    {
        Id = id, Name = name, Version = "1.0.0", Kind = PackageKind.Content,
        Category = "Fixtures", TargetPartition = id, SourceFolder = id,
    };

    private static bool HasPolicyOtherThanAuto(IReadOnlyList<PackageManifest> items, string id)
        => items.Any(i => i.Id == id && i.EffectiveUpdatePolicy != PackageUpdatePolicy.Auto);

    /// <summary>
    /// Makes <paramref name="identity"/> the one the NEXT subscription is opened as: the host
    /// identity, and the flow's delivery-scoped context — which the installs above leave on the
    /// System identity they ran as, and a stream opened in that state would be System's page.
    /// </summary>
    private void ActAs(AccessContext identity)
    {
        TestUsers.DevLogin(Mesh, identity);
        Mesh.ServiceProvider.GetRequiredService<AccessService>().SetContext(identity);
    }

    private Task<InstallResult> Install(PackageManifest manifest)
        => PackageInstaller.Install(Mesh, manifest,
                [new PackageFile($"{manifest.Id}/Doc.md", $"# {manifest.Name}")], "HEAD")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    private ISynchronizationStream<JsonElement> OpenAll()
        => GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(CatalogNode),
            new LayoutAreaReference(Area) { Id = $"{Area}?{CatalogLayoutAreas.AllParam}=true" });

    // The install registry as the catalog reads it, live, read as the mesh itself.
    private IObservable<IReadOnlyList<PackageManifest>> Records()
    {
        var records = Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(CatalogLayoutAreas.AllInstalledQuery))
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
                .OfType<PackageManifest>()
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

    /// <summary>
    /// Sends the click as <paramref name="actingUser"/> and waits for the owner's receipt — the
    /// click action has RUN by then (it answers when the action is done), so the silence asserted
    /// after it is the action's decision and not a click still in flight.
    /// </summary>
    private static async Task Click(
        ISynchronizationStream<JsonElement> stream, string area, RowContext row, AccessContext actingUser)
        => (await Answer(stream, area, row, actingUser)).Should().BeNull(
            "the owner accepts a click made by the identity the page was opened for");

    /// <summary>
    /// Sends the click as <paramref name="actingUser"/> and returns the owner's answer:
    /// <c>null</c> when the click was accepted (its action has run by then), the refusal sentence
    /// otherwise.
    /// </summary>
    private static async Task<string?> Answer(
        ISynchronizationStream<JsonElement> stream, string area, RowContext row, AccessContext actingUser)
    {
        var answer = new ReplaySubject<string?>(1);
        stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId) { Row = row }, actingUser,
            onRefused: answer.OnNext,
            onAccepted: () => answer.OnNext(null));
        return await answer.Should().Within(TestTimeouts.Convergence).Emit(
            "the owner answers every click", TestContext.Current.CancellationToken);
    }

    private sealed class FixtureSource : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
            => Observable.Return<IReadOnlyList<PackageManifest>>(Listed);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
            => Observable.Throw<IReadOnlyList<PackageFile>>(new InvalidOperationException("These actions must never fetch package files."));
    }
}
