using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Apps;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Reactive.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The pure halves of the app directory (MeshWeaver.Plugins <c>Store/AppsOnTheInstance</c> §4):
/// reading an app root shape-tolerantly, the built-ins, the warm cache on virtual time, the
/// arrangement carried over from the legacy records, and the band's <c>Home:AppSource</c> switch.
/// </summary>
public class AppDirectoryPureTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static MeshNode Root(string path, string json, string? category = null) =>
        new(path)
        {
            NodeType = "Markdown",
            Name = path + " app",
            Icon = $"/static/{path}.svg",
            Description = $"{path} does things",
            Category = category,
            Content = JsonDocument.Parse(json).RootElement,
        };

    // ── App roots ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAppRoot_IsARootWhoseContentSaysApp()
    {
        var root = AppDirectory.ReadRoot(Root("Crm", """{"app":true,"entryPoint":"Crm/Pipeline"}""", "Business"), Options);
        root.Should().NotBeNull();
        root!.Path.Should().Be("Crm");
        root.Name.Should().Be("Crm app");
        root.Category.Should().Be("Business");
        root.OpenPath.Should().Be("Crm/Pipeline");
        root.ProbePath.Should().Be("Crm/Pipeline", "the probe is the ENTRY POINT — a gated root is everybody's storefront");
    }

    [Theory]
    [InlineData("""{"app":false}""")]
    [InlineData("""{"entryPoint":"X/Y"}""")]
    [InlineData("""{"app":"true"}""")]
    public void ARootWithoutTheAppFlag_IsNoApp(string json)
        => AppDirectory.ReadRoot(Root("X", json), Options).Should().BeNull();

    [Fact]
    public void ANonRoot_IsNoApp_EvenWithTheFlag()
        => AppDirectory.ReadRoot(new MeshNode("Inner", "X")
        {
            NodeType = "Markdown",
            Content = JsonDocument.Parse("""{"app":true}""").RootElement,
        }, Options).Should().BeNull("only a partition ROOT is an app");

    /// <summary>The content is read in whatever shape it arrives — typed content included, read by
    /// its concrete runtime type with a PascalCase property.</summary>
    [Fact]
    public void TypedContent_IsReadToo()
    {
        var node = new MeshNode("Typed") { NodeType = "Markdown", Content = new FakePluginContent(true, "Typed/Home") };
        var root = AppDirectory.ReadRoot(node, Options);
        root.Should().NotBeNull();
        root!.OpenPath.Should().Be("Typed/Home");
    }

    [Fact]
    public void AnAreaEntryPoint_IsProbedAtItsNode()
    {
        var root = AppDirectory.ReadRoot(Root("Approvals", """{"app":true,"entryPoint":"Approvals/Workspace/area/Approvals"}"""), Options)!;
        root.OpenPath.Should().Be("Approvals/Workspace/area/Approvals", "the tile opens the area");
        root.ProbePath.Should().Be("Approvals/Workspace", "but access is decided on the node");
    }

    [Fact]
    public void WithoutAnEntryPoint_TheRootIsBothTargetAndProbe()
    {
        var root = AppDirectory.ReadRoot(Root("Chess", """{"app":true}"""), Options)!;
        root.OpenPath.Should().Be("Chess");
        root.ProbePath.Should().Be("Chess");
    }

    // ── Built-ins ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryViewer_HasSettingsAndInbox_OnlyAnAdminHasAdministration()
    {
        var user = AppDirectory.BuiltIns("alice", isGlobalAdmin: false);
        user.Select(e => e.Id).Should().Equal(AppDirectory.SettingsId, AppDirectory.InboxId);
        user[0].OpenPath.Should().Be($"alice/{MeshNodeLayoutAreas.SettingsArea}");
        user[1].OpenPath.Should().Be($"alice/{InboxLayoutArea.AreaName}", "the same target SeedInboxAppLogonAction seeds");
        user.Should().OnlyContain(e => e.BuiltIn && e.LabelKey != null);

        var admin = AppDirectory.BuiltIns("root", isGlobalAdmin: true);
        admin.Select(e => e.Id).Should().Equal(AppDirectory.SettingsId, AppDirectory.InboxId, AppDirectory.AdminId);
        admin[2].OpenPath.Should().Be(AdminAppNodeType.Path);
        admin[2].LabelKey.Should().Be("adminApp.title");
    }

    [Fact]
    public void Compose_PutsBuiltInsFirst_AppsByName_AndNeverListsTheAdminRootTwice()
    {
        var roots = new[]
        {
            new AppRoot("Zeta", "Zeta", "z.svg", null, "B", null),
            new AppRoot("Admin", "Admin root", "a.svg", null, null, null),
            new AppRoot("Alpha", "Alpha", "a.svg", null, "A", "Alpha/Go"),
        };
        var list = AppDirectory.Compose("bob", roots, isGlobalAdmin: true);
        list.Select(e => e.Id).Should().Equal("Settings", "Inbox", "Admin", "Alpha", "Zeta");
        list.Single(e => e.Id == "Alpha").OpenPath.Should().Be("Alpha/Go");
    }

    // ── The warm cache, on virtual time ──────────────────────────────────────────────────────

    private sealed class CountingSource
    {
        public int Built;
        public int Subscriptions;
        public int Disposals;
        public BehaviorSubject<ImmutableList<AppDirectoryEntry>> Subject { get; } = new(ImmutableList<AppDirectoryEntry>.Empty);

        public IObservable<ImmutableList<AppDirectoryEntry>> For(string viewer)
        {
            Interlocked.Increment(ref Built);
            return Observable.Create<ImmutableList<AppDirectoryEntry>>(observer =>
            {
                Interlocked.Increment(ref Subscriptions);
                var inner = Subject.Subscribe(observer);
                return () => { Interlocked.Increment(ref Disposals); inner.Dispose(); };
            });
        }
    }

    [Fact]
    public void TheCache_ReusesTheStream_WithinTheWarmWindow_AndDisposesItAfter()
    {
        var scheduler = new TestScheduler();
        var source = new CountingSource();
        using var cache = new AppDirectoryCache(source.For, scheduler, AppDirectoryCache.WarmLifetime);

        var first = cache.ForViewer("alice").Subscribe(_ => { });
        first.Dispose();
        scheduler.AdvanceBy(TimeSpan.FromMinutes(4).Ticks);
        cache.IsWarm("alice").Should().BeTrue("4 minutes after the last subscriber it is still warm");
        source.Disposals.Should().Be(0);

        // Back within the window: the SAME computation, no recompute.
        ImmutableList<AppDirectoryEntry>? painted = null;
        var second = cache.ForViewer("alice").Subscribe(v => painted = v);
        painted.Should().NotBeNull("a warm stream paints at once from its Replay(1)");
        source.Subscriptions.Should().Be(1);
        source.Built.Should().Be(1);
        second.Dispose();

        // The clock SLID: 4 minutes after the second leave is still inside its own window …
        scheduler.AdvanceBy(TimeSpan.FromMinutes(4).Ticks);
        cache.IsWarm("alice").Should().BeTrue("each subscriber resets the clock");
        // … and 5 minutes after it, the computation is gone.
        scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks + 1);
        cache.IsWarm("alice").Should().BeFalse();
        source.Disposals.Should().Be(1, "the upstream is disposed when the warm window lapses");

        // Idle longer than the window: the next subscriber recomputes once.
        using var third = cache.ForViewer("alice").Subscribe(_ => { });
        source.Built.Should().Be(2);
        source.Subscriptions.Should().Be(2);
    }

    [Fact]
    public void TheCache_HoldsTheStream_WhileAnyoneIsSubscribed()
    {
        var scheduler = new TestScheduler();
        var source = new CountingSource();
        using var cache = new AppDirectoryCache(source.For, scheduler, AppDirectoryCache.WarmLifetime);

        using var keeper = cache.ForViewer("alice").Subscribe(_ => { });
        cache.ForViewer("alice").Subscribe(_ => { }).Dispose();
        scheduler.AdvanceBy(TimeSpan.FromHours(1).Ticks);
        cache.IsWarm("alice").Should().BeTrue("a subscriber is still there; the clock only runs when nobody is");
        source.Disposals.Should().Be(0);
    }

    [Fact]
    public void TwoConcurrentFirstSubscribers_ShareOneComputation()
    {
        var scheduler = new TestScheduler();
        var source = new CountingSource();
        using var cache = new AppDirectoryCache(source.For, scheduler, AppDirectoryCache.WarmLifetime);

        // Both subscribe BEFORE the source has produced anything — the first-paint race.
        var seenA = new List<ImmutableList<AppDirectoryEntry>>();
        var seenB = new List<ImmutableList<AppDirectoryEntry>>();
        IDisposable? a = null, b = null;
        Parallel.Invoke(
            () => a = cache.ForViewer("alice").Subscribe(v => { lock (seenA) seenA.Add(v); }),
            () => b = cache.ForViewer("alice").Subscribe(v => { lock (seenB) seenB.Add(v); }));

        source.Built.Should().Be(1, "both first subscribers join one computation");
        source.Subscriptions.Should().Be(1);
        var next = ImmutableList.Create(new AppDirectoryEntry("X", "X", "x.svg", null, null, "X"));
        source.Subject.OnNext(next);
        seenA.Last().Should().BeSameAs(next);
        seenB.Last().Should().BeSameAs(next);
        a!.Dispose();
        b!.Dispose();
    }

    /// <summary>A faulted computation is evicted at once — never replayed to the warm window's
    /// subscribers — and the next subscriber recomputes.</summary>
    [Fact]
    public void AFaultedComputation_IsEvicted_AndTheNextSubscriberRecomputes()
    {
        var built = 0;
        IObservable<ImmutableList<AppDirectoryEntry>> Source(string viewer)
        {
            built++;
            return built == 1
                ? Observable.Throw<ImmutableList<AppDirectoryEntry>>(new InvalidOperationException("roots query failed"))
                : Observable.Return(ImmutableList<AppDirectoryEntry>.Empty).Concat(Observable.Never<ImmutableList<AppDirectoryEntry>>());
        }
        using var cache = new AppDirectoryCache(Source, new TestScheduler(), AppDirectoryCache.WarmLifetime);

        Exception? error = null;
        cache.ForViewer("alice").Subscribe(_ => { }, e => error = e);
        error.Should().NotBeNull();
        cache.IsWarm("alice").Should().BeFalse("a fault is not kept warm");

        ImmutableList<AppDirectoryEntry>? painted = null;
        Exception? second = null;
        using var again = cache.ForViewer("alice").Subscribe(v => painted = v, e => second = e);
        built.Should().Be(2, "the next subscriber builds a fresh computation");
        second.Should().BeNull();
        painted.Should().NotBeNull();
    }

    [Fact]
    public void Viewers_DoNotShareAComputation()
    {
        var source = new CountingSource();
        using var cache = new AppDirectoryCache(source.For, new TestScheduler(), AppDirectoryCache.WarmLifetime);
        using var a = cache.ForViewer("alice").Subscribe(_ => { });
        using var b = cache.ForViewer("bob").Subscribe(_ => { });
        source.Built.Should().Be(2);
    }

    // ── The arrangement ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSeed_CarriesGroupOrderAndCustomGroup_FromTheLegacyRecords()
    {
        MeshNode Record(string id, string json) => new(id, "alice/_App")
        {
            NodeType = AppNodeType.NodeType,
            Content = JsonDocument.Parse(json).RootElement,
        };
        var arrangement = LauncherArrangementSource.FromLegacyRecords(
        [
            Record("Crm", """{"$type":"App","plugin":"Crm","group":"Work","order":2,"customGroup":true}"""),
            Record("Chess", """{"$type":"App","plugin":"Chess","group":"Games","order":1}"""),
            Record("Inbox", """{"$type":"App","plugin":"","openPath":"alice/Inbox","group":"","order":3}"""),
            Record("Doc", """{"$type":"App","plugin":"Doc"}"""),
        ], Options);

        arrangement.Entries.Keys.OrderBy(k => k).Should().Equal(["Chess", "Crm", "Inbox"], "a never-placed record carries nothing over");
        arrangement.For("Crm").Should().Be(new LauncherEntry { Group = "Work", Order = 2, CustomGroup = true });
        arrangement.For("Chess").Should().Be(new LauncherEntry { Group = "Games", Order = 1, CustomGroup = false });
        arrangement.For("Inbox").Should().Be(new LauncherEntry { Group = "", Order = 3, CustomGroup = false },
            "an area app is keyed by its record id, which is the built-in's id");
    }

    [Fact]
    public void Rows_TakeTheGroupFromTheArrangement_ElseTheCategory()
    {
        var entries = ImmutableList.Create(
            new AppDirectoryEntry("Crm", "CRM", "c.svg", "d", "Business", "Crm/Pipeline"),
            new AppDirectoryEntry("Chess", "Chess", "k.svg", null, "Games", "Chess"));
        var arrangement = new LauncherArrangement().Place("Crm", "Mine", 4);

        var rows = AppDirectoryQueryProvider.Rows("alice", entries, arrangement, Options);

        rows.Select(r => r.Path).Should().Equal("alice/_Apps/Crm", "alice/_Apps/Chess");
        rows.Should().OnlyContain(r => r.NodeType == AppNodeType.NodeType);
        var crm = rows[0].ContentAs<App>(Options)!;
        crm.Group.Should().Be("Mine");
        crm.Order.Should().Be(4);
        crm.Plugin.Should().Be("Crm");
        crm.OpenPath.Should().Be("Crm/Pipeline");
        rows[0].MainNode.Should().Be("Crm/Pipeline", "a tile navigates straight to the entry point");
        rows[1].ContentAs<App>(Options)!.Group.Should().Be("Games", "unplaced → the root's category");
        rows[1].ContentAs<App>(Options)!.Order.Should().Be(0);
    }

    [Theory]
    [InlineData("alice/_Apps/Crm", true, "alice", "Crm")]
    [InlineData("alice/_Apps/Edu~2FCourses", true, "alice", "Edu/Courses")]
    [InlineData("alice/_Apps/A~7EB", true, "alice", "A~B")]
    [InlineData("alice/_App/Crm", false, "", "")]
    [InlineData("alice/_Apps", false, "", "")]
    [InlineData("alice/_Apps/Crm/x", false, "", "")]
    public void ADirectoryRow_IsRecognisedByItsPath(string path, bool isRow, string owner, string appId)
    {
        LauncherArrangementPaths.TryParseRow(path, out var o, out var a).Should().Be(isRow);
        o.Should().Be(owner);
        a.Should().Be(appId);
    }

    [Fact]
    public void TheRootsQuery_IsTheDeclaredPackageRootShape_WithAnExplicitLimit()
    {
        var parsed = new QueryParser().Parse(AppDirectory.RootsQuery);
        parsed.CrossPartition.Should().BeTrue("a query without partitions:all is refused or incomplete");
        AppDirectory.RootsQuery.Should().Contain("nodeType:(Space OR Store/Plugin)");
        AppDirectory.RootsQuery.Should().Contain(MeshQueryRequest.CompleteQualifier);
        AppDirectory.RootsQuery.Should().Contain("content.app:true");
    }

    [Theory]
    [InlineData("Edu/Courses")]
    [InlineData("A~B")]
    [InlineData("A~2FB")]
    [InlineData("A/~7E/B~")]
    [InlineData("~")]
    [InlineData("Plain")]
    public void AnAppId_RoundTripsThroughItsRowId(string appId)
    {
        var row = LauncherArrangementPaths.RowIdFor(appId);
        row.Should().NotContain("/", "a row id is one path segment");
        LauncherArrangementPaths.AppIdOfRow(row).Should().Be(appId);
    }

    [Fact]
    public void DistinctAppIds_NeverShareARow()
    {
        string[] ids = ["A/B", "A~B", "A~2FB", "A~7EB", "A~~B", "A/~B"];
        ids.Select(LauncherArrangementPaths.RowIdFor).Should().OnlyHaveUniqueItems();
    }

    // ── Home:AppSource ───────────────────────────────────────────────────────────────────────

    /// <summary>With the default (<c>Records</c>) the band's query is EXACTLY what it was.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Records")]
    [InlineData("bogus")]
    public void RecordsSource_KeepsTheBandUnchanged(string? configured)
    {
        HomeAppSource.IsDirectory(configured).Should().BeFalse();
        var band = UserActivityLayoutAreas.BuildAppsBand("alice", null, fromDirectory: HomeAppSource.IsDirectory(configured));
        band.HiddenQuery.Should().Be("path:alice/_App scope:children nodeType:InstalledApp sort:Name-asc");
        band.ScopeTabs![0].Query.Should().Be((string?)band.HiddenQuery);
    }

    [Fact]
    public void DirectorySource_ReadsTheVirtualNamespace()
    {
        HomeAppSource.IsDirectory("Directory").Should().BeTrue();
        HomeAppSource.IsDirectory(" directory ").Should().BeTrue();
        var band = UserActivityLayoutAreas.BuildAppsBand("alice", null, fromDirectory: true);
        band.HiddenQuery.Should().Be("path:alice/_Apps scope:children nodeType:InstalledApp sort:Name-asc");
        band.ScopeTabs![0].Query.Should().Be((string?)band.HiddenQuery);
        band.ScopeTabs[0].Sortable.Should().BeTrue("drag and drop stays on: the view routes it to the arrangement");
    }

    [Fact]
    public void TheProvider_AnswersOnlyTheDirectoryNamespace()
    {
        var parser = new QueryParser();
        AppDirectoryQueryProvider.Target([parser.Parse("path:alice/_Apps scope:children nodeType:InstalledApp")])!
            .Value.Owner.Should().Be("alice");
        AppDirectoryQueryProvider.Target([parser.Parse("namespace:bob/_Apps")])!.Value.Owner.Should().Be("bob");
        AppDirectoryQueryProvider.Target([parser.Parse("path:alice/_App scope:children")]).Should().BeNull();
        AppDirectoryQueryProvider.Target([parser.Parse("path:alice/_Apps")]).Should().BeNull("the namespace node itself is nothing");
        AppDirectoryQueryProvider.Target([parser.Parse("namespace:x/_Apps/deeper")]).Should().BeNull();
    }

    private sealed record FakePluginContent(bool App, string EntryPoint);
}

/// <summary>
/// The app directory on a real mesh with REAL access control (<c>ConfigureMeshBase</c>, no public
/// admin): the visibility matrix (app flag × grant × global admin), a revoke re-emitting on the same
/// live subscription, the arrangement seeded once from the legacy records, and the virtual rows served
/// to the owner only.
/// </summary>
public class AppDirectoryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Granted = "dir-granted";
    private const string Ungranted = "dir-ungranted";
    private const string Admin = "dir-admin";
    private const string Revoked = "dir-revoked";
    private const string Seeded = "dir-seeded";

    /// <summary>A package root's content as a package carries it: its OWN typed record (the Store's
    /// <c>PluginContent</c> in production), with <c>app</c> and <c>entryPoint</c>.</summary>
    public sealed record TestPackageContent
    {
        /// <summary>Whether the package is an app.</summary>
        public bool App { get; init; }
        /// <summary>Where the app opens.</summary>
        public string? EntryPoint { get; init; }
    }

    // A package root as a mirror may index it: a Space whose typed content says app.
    private static MeshNode AppRootNode(string id, bool app) => new(id)
    {
        NodeType = "Space",
        Name = id,
        Category = "Business",
        State = MeshNodeState.Active,
        Content = new TestPackageContent { App = app, EntryPoint = $"{id}/Home" },
    };

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureHub(config => config.WithType<TestPackageContent>(nameof(TestPackageContent)))
            .AddMeshNodes(
            AppRootNode("DirApp", app: true),
            AppRootNode("DirNotAnApp", app: false),
            // Readable ONLY at its entry point: the probe must be the entry point, not the root.
            AppRootNode("DirEntryOnly", app: true),
            AssignmentNodeFactory.UserRole(Granted, "Viewer", "DirEntryOnly/Home"),
            // Granted reads both packages' entry points; only the app flag tells them apart.
            AssignmentNodeFactory.UserRole(Granted, "Viewer", "DirApp"),
            AssignmentNodeFactory.UserRole(Granted, "Viewer", "DirNotAnApp"),
            // The admin is a PLATFORM admin — no data grant on the app.
            AssignmentNodeFactory.UserRole(Admin, "Admin", AdminAppNodeType.Path));

    private AppDirectory Directory => Mesh.ServiceProvider.GetRequiredService<AppDirectory>();

    private async Task<ImmutableList<AppDirectoryEntry>> Settled(
        IObservable<ImmutableList<AppDirectoryEntry>> stream, Func<ImmutableList<AppDirectoryEntry>, bool> until)
        => await stream.Where(until).FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    [Theory(Timeout = 60000)]
    [InlineData(Granted, new[] { "Settings", "Inbox", "DirApp", "DirEntryOnly" })]
    [InlineData(Ungranted, new[] { "Settings", "Inbox" })]
    [InlineData(Admin, new[] { "Settings", "Inbox", "Admin" })]
    public async Task Visibility_IsAppFlagAndReadOnTheEntryPoint_PlusBuiltIns(string viewer, string[] expected)
    {
        var ct = TestContext.Current.CancellationToken;
        // Wait until the shared roots query has seen every seeded package, then read the FIRST
        // computation over it — never a count that a transient partial list could satisfy.
        await Directory.Roots()
            .Where(roots => roots.Any(r => r.Path == "DirApp") && roots.Any(r => r.Path == "DirEntryOnly"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var list = await Directory.Compute(viewer).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        list.Select(e => e.Id).Should().Equal(expected);
        list.Should().NotContain(e => e.Id == "DirNotAnApp", "a package without app:true is no tile, whatever the grant");
    }

    [Fact(Timeout = 60000)]
    public async Task ARevoke_ReEmitsWithoutTheApp_OnTheSameSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var seen = new ReplaySubject<ImmutableList<AppDirectoryEntry>>();
        using var subscription = Directory.Compute(Revoked).Subscribe(seen);

        await Settled(seen, l => l.Count == 2 && l.All(e => e.BuiltIn));

        var grant = AssignmentNodeFactory.UserRole(Revoked, "Viewer", "DirApp");
        await access.RunAsSystem(() => mesh.CreateNode(grant)).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await Settled(seen, l => l.Any(e => e.Id == "DirApp"));

        await access.RunAsSystem(() => mesh.DeleteNode(grant.Path)).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var after = await Settled(seen.SkipWhile(l => l.All(e => e.Id != "DirApp")), l => l.All(e => e.Id != "DirApp"));
        after.Select(e => e.Id).Should().Equal("Settings", "Inbox");
    }

    [Fact(Timeout = 60000)]
    public async Task TheDirectoryRows_AreServedToTheOwner_AndSeedTheArrangementOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await CreateUserAsync(Seeded, ct);
        // The owner's self-grant is written after the user exists: wait for it through the _Access
        // LISTING (never a point read of a path that may not exist yet).
        await mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{Seeded}/_Access nodeType:AccessAssignment"))
            .Scan(0, (seen, change) => seen + change.Items.Count(n => n.Id == $"{Seeded}_Access"))
            .Where(seen => seen > 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        // A legacy record the viewer arranged.
        await access.RunAsSystem(() => mesh.CreateNode(new MeshNode("Inbox", $"{Seeded}/{AppNodeType.UserNamespace}")
        {
            NodeType = AppNodeType.NodeType,
            Name = "Inbox",
            State = MeshNodeState.Active,
            Content = new App { OpenPath = $"{Seeded}/Inbox", Group = "Mine", Order = 7 },
        })).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var query = UserActivityLayoutAreas.AppsQuery(Seeded, fromDirectory: true);
        var owner = new AccessContext { ObjectId = Seeded, Name = Seeded };

        // The owner sees the built-ins as rows, the Inbox carrying the seeded arrangement.
        var rows = await Live(access.RunAs(owner, () => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))))
            .Where(items => items.Any(r => r.Id == "Inbox" && r.ContentAs<App>(Mesh.JsonSerializerOptions)?.Group == "Mine"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        rows.Select(r => r.Id).Should().Contain(["Settings", "Inbox"]);
        rows.Single(r => r.Id == "Inbox").ContentAs<App>(Mesh.JsonSerializerOptions)!.Order.Should().Be(7);

        var arrangementPath = LauncherArrangementPaths.PathFor(Seeded);
        var arrangement = await Mesh.GetWorkspace().GetMeshNodeStream(arrangementPath)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        arrangement!.NodeType.Should().Be(LauncherArrangementPaths.NodeType);
        arrangement.ContentAs<LauncherArrangement>(Mesh.JsonSerializerOptions)!.For("Inbox")!.Group.Should().Be("Mine");

        // Written ONCE: the seed succeeded, so this process never attempts it again for the viewer —
        // a second render reads the node and writes nothing.
        var source = Mesh.ServiceProvider.GetRequiredService<LauncherArrangementSource>();
        source.IsSeeded(Seeded).Should().BeTrue();
        var seededVersion = arrangement.Version;
        await Live(access.RunAs(owner, () => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))))
            .Where(items => items.Any(r => r.Id == "Inbox"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        (await Mesh.GetWorkspace().GetMeshNodeStream(arrangementPath).Where(n => n is not null)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))!
            .Version.Should().Be(seededVersion, "a second render does not write the arrangement again");

        // A rearrangement goes to the arrangement node, never to the virtual row.
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        await access.RunAs(owner, () => LauncherArrangementPaths.Place(
                cache, $"{Seeded}/_Apps/Inbox", "Elsewhere", 2, Mesh.JsonSerializerOptions))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await Live(access.RunAs(owner, () => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))))
            .Where(items => items.Any(r => r.Id == "Inbox" && r.ContentAs<App>(Mesh.JsonSerializerOptions)?.Group == "Elsewhere"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        // Someone else asking for the owner's directory gets nothing.
        var intruder = new AccessContext { ObjectId = Ungranted, Name = Ungranted };
        var foreign = await access.RunAs(intruder, () => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        foreign.Items.Should().BeEmpty("a viewer's directory is served to that viewer only");
    }

    /// <summary>The live row set of a query — its Initial folded with every later delta.</summary>
    private static IObservable<List<MeshNode>> Live(IObservable<QueryResultChange<MeshNode>> changes)
        => LauncherArrangementSource.Fold(changes).Select(rows => rows.Values.ToList());

    private async Task CreateUserAsync(string path, CancellationToken ct)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        await access.RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
        {
            NodeType = UserNodeType.NodeType,
            Name = path,
            State = MeshNodeState.Active,
            Content = new User { FullName = path },
        })).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }
}
