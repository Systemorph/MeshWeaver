using System.Collections.Immutable;
using System.Reflection;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Actual catalog projection and owner ClickedEvent dispatch, with
/// file fetch intercepted before the real installer can receive any payload or write any node.
/// </summary>
public class CatalogActionIdentityTest : HubTestBase
{
    private const string Area = "CatalogActions";
    private readonly AsyncSubject<LayoutAreaHost> owner = new();

    public CatalogActionIdentityTest(ITestOutputHelper output) : base(output)
        => Services.AddSingleton<AccessService>();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddLayout(layout => layout.WithView(Area, (host, _) =>
        {
            owner.OnNext(host);
            owner.OnCompleted();
            return Controls.Stack;
        }));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static PackageManifest Package(string id, string name) => new()
    {
        Id = id, Name = name, Version = "2.0.0", Kind = PackageKind.Content,
        Category = "Fixtures", TargetPartition = "Fixture" + id,
    };

    private static UiControl Project(LayoutAreaHost host, IPackageSource source,
        IReadOnlyList<PackageManifest> packages, IReadOnlyList<MeshNode> installed)
        => Assert.IsAssignableFrom<UiControl>(typeof(CatalogLayoutAreas)
            .GetMethod("BuildPackages", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [host, source, "HEAD", null, "Fixture source",
                CatalogLayoutAreas.Plan("Fixtures", null, packages), installed,
                ImmutableHashSet<string>.Empty, false, new ModuleActivationReport([])]));

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

    private static IEnumerable<(string Path, object? Control)> Children(LayoutAreaHost host, string path)
        => ((StackControl)host.GetControl(path)!).Areas.Select(child =>
        {
            var childPath = path + "/" + child.Id;
            return (childPath, host.GetControl(childPath));
        });

    private static (string Card, string Action, string Label) FindAction(LayoutAreaHost host, string name)
    {
        var card = Children(host, Area).Single(child => child.Control is StackControl
            && Children(host, child.Path).Any(grandchild =>
                grandchild.Control is LabelControl label && Equals(label.Data, name))).Path;
        var action = Children(host, card).Single(child => child.Control is ButtonControl { IsClickable: true });
        return (card, action.Path, ((ButtonControl)action.Control!).Data.ToString()!);
    }

    [Theory]
    [InlineData(false, "same")]
    [InlineData(false, "insert")]
    [InlineData(false, "rename")]
    [InlineData(false, "description")]
    [InlineData(false, "slash")]
    [InlineData(true, "same")]
    [InlineData(true, "insert")]
    [InlineData(true, "rename")]
    [InlineData(true, "description")]
    [InlineData(true, "slash")]
    public async Task RetainedInstallOrUpdateClick_MustKeepItsPackage(bool update, string change)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        await stream.GetControlStream(Area).Where(c => c is StackControl)
            .Should().Within(TestTimeouts.Convergence).Emit();
        var host = await owner.Should().Within(TestTimeouts.Convergence).Emit();
        using var fetched = new ReplaySubject<string>();
        var first = Package(change == "slash" ? "Plugins-Store" : "package-a", "Package A");
        var intended = Package(change == "slash" ? "Plugins/Store" : "package-b", "Package B");
        var last = Package(change == "slash" ? "Plugins" : "package-c", "Package C");
        var source = new RecordingSource(fetched);
        IReadOnlyList<MeshNode> installed = update
            ? [new MeshNode(intended.Id, PackageInstaller.InstalledPartition)
                { Content = intended with { Version = "1.0.0" } }]
            : [];

        host.UpdateArea(Area, Project(host, source, [intended, last], installed));
        var retained = await ReadOwner(host, () => FindAction(host, intended.Name!))
            .Should().Within(TestTimeouts.Convergence).Emit();
        retained.Label.Should().Contain(update ? "Update" : "Install");

        var revised = change switch
        {
            "rename" => intended with { Name = "Package Z" },
            "description" => intended with { Description = "New description shifts optional content" },
            _ => intended
        };
        if (change == "rename")
        {
            Assert.Equal([intended.Id, last.Id], CatalogLayoutAreas.InCategory([intended, last], "Fixtures").Select(p => p.Id));
            Assert.Equal([last.Id, intended.Id], CatalogLayoutAreas.InCategory([revised, last], "Fixtures").Select(p => p.Id));
        }
        host.UpdateArea(Area, Project(host, source,
            change is "insert" or "slash" ? [first, revised, last] : [revised, last], installed));
        var current = await ReadOwner(host, () => FindAction(host, revised.Name!))
            .Should().Within(TestTimeouts.Convergence).Emit();
        Output.WriteLine("Retained {0} ({1}); current intended {2}; change={3}",
            retained.Action, retained.Label, current.Action, change);
        Assert.Equal(retained.Action, current.Action);
        if (change == "slash")
            Assert.Equal(3, retained.Action.Split('/').Length); // Area / encoded card / install.

        // The actual owner dispatch resolves the retained event.Area against its CURRENT controls.
        // No action delegate is invoked directly by this test.
        var streamHub = host.Stream.Hub;
        streamHub.Post(new ClickedEvent(retained.Action, host.Stream.ClientId),
            options => options.WithTarget(streamHub.Address));
        var requested = await fetched.Take(1).Should().Within(TestTimeouts.Convergence).Emit();
        Output.WriteLine("Actual source FetchPackageFiles target: {0}; intended: {1}", requested, intended.Id);
        Assert.Equal(intended.Id, requested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_removed_or_now_installed_package_has_no_retained_install_target(bool nowInstalled)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        await stream.GetControlStream(Area).Where(c => c is StackControl)
            .Should().Within(TestTimeouts.Convergence).Emit();
        var host = await owner.Should().Within(TestTimeouts.Convergence).Emit();
        using var fetched = new ReplaySubject<string>();
        var intended = Package("package-b", "Package B");
        var other = Package("package-c", "Package C");
        var source = new RecordingSource(fetched);
        host.UpdateArea(Area, Project(host, source, [intended, other], []));
        var retained = await ReadOwner(host, () => FindAction(host, intended.Name!))
            .Should().Within(TestTimeouts.Convergence).Emit();
        host.UpdateArea(Area, Project(host, source, nowInstalled ? [intended, other] : [other],
            nowInstalled ? [new MeshNode(intended.Id, PackageInstaller.InstalledPartition) { Content = intended }] : []));
        // This is the exact current-control lookup OnClick uses. A missing action must not be
        // reoccupied by the neighboring package or a different command on the same card.
        Assert.True(await ReadOwner(host, () => host.GetControl(retained.Action) is null)
            .Should().Within(TestTimeouts.Convergence).Emit());
        var next = await ReadOwner(host, () => FindAction(host, other.Name!))
            .Should().Within(TestTimeouts.Convergence).Emit();
        var streamHub=host.Stream.Hub;
        streamHub.Post(new ClickedEvent(retained.Action,host.Stream.ClientId),o=>o.WithTarget(streamHub.Address));
        streamHub.Post(new ClickedEvent(next.Action,host.Stream.ClientId),o=>o.WithTarget(streamHub.Address));
        Assert.Equal(other.Id,await fetched.Take(1).Should().Within(TestTimeouts.Convergence).Emit());
    }

    private sealed class RecordingSource(IObserver<string> fetched) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
            => Observable.Return<IReadOnlyList<PackageManifest>>([]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
            => Observable.Defer(() =>
            {
                fetched.OnNext(package.Id);
                // Not even an empty payload is emitted: SelectMany cannot invoke PackageInstaller.
                return Observable.Empty<IReadOnlyList<PackageFile>>();
            });
    }
}
