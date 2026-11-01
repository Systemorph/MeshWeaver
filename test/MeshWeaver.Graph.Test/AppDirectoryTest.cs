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
    public void ThePersonTile_CarriesTheViewersName_PictureAndASettingsDescription_InTheirLanguage()
    {
        var person = new PersonTile("Roland Bürgi", "data:image/png;base64,AAAA", "de");
        var tile = AppDirectory.BuiltIns("rbuergi", isGlobalAdmin: false, person)[0];
        tile.Id.Should().Be(AppDirectory.SettingsId);
        tile.Name.Should().Be("Roland Bürgi");
        tile.LabelKey.Should().BeNull("a label key would repaint the tile as the catalog's \"Settings\"");
        tile.Icon.Should().Be("data:image/png;base64,AAAA");
        tile.OpenPath.Should().Be($"rbuergi/{MeshNodeLayoutAreas.SettingsArea}");
        tile.Description.Should().Be(LocalizationCatalog.Get(AppDirectory.PersonSettingsDescriptionKey, "de"))
            .And.Contain("Einstellungen");

        var noPicture = AppDirectory.BuiltIns("rbuergi", isGlobalAdmin: false, person with { Icon = null, Locale = null })[0];
        noPicture.Icon.Should().Be(AppDirectory.SettingsIcon);
        noPicture.Description.Should().Contain("settings");
    }

    [Fact]
    public void WithoutAName_TheTileStaysThePlainSettings()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        AppDirectory.ReadPerson(null, options).Should().BeNull();
        AppDirectory.ReadPerson(new MeshNode("acme") { NodeType = "Space", Name = "Acme Space" }, options)
            .Should().BeNull("only a USER node names the person tile");
        AppDirectory.PersonQuery("alice").Should().Be($"path:alice nodeType:{UserNodeType.NodeType}");
        AppDirectory.ReadPerson(new MeshNode("carol") { NodeType = "User", Name = "Carol Roe", Icon = "content:me.png" }, options)!
            .Icon.Should().Be(MeshNodeImageHelper.ResolvePictureUrl("content:me.png", "carol"))
            .And.NotStartWith("content:", "the synthetic _Apps row would resolve a relative picture against itself");
        foreach (var glyph in new[] { "Person", "🙂", "<svg xmlns='http://www.w3.org/2000/svg'/>" })
            AppDirectory.ReadPerson(new MeshNode("dave") { NodeType = "User", Name = "Dave Roe", Icon = glyph }, options)!
                .Icon.Should().BeNull($"'{glyph}' is no picture — the tile keeps the settings icon");
        AppDirectory.ReadPerson(new MeshNode("alice") { NodeType = "User", Name = "alice" }, options)
            .Should().BeNull("a name that is just the id says nothing the plain tile does not");
        AppDirectory.ReadPerson(new MeshNode("alice")
        {
            NodeType = "User",
            Name = "alice",
            Content = System.Text.Json.JsonSerializer.SerializeToElement(new { fullName = "Alice Muster", locale = "de" }),
        }, options).Should().Be(new PersonTile("Alice Muster", null, "de"));
        AppDirectory.ReadPerson(new MeshNode("bob") { NodeType = "User", Name = "Bob Roe", Icon = "/pic.png" }, options)
            .Should().Be(new PersonTile("Bob Roe", "/pic.png", null));
        AppDirectory.Compose("alice", [], isGlobalAdmin: false, person: null)[0].LabelKey.Should().Be("common.settings");
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

    /// <summary>An entry that only carries an ORDER keeps the category (<c>null</c> group); a
    /// deliberately ungrouped tile (<c>""</c>) stays ungrouped.</summary>
    [Fact]
    public void ANullGroup_FallsBackToTheCategory_AnEmptyGroupStaysUngrouped()
    {
        var entries = ImmutableList.Create(
            new AppDirectoryEntry("Crm", "CRM", "c.svg", null, "Business", "Crm"),
            new AppDirectoryEntry("Chess", "Chess", "k.svg", null, "Games", "Chess"));
        var arrangement = new LauncherArrangement
        {
            Entries = ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, new[]
            {
                KeyValuePair.Create("Crm", new LauncherEntry { Group = null, Order = 3 }),
                KeyValuePair.Create("Chess", new LauncherEntry { Group = "", Order = 1 }),
            }),
        };
        var rows = AppDirectoryQueryProvider.Rows("alice", entries, arrangement, Options);
        rows[0].ContentAs<App>(Options)!.Group.Should().Be("Business");
        rows[0].ContentAs<App>(Options)!.Order.Should().Be(3);
        rows[1].ContentAs<App>(Options)!.Group.Should().Be("");
        rows[0].ContentAs<App>(Options)!.Source.Should().Be(AppSources.Directory);
    }

    /// <summary>
    /// <c>adminOnly: true</c> makes the tile a GLOBAL ADMIN's only, like Administration (Feedback's
    /// review app): an admin sees it, everyone else does not, and only a literal <c>true</c> opts in.
    /// </summary>
    [Theory]
    [InlineData("""{"app":true,"adminOnly":true}""", true)]
    [InlineData("""{"app":true,"adminOnly":false}""", false)]
    [InlineData("""{"app":true,"adminOnly":"true"}""", false)]
    [InlineData("""{"app":true}""", false)]
    public void AnAppRoot_ReadsAdminOnly_AsALiteralTrue(string json, bool expected)
        => AppDirectory.ReadRoot(Root("Feedback", json), Options)!.AdminOnly.Should().Be(expected);

    [Fact]
    public void AnAdminOnlyApp_IsATileForAGlobalAdmin_AndForNobodyElse()
    {
        var feedback = AppDirectory.ReadRoot(Root("Feedback", """{"app":true,"adminOnly":true}"""), Options)!;
        var crm = AppDirectory.ReadRoot(Root("Crm", """{"app":true}"""), Options)!;

        AppDirectory.Compose("alice", [feedback, crm], isGlobalAdmin: false)
            .Select(e => e.Id).Should().Contain("Crm").And.NotContain("Feedback");
        AppDirectory.Compose("bob", [feedback, crm], isGlobalAdmin: true)
            .Select(e => e.Id).Should().Contain("Crm").And.Contain("Feedback");
    }

    /// <summary>A package declares <c>launcherGroup: ""</c> to sit TOP-LEVEL (the Inbox's shape) and
    /// <c>launcherGroupOrder</c> to sort its group; both are read off the root content, shape-tolerantly.</summary>
    [Fact]
    public void AnAppRoot_DeclaresATopLevelTile_AndAGroupOrder_AsData()
    {
        var threads = AppDirectory.ReadRoot(
            new MeshNode("AI") { Name = "Threads", Category = "AI & learning", Content = System.Text.Json.JsonDocument.Parse(
                """{"app":true,"entryPoint":"AI/AiThreads","launcherGroup":""}""").RootElement.Clone() }, Options)!;
        var crm = AppDirectory.ReadRoot(
            new MeshNode("Crm") { Name = "CRM", Category = "Business", Content = System.Text.Json.JsonDocument.Parse(
                """{"app":true,"launcherGroupOrder":7}""").RootElement.Clone() }, Options)!;

        threads.LauncherGroup.Should().Be("", "an empty group is a real value: top-level, in no folder");
        crm.LauncherGroup.Should().BeNull("not declared → the store category applies");
        crm.LauncherGroupOrder.Should().Be(7);

        var entries = AppDirectory.Compose("alice", [threads, crm], isGlobalAdmin: false);
        entries.Single(e => e.Id == "AI").Category.Should().Be("");
        entries.Single(e => e.Id == "AI").GroupDeclared.Should().BeTrue();
        entries.Single(e => e.Id == "Crm").Category.Should().Be("Business");
        entries.Single(e => e.Id == "Crm").GroupDeclared.Should().BeFalse();
    }

    /// <summary>Settings and Administration share ONE group, sorted last; the Inbox stays top-level.</summary>
    [Fact]
    public void TheSettingsTiles_ShareOneLastGroup_TheInboxStaysTopLevel()
    {
        var entries = AppDirectory.BuiltIns("alice", isGlobalAdmin: true);
        entries.Where(e => e.Id is AppDirectory.SettingsId or AppDirectory.AdminId)
            .Should().OnlyContain(e => e.Category == AppDirectory.SettingsGroup
                                       && e.GroupDeclared && e.GroupOrder == AppDirectory.LastGroupOrder
                                       && e.GroupLabelKey == "common.settings");
        entries.Single(e => e.Id == AppDirectory.InboxId).Category.Should().BeNull();
        entries.Single(e => e.Id == AppDirectory.InboxId).GroupOrder.Should().Be(0);
    }

    /// <summary>A DECLARED group beats the legacy seed's non-custom copy, never the viewer's own choice.</summary>
    [Fact]
    public void ADeclaredGroup_BeatsTheSeedCopy_NeverTheViewersChoice()
    {
        var declared = new AppDirectoryEntry("AI", "Threads", "t.svg", null, "", "AI/AiThreads") { GroupDeclared = true, GroupOrder = 5 };
        var seeded = new LauncherArrangement
        {
            Entries = ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, new[]
            {
                KeyValuePair.Create("AI", new LauncherEntry { Group = "AI", Order = 2 }),
            }),
        };
        var seededRow = AppDirectoryQueryProvider.Rows("alice", [declared], seeded, Options)[0].ContentAs<App>(Options)!;
        seededRow.Group.Should().Be("", "the seed's copy of an old category yields to the declared group");
        seededRow.GroupOrder.Should().Be(5);
        var labelled = declared with { GroupLabelKey = "common.settings" };
        AppDirectoryQueryProvider.Rows("alice", [labelled], seeded, Options)[0].ContentAs<App>(Options)!
            .GroupLabelKey.Should().Be("common.settings", "the heading is localized by key; Group stays the identity");

        var chosen = new LauncherArrangement().Place("AI", "Mine", 1);
        var chosenRow = AppDirectoryQueryProvider.Rows("alice", [declared], chosen, Options)[0].ContentAs<App>(Options)!;
        chosenRow.Group.Should().Be("Mine", "a group the viewer chose always wins");
        chosenRow.GroupOrder.Should().Be(0, "the declared sort preference belongs to the declared group");
        AppDirectoryQueryProvider.Rows("alice", [labelled], chosen, Options)[0].ContentAs<App>(Options)!
            .GroupLabelKey.Should().BeNull("a viewer-authored group name is shown as written");
    }

    /// <summary>The snapshot fold behind the provider's unified surface applies EVERY delta kind —
    /// a Removed path leaves the snapshot (the interface's default bridge drops Removed).</summary>
    [Fact]
    public void TheSnapshotFold_AppliesRemovals()
    {
        var parsed = new QueryParser().Parse("namespace:alice/_Apps");
        MeshNode Row(string id) => new(id, "alice/_Apps") { NodeType = AppNodeType.NodeType };
        var folded = new List<ImmutableDictionary<string, MeshNode>>();
        LauncherArrangementSource.Fold(new[]
        {
            new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial, Items = [Row("A"), Row("B")], Query = parsed },
            new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Removed, Items = [Row("A")], Query = parsed },
        }.ToObservable()).Subscribe(folded.Add);
        folded.Last().Keys.Should().Equal("alice/_Apps/B");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Records", true)]
    [InlineData("directory", true)]
    [InlineData("bogus", false)]
    public void UnknownAppSources_AreReportable(string? value, bool known)
        => HomeAppSource.IsKnown(value).Should().Be(known);

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

    // ── Installed, and not a section of another app ──────────────────────────────────────────

    private static InstallEvidence Evidence(string[] records, string[]? synced = null, string[]? uninstalled = null) =>
        new(Set(records), Set(synced ?? []), Set(uninstalled ?? []));

    // The directory reads every set case-insensitively (RecordIds / PartitionsOf / UninstalledPackages).
    private static ImmutableHashSet<string> Set(string[] ids) => ids.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ARoot_CarriesItsHostedIn()
    {
        AppDirectory.ReadRoot(Root("Signature", """{"app":true,"hostedIn":"{user}/Settings"}"""), Options)!
            .HostedIn.Should().Be("{user}/Settings");
        AppDirectory.ReadRoot(Root("Crm", """{"app":true}"""), Options)!.HostedIn.Should().BeNull();
    }

    [Theory]
    // record present → installed, whatever else is said
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    // a git-provisioned package writes no record: its sync source is the evidence…
    [InlineData(false, true, false, true)]
    // …until an uninstall request names it (phase 1 removed the record, the data and sync stayed)
    [InlineData(false, true, true, false)]
    // uninstalled with its data retained, never git-provisioned: nothing says installed
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    public void Installed_IsTheRecord_OrAGitSyncNoUninstallNames(bool record, bool synced, bool uninstalled, bool expected)
    {
        var evidence = Evidence(record ? ["Crm"] : [], synced ? ["Crm"] : [], uninstalled ? ["Crm"] : []);
        evidence.IsInstalled("Crm").Should().Be(expected);
        evidence.IsInstalled("crm").Should().Be(expected, "package ids are case-insensitive, like paths");
    }

    /// <summary>
    /// The DEFAULT APPS (Threads = package <c>AI</c>, Examples = package <c>Developer</c>) as their
    /// roots are authored — <c>tier: free</c>, <c>defaultApp</c>, <c>app</c>. Once the package is
    /// installed, they are tiles for ANY viewer beside the built-ins Inbox and Settings, and a plan is
    /// never consulted: the launcher decides on the install record and the viewer's read grant alone
    /// (a free-plan viewer has both for a free, pre-installed package).
    /// </summary>
    [Fact]
    public void TheDefaultApps_AreTilesForAnyViewerOnceInstalled_BesideInboxAndSettings()
    {
        var threads = AppDirectory.ReadRoot(Root("AI",
            """{"app":true,"entryPoint":"AI/AiThreads","tier":"free","preInstalled":true,"defaultApp":true}"""), Options)!;
        var examples = AppDirectory.ReadRoot(Root("Developer",
            """{"app":true,"entryPoint":"Developer/Home","tier":"free","preInstalled":true,"defaultApp":true}"""), Options)!;
        var evidence = Evidence(["AI", "Developer"]);

        AppDirectory.IsTile(threads, evidence).Should().BeTrue();
        AppDirectory.IsTile(examples, evidence).Should().BeTrue();
        AppDirectory.IsTile(threads, Evidence([])).Should().BeFalse("not installed, no tile");

        var list = AppDirectory.Compose("rbuergi", [threads, examples], isGlobalAdmin: false);
        list.Select(e => e.Id).Should().Contain(["Inbox", "Settings", "AI", "Developer"]);
        list.Select(e => e.Id).Should().NotContain("Admin", "a plain viewer has no Administration");
    }

    [Fact]
    public void AnUninstalledApp_IsNoTile_EvenWithItsRootAndAppFlagLeft()
    {
        var parties = AppDirectory.ReadRoot(Root("Parties", """{"app":true}"""), Options)!;
        AppDirectory.IsTile(parties, Evidence(["Crm"])).Should().BeFalse(
            "phase 1 of an uninstall removes the record and keeps the root — the tile must go with the record");
        AppDirectory.IsTile(parties, Evidence(["Parties"])).Should().BeTrue();
    }

    [Theory]
    [InlineData("Developer/Home", false)]
    [InlineData("Games/Home", false)]
    [InlineData("AI/AiThreads", false)]
    // a settings tab of the person app may still belong to an app (Signature)
    [InlineData("{user}/Settings", true)]
    [InlineData("{USER}/settings", true)]
    public void AHostedPackage_IsNoTile_UnlessItsHostIsThePersonApp(string host, bool tile)
    {
        var root = AppDirectory.ReadRoot(Root("Pack", $$"""{"app":true,"hostedIn":"{{host}}"}"""), Options)!;
        AppDirectory.IsTile(root, Evidence(["Pack"])).Should().Be(tile);
    }

    [Fact]
    public void TheEvidence_IsReadOffTheRecordsSyncSourcesAndUninstallRequests()
    {
        AppDirectory.RecordIds([
            new MeshNode("Crm", AppDirectory.InstallRecordsPartition) { NodeType = "Package" },
            new MeshNode("_DefaultInstallLedger", "Elsewhere"),
        ]).OrderBy(x => x, StringComparer.Ordinal).Should().Equal(["Crm"], "only a node IN the records partition is a record");

        // A record whose package installs into ANOTHER partition is keyed by that partition — the root's path.
        AppDirectory.RecordIds([
            new MeshNode("CrmPackage", AppDirectory.InstallRecordsPartition)
            {
                NodeType = "Package",
                Content = JsonDocument.Parse("""{"id":"CrmPackage","targetPartition":"Crm"}""").RootElement,
            },
        ], Options).OrderBy(x => x, StringComparer.Ordinal).Should().Equal(["Crm"]);

        AppDirectory.PartitionsOf([
            MeshNode.FromPath("Reinsurance/_GitSync"),
            MeshNode.FromPath("Manufacturing/_GitSync/main"),
        ]).OrderBy(x => x, StringComparer.Ordinal).Should().Equal("Manufacturing", "Reinsurance");

        AppDirectory.UninstalledPackages([
            new MeshNode("r1", "Admin/_PackageUninstall") { Content = JsonDocument.Parse("""{"package":"Parties","status":"AwaitingConfirmation"}""").RootElement },
            new MeshNode("r2", "Admin/_PackageUninstall") { Content = new System.Text.Json.Nodes.JsonObject { ["Package"] = "DeepSign", ["Status"] = "Done" } },
            new MeshNode("r3", "Admin/_PackageUninstall") { Content = new PackageUninstallRequest { Package = "Crm", Status = PackageUninstallStatus.TearingDown } },
            new MeshNode("r4", "Admin/_PackageUninstall") { Content = JsonDocument.Parse("""{"status":"Done"}""").RootElement },
            // the partitions a completed request recorded count too — roots are keyed by partition
            new MeshNode("r5", "Admin/_PackageUninstall") { Content = JsonDocument.Parse("""{"package":"Pkg","status":"Done","partitions":[{"partition":"PkgSpace"}]}""").RootElement },
        ], Options).OrderBy(x => x, StringComparer.Ordinal).Should().Equal(["Crm", "DeepSign", "Parties", "Pkg", "PkgSpace"], "content is read in whatever shape it arrives");
    }

    [Theory]
    // phase 1 has not removed anything yet…
    [InlineData("Requested")]
    [InlineData("Uninstalling")]
    // …or refused (a git-provisioned package has no record, a shared partition, user data) and removed nothing
    [InlineData("Failed")]
    [InlineData(null)]
    public void ARequestThatDidNotCompletePhaseOne_UninstallsNothing(string? status)
    {
        var json = status is null ? """{"package":"Crm"}""" : $$"""{"package":"Crm","status":"{{status}}"}""";
        var uninstalled = AppDirectory.UninstalledPackages(
            [new MeshNode("r1", "Admin/_PackageUninstall") { Content = JsonDocument.Parse(json).RootElement }], Options);
        uninstalled.Should().BeEmpty("only AwaitingConfirmation, TearingDown and Done say the package is gone");
        new InstallEvidence([], ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "Crm"), uninstalled)
            .IsInstalled("Crm").Should().BeTrue("a refused uninstall leaves a git-provisioned app installed — and its tile");
    }

    [Fact]
    public void TheEvidenceQueries_AreAnchoredOrDeclared_AndComplete()
    {
        var parser = new QueryParser();
        AppDirectory.InstallRecordsQuery.Should().Contain("path:Plugins scope:children nodeType:Package");
        AppDirectory.UninstallRequestsQuery.Should().Contain("namespace:Admin/_PackageUninstall");
        parser.Parse(AppDirectory.SyncSourcesQuery).CrossPartition.Should().BeTrue("sync sources live in every partition");
        foreach (var query in new[] { AppDirectory.InstallRecordsQuery, AppDirectory.SyncSourcesQuery, AppDirectory.UninstallRequestsQuery })
            query.Should().Contain(MeshQueryRequest.CompleteQualifier, "an enumeration — a truncated page would drop an installed app");
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

    /// <summary>With an explicit <c>Records</c> the band's query is EXACTLY what it was.</summary>
    [Theory]
    [InlineData("Records")]
    [InlineData(" records ")]
    public void RecordsSource_KeepsTheBandUnchanged(string? configured)
    {
        HomeAppSource.IsDirectory(configured).Should().BeFalse();
        var band = UserActivityLayoutAreas.BuildAppsBand("alice", null, fromDirectory: HomeAppSource.IsDirectory(configured));
        band.HiddenQuery.Should().Be("path:alice/_App scope:children nodeType:InstalledApp sort:Name-asc");
        band.ScopeTabs![0].Query.Should().Be((string?)band.HiddenQuery);
    }

    /// <summary>The directory is the DEFAULT: absent, empty, Directory and an unknown value all
    /// select it — only an explicit Records keeps the legacy band (MeshWeaver#6448: the
    /// record-driven fleet renders no Home__AppSource, so the image default is what it runs).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Directory")]
    [InlineData(" directory ")]
    [InlineData("bogus")]
    public void DirectoryIsTheDefault(string? configured)
        => HomeAppSource.IsDirectory(configured).Should().BeTrue();

    [Fact]
    public void DirectorySource_ReadsTheVirtualNamespace_WithoutTheHiddenApps()
    {
        var band = UserActivityLayoutAreas.BuildAppsBand("alice", null, fromDirectory: true);
        band.HiddenQuery.Should().Be("path:alice/_Apps scope:children nodeType:InstalledApp -content.hidden:true sort:Name-asc");
        band.ScopeTabs![0].Query.Should().Be((string?)band.HiddenQuery);
        band.ScopeTabs[0].Sortable.Should().BeTrue("drag and drop stays on: the view routes it to the arrangement");
    }

    /// <summary>The directory band offers a third scope — the apps the viewer hid — so a hidden
    /// tile is always reachable to be shown again; the Records band has no such scope.</summary>
    [Fact]
    public void DirectorySource_OffersTheHiddenScope_RecordsDoesNot()
    {
        var band = UserActivityLayoutAreas.BuildAppsBand("alice", null, fromDirectory: true);
        band.ScopeTabs.Should().HaveCount(3, "apps, spaces, hidden");
        var hidden = band.ScopeTabs![2];
        hidden.Label.Should().Be("Hidden");
        hidden.Query.Should().Be("path:alice/_Apps scope:children nodeType:InstalledApp content.hidden:true sort:Name-asc");
        hidden.Sortable.Should().BeFalse("a hidden tile has no place on the grid to drag to");
        hidden.NavigateToMainNode.Should().BeTrue("a hidden app still opens");

        UserActivityLayoutAreas.BuildAppsBand("alice", null, fromDirectory: false)
            .ScopeTabs.Should().HaveCount(2, "the legacy records band is unchanged");
        UserActivityLayoutAreas.BuildAppsBand("alice", "de", fromDirectory: true)
            .ScopeTabs![2].Label.Should().Be("Ausgeblendet");
    }

    // ── Hide ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnArrangementWithoutTheFlag_HidesNothing()
    {
        var arrangement = JsonSerializer.Deserialize<LauncherArrangement>(
            """{"entries":{"Crm":{"group":"Mine","order":2,"customGroup":true}}}""", Options)!;
        arrangement.For("Crm")!.Hidden.Should().BeFalse("an arrangement written before the flag existed shows every tile");
        arrangement.IsHidden("Crm").Should().BeFalse();
        arrangement.IsHidden("NeverPlaced").Should().BeFalse();
    }

    [Fact]
    public void Hiding_KeepsThePlacement_AndShowingPutsTheTileBack()
    {
        var placed = new LauncherArrangement().Place("Crm", "Mine", 4);
        var hidden = placed.SetHidden("Crm", true);
        hidden.IsHidden("Crm").Should().BeTrue();
        hidden.For("Crm").Should().Be(new LauncherEntry { Group = "Mine", Order = 4, CustomGroup = true, Hidden = true });

        var shown = hidden.SetHidden("Crm", false);
        shown.For("Crm").Should().Be(placed.For("Crm"), "showing again restores exactly the placement it had");
    }

    [Fact]
    public void HidingAnUnplacedApp_CarriesOnlyTheFlag()
    {
        var hidden = new LauncherArrangement().SetHidden("chess", true);
        hidden.IsHidden("Chess").Should().BeTrue("app ids are case-insensitive, like every placement");
        hidden.For("Chess").Should().Be(new LauncherEntry { Hidden = true },
            "no group (the category still applies), no order, no custom-group claim");
    }

    [Fact]
    public void MovingAHiddenTile_KeepsItHidden()
    {
        var moved = new LauncherArrangement().SetHidden("Crm", true).Place("Crm", "Elsewhere", 1);
        moved.For("Crm").Should().Be(new LauncherEntry { Group = "Elsewhere", Order = 1, CustomGroup = true, Hidden = true });
    }

    [Fact]
    public void Rows_CarryTheHiddenFlag_AndTheQueriesSplitOnIt()
    {
        var entries = ImmutableList.Create(
            new AppDirectoryEntry("Crm", "CRM", "c.svg", null, "Business", "Crm"),
            new AppDirectoryEntry("Chess", "Chess", "k.svg", null, "Games", "Chess"));
        var rows = AppDirectoryQueryProvider.Rows("alice", entries,
            new LauncherArrangement().Place("Crm", "Mine", 2).SetHidden("Crm", true), Options);
        var crm = rows[0].ContentAs<App>(Options)!;
        crm.Hidden.Should().BeTrue();
        crm.Group.Should().Be("Mine", "a hidden tile keeps its place for when it is shown again");
        rows[1].ContentAs<App>(Options)!.Hidden.Should().BeFalse();
        AppDirectoryQueryProvider.Rows("alice", entries, null, Options)
            .Should().OnlyContain(r => !r.ContentAs<App>(Options)!.Hidden, "no arrangement hides nothing");

        // The two band queries, evaluated by the predicate the provider applies.
        var parser = new QueryParser();
        var evaluator = new MeshWeaver.Hosting.Persistence.Query.QueryEvaluator();
        var shown = parser.Parse(UserActivityLayoutAreas.AppsQuery("alice", fromDirectory: true));
        var hiddenOnly = parser.Parse(UserActivityLayoutAreas.HiddenAppsQuery("alice"));
        rows.Where(r => evaluator.Matches(r, shown)).Select(r => r.Id).Should().Equal("Chess");
        rows.Where(r => evaluator.Matches(r, hiddenOnly)).Select(r => r.Id).Should().Equal("Crm");
    }

    [Fact]
    public void SetHidden_RefusesAPathThatIsNoDirectoryRow()
    {
        Exception? error = null;
        using var _ = LauncherArrangementPaths.SetHidden(null!, "alice/_App/Crm", true, Options)
            .Subscribe(_ => { }, e => error = e);
        error.Should().BeOfType<ArgumentException>().Which.Message.Should().Contain("not an app directory row");
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
    private const string Named = "dir-named";

    /// <summary>A package root's content as a package carries it: its OWN typed record (the Store's
    /// <c>PluginContent</c> in production), with <c>app</c> and <c>entryPoint</c>.</summary>
    public sealed record TestPackageContent
    {
        /// <summary>Whether the package is an app.</summary>
        public bool App { get; init; }
        /// <summary>Where the app opens.</summary>
        public string? EntryPoint { get; init; }
        /// <summary>The host app the package is a section of, when it is one.</summary>
        public string? HostedIn { get; init; }
    }


    // A package-uninstall request as the platform writes it.
    private static MeshNode UninstallRequest(string package, string status) =>
        new($"20261013-{package}", PackageUninstallRequest.Namespace)
        {
            NodeType = PackageUninstallRequest.NodeType,
            State = MeshNodeState.Active,
            Content = new PackageUninstallRequest { Package = package, Status = status },
        };

    // The install record the platform's installer writes for an installed package.
    private static MeshNode InstallRecord(string id) => new(id, AppDirectory.InstallRecordsPartition)
    {
        NodeType = "Package",
        Name = id,
        State = MeshNodeState.Active,
    };

    // A package root as a mirror may index it: a Space whose typed content says app.
    private static MeshNode AppRootNode(string id, bool app, string? hostedIn = null) => new(id)
    {
        NodeType = "Space",
        Name = id,
        Category = "Business",
        State = MeshNodeState.Active,
        Content = new TestPackageContent { App = app, EntryPoint = $"{id}/Home", HostedIn = hostedIn },
    };

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureHub(config => config
                .WithType<TestPackageContent>(nameof(TestPackageContent))
                // The uninstall request's content type, as the PluginCatalog registers it in production —
                // without it the request nodes read as untyped content (the untyped-content CI gate).
                .WithType<PackageUninstallRequest>(nameof(PackageUninstallRequest)))
            .AddMeshNodes(
            AppRootNode("DirApp", app: true),
            InstallRecord("DirApp"),
            InstallRecord("DirNotAnApp"),
            InstallRecord("DirEntryOnly"),
            // Uninstalled with its data retained (phase 1): root and app flag left, no record.
            AppRootNode("DirUninstalled", app: true),
            AssignmentNodeFactory.UserRole(Granted, "Viewer", "DirUninstalled"),
            // A section of another app: installed and readable, still no tile.
            AppRootNode("DirHosted", app: true, hostedIn: "Developer/Home"),
            InstallRecord("DirHosted"),
            AssignmentNodeFactory.UserRole(Granted, "Viewer", "DirHosted"),
            // Provisioned by a git import (no record), then uninstalled: the sync source stays.
            AppRootNode("DirRetired", app: true),
            MeshNode.FromPath("DirRetired/_GitSync") with { NodeType = "GitHubSyncConfig", State = MeshNodeState.Active },
            UninstallRequest("DirRetired", PackageUninstallStatus.AwaitingConfirmation),
            AssignmentNodeFactory.UserRole(Granted, "Viewer", "DirRetired"),
            // Provisioned by a git import, and its uninstall was REFUSED: still installed, still a tile.
            AppRootNode("DirRefused", app: true),
            MeshNode.FromPath("DirRefused/_GitSync") with { NodeType = "GitHubSyncConfig", State = MeshNodeState.Active },
            UninstallRequest("DirRefused", PackageUninstallStatus.Failed),
            AssignmentNodeFactory.UserRole(Granted, "Viewer", "DirRefused"),
            // A person with a display name: their Settings tile carries it.
            new MeshNode(Named) { NodeType = "User", Name = "Max Muster", State = MeshNodeState.Active },
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
    [InlineData(Granted, new[] { "Settings", "Inbox", "DirApp", "DirEntryOnly", "DirRefused" })]
    [InlineData(Ungranted, new[] { "Settings", "Inbox" })]
    [InlineData(Admin, new[] { "Settings", "Inbox", "Admin" })]
    public async Task Visibility_IsAppFlagAndReadOnTheEntryPoint_PlusBuiltIns(string viewer, string[] expected)
    {
        var ct = TestContext.Current.CancellationToken;
        // Wait until the shared roots query has seen every seeded package, then read the FIRST
        // computation over it — never a count that a transient partial list could satisfy.
        await Directory.Roots()
            .Where(roots => roots.Any(r => r.Path == "DirApp") && roots.Any(r => r.Path == "DirEntryOnly")
                            && roots.Any(r => r.Path == "DirRefused"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        // …and the install evidence has seen every seeded record, sync source and request.
        await Directory.Installed()
            .Where(e => e.Records.Contains("DirEntryOnly") && e.Synced.Contains("DirRefused") && e.Uninstalled.Contains("DirRetired"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var list = await Directory.Compute(viewer).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        list.Select(e => e.Id).Should().Equal(expected);
        list.Should().NotContain(e => e.Id == "DirNotAnApp", "a package without app:true is no tile, whatever the grant");
    }

    /// <summary>
    /// The installed filter on a real mesh: the granted viewer can READ the uninstalled, the hosted
    /// and the git-provisioned-then-uninstalled packages, and gets no tile for any of them — the
    /// tile follows the install evidence, not the leftover root.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task UninstalledAndHostedPackages_GetNoTile_ThoughTheViewerCanReadThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await Directory.Roots()
            .Where(roots => new[] { "DirUninstalled", "DirHosted", "DirRetired" }.All(id => roots.Any(r => r.Path == id)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var installed = await Directory.Installed()
            .Where(e => e.Records.Contains("DirHosted") && e.Synced.Contains("DirRetired") && e.Synced.Contains("DirRefused")
                        && e.Uninstalled.Contains("DirRetired"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        installed.IsInstalled("DirUninstalled").Should().BeFalse();
        installed.IsInstalled("DirRetired").Should().BeFalse();
        installed.IsInstalled("DirRefused").Should().BeTrue("a refused uninstall removed nothing");

        var list = await Directory.Compute(Granted).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        list.Select(e => e.Id).Should().NotContain(["DirUninstalled", "DirHosted", "DirRetired"]);
        list.Select(e => e.Id).Should().Contain(["DirApp", "DirRefused"]);
    }

    [Fact(Timeout = 60000)]
    public async Task TheSettingsTile_BecomesTheViewersName_OnceTheirNodeIsRead()
    {
        _ = TestContext.Current.CancellationToken; // Settled waits on it
        var list = await Settled(Directory.Compute(Named),
            l => l.Count > 0 && l[0].Name == "Max Muster");
        list[0].Id.Should().Be(AppDirectory.SettingsId);
        list[0].LabelKey.Should().BeNull();
        list[0].Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact(Timeout = 60000)]
    public async Task AnAbsentOwner_KeepsThePlainSettingsTile()
    {
        var ct = TestContext.Current.CancellationToken;
        const string ghost = "dir-no-such-owner";
        var list = await Directory.Compute(ghost).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        list[0].Id.Should().Be(AppDirectory.SettingsId);
        list[0].LabelKey.Should().Be("common.settings");
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

        // The FULL predicate applies, like on any stored namespace: a content filter narrows the rows.
        var narrowed = await Live(access.RunAs(owner, () => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{Seeded}/_Apps content.group:Elsewhere"))))
            .Where(items => items.Count > 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        narrowed.Select(r => r.Id).Should().Equal("Inbox");

        // Someone else asking for the owner's directory gets nothing.
        var intruder = new AccessContext { ObjectId = Ungranted, Name = Ungranted };
        var foreign = await access.RunAs(intruder, () => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        foreign.Items.Should().BeEmpty("a viewer's directory is served to that viewer only");
    }

    /// <summary>
    /// Hide and show again, end to end: the flag is written onto the OWNER's arrangement node, the
    /// apps scope drops the tile and the Hidden scope lists it, the placement survives, and the
    /// viewer's ACCESS is untouched throughout — the directory (which is the access check) still
    /// lists the app while it is hidden.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task HidingATile_MovesItToTheHiddenScope_AndShowingBringsItBack_WithoutTouchingAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        const string hider = "dir-hider";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var options = Mesh.JsonSerializerOptions;
        await CreateUserAsync(hider, ct);
        await access.RunAsSystem(() => mesh.CreateNode(AssignmentNodeFactory.UserRole(hider, "Viewer", "DirApp")))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var owner = new AccessContext { ObjectId = hider, Name = hider };
        IObservable<List<MeshNode>> Rows(string query) =>
            Live(access.RunAs(owner, () => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))));
        var shownQuery = UserActivityLayoutAreas.AppsQuery(hider, fromDirectory: true);
        var hiddenQuery = UserActivityLayoutAreas.HiddenAppsQuery(hider);
        var row = $"{hider}/_Apps/DirApp";

        // Visible, and seeded (the rows wait for the arrangement node).
        await Rows(shownQuery).Where(items => items.Any(r => r.Path == row))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await access.RunAs(owner, () => LauncherArrangementPaths.Place(cache, row, "Mine", 3, options))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        // Hide.
        await access.RunAs(owner, () => LauncherArrangementPaths.SetHidden(cache, row, true, options))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await Rows(shownQuery).Where(items => items.Count > 0 && items.All(r => r.Path != row))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var hiddenRows = await Rows(hiddenQuery).Where(items => items.Any(r => r.Path == row))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        hiddenRows.Select(r => r.Path).Should().Equal(row);
        var arrangement = await Mesh.GetWorkspace().GetMeshNodeStream(LauncherArrangementPaths.PathFor(hider))
            .Select(n => n?.ContentAs<LauncherArrangement>(options))
            .Where(a => a?.IsHidden("DirApp") == true)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        arrangement!.For("DirApp")!.Group.Should().Be("Mine", "hiding keeps the placement");

        // Access is untouched: the directory (the access check itself) still lists the app.
        (await Directory.Compute(hider).Where(l => l.Any(e => e.Id == "DirApp"))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Contain(e => e.Id == "DirApp");

        // Show again: back in the apps scope, in its old group, gone from the Hidden scope.
        await access.RunAs(owner, () => LauncherArrangementPaths.SetHidden(cache, row, false, options))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var back = await Rows(shownQuery).Where(items => items.Any(r => r.Path == row))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        back.Single(r => r.Path == row).ContentAs<App>(options)!.Group.Should().Be("Mine");
        await Rows(hiddenQuery).Where(items => items.All(r => r.Path != row))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>
    /// The System channel (a process-wide synced query, whose real caller is checked only
    /// downstream) is served SIDE-EFFECT FREE: it gets the rows but never seeds the owner's
    /// arrangement — a foreign viewer must not be able to write into someone's home through it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheSystemChannel_ServesRows_ButNeverWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        const string target = "dir-system-target";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await CreateUserAsync(target, ct);

        var rows = await Live(access.RunAsSystem(() => mesh.Query<MeshNode>(
                MeshQueryRequest.FromQuery(UserActivityLayoutAreas.AppsQuery(target, fromDirectory: true)))))
            .Where(items => items.Any(r => r.Id == AppDirectory.SettingsId))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        rows.Should().NotBeEmpty();

        var source = Mesh.ServiceProvider.GetRequiredService<LauncherArrangementSource>();
        source.IsSeeded(target).Should().BeFalse("the System channel never seeds");
        var settings = await access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                LauncherArrangementSource.ArrangementQuery(target))))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        settings.Items.Should().BeEmpty("no arrangement was written for the target");
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
