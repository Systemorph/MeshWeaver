using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
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
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Retained orphan-removal clicks run through the actual catalog, owner dispatch and installer
/// against records in the isolated test mesh. Package files are local fixture content only.
/// </summary>
public class CatalogOrphanActionIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CatalogNode = "OrphanIdentityCatalog";
    private const string Area = CatalogLayoutAreas.CatalogArea;
    private readonly AsyncSubject<LayoutAreaHost> owner = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog().AddMeshNodes(
            new MeshNode("OrphanIdentityCatalogType")
            {
                HubConfiguration = config => config.AddDefaultLayoutAreas().AddLayout(layout =>
                    layout.WithView(Area, (host, _) =>
                    {
                        owner.OnNext(host);
                        owner.OnCompleted();
                        return CatalogLayoutAreas.RenderFromSource(host, new FixtureSource(), "HEAD", null, "fixture");
                    }))
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
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

    private static IObservable<T> ReadOwner<T>(LayoutAreaHost host, Func<T> read)
        => Observable.Create<T>(observer =>
        {
            host.Update(LayoutAreaReference.Data, data =>
            {
                try { observer.OnNext(read()); observer.OnCompleted(); }
                catch (Exception error) { observer.OnError(error); }
                return data;
            });
            return Disposable.Empty;
        });

    private static IEnumerable<string> OrphanCards(LayoutAreaHost host)
        => host.GetControl(Area) is StackControl root
            ? root.Areas.Where(child => child.Id is string id && id.StartsWith("orphan-", StringComparison.Ordinal))
                .Select(child => Area + "/" + child.Id)
            : [];

    private static string FindAction(LayoutAreaHost host, string name)
    {
        var card = OrphanCards(host).Single(path => ((StackControl)host.GetControl(path)!).Areas
            .Any(child => host.GetControl(path + "/" + child.Id) is LabelControl label && Equals(label.Data, name)));
        var action = ((StackControl)host.GetControl(card)!).Areas
            .Single(child => host.GetControl(card + "/" + child.Id) is ButtonControl { IsClickable: true });
        return card + "/" + action.Id;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetainedRemoveClick_KeepsItsOrphanWhenANeighborIsInsertedOrRemoved(bool insert)
    {
        var first = Package("OrphanA", "Package A");
        var intended = Package("OrphanB", "Package B");
        var last = Package("OrphanC", "Package C");
        await Install(intended);
        await Install(last);
        if (!insert)
            await Install(first);

        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(CatalogNode), new LayoutAreaReference(Area) { Id = Area + "?all=true" });
        using var subscription = stream.Subscribe();
        var host = await owner.Should().Within(TestTimeouts.Convergence).Emit();
        await stream.SelectMany(_ => ReadOwner(host, () => OrphanCards(host).Count()))
            .Where(count => count == (insert ? 2 : 3))
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit();
        var retained = await ReadOwner(host, () => FindAction(host, intended.Name!))
            .Should().Within(TestTimeouts.Convergence).Emit();

        if (insert)
            await Install(first);
        else
            Assert.True(await PackageInstaller.RemoveInstalledRecord(Mesh, first.Id)
                .Should().Within(TestTimeouts.Convergence).Emit());

        await stream.SelectMany(_ => ReadOwner(host, () => OrphanCards(host).Count()))
            .Where(count => count == (insert ? 3 : 2))
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit();
        var current = await ReadOwner(host, () => FindAction(host, intended.Name!))
            .Should().Within(TestTimeouts.Convergence).Emit();
        Assert.Equal(retained, current);

        // A per-node stream does not emit a deletion frame after its owner disappears. Observe
        // the actual install-registry query, whose Removed change is the catalog's own signal.
        var records = CatalogLayoutAreas.ObserveInstalledManifests(host).Replay(1);
        using var recordSubscription = records.Connect();
        await records.Where(items => items.Any(item => item.Id == intended.Id))
            .Should().Within(TestTimeouts.Convergence).Emit();
        var streamHub = host.Stream.Hub;
        streamHub.Post(new ClickedEvent(retained, host.Stream.ClientId), options => options.WithTarget(streamHub.Address));
        var remaining = await records.Where(items => items.All(item => item.Id != intended.Id))
            .Should().Within(TestTimeouts.Convergence).Emit();
        Assert.Contains(remaining, item => item.Id == last.Id);
        if (insert)
            Assert.Contains(remaining, item => item.Id == first.Id);
        // Removing the record must leave the package's installed content in place.
        Assert.NotNull(await Mesh.GetMeshNode(intended.Id + "/Doc")
            .Should().Within(TestTimeouts.Convergence).Emit());
    }

    private sealed class FixtureSource : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
            => Observable.Return<IReadOnlyList<PackageManifest>>([Package("AvailableFixture", "Available package")]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
            => Observable.Throw<IReadOnlyList<PackageFile>>(new InvalidOperationException("Orphan removal must never fetch package files."));
    }
}
