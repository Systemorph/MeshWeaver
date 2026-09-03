using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The plugin catalog opens on its categories and loads packages per category</b> (maintainer,
/// 2026-09-03: "the store must not load the full thing — only categories first").
///
/// <para>Pinned on the REAL render path — a mesh node whose catalog area is
/// <see cref="CatalogLayoutAreas.RenderFromSource"/> over a stub <see cref="IPackageSource"/>,
/// rendered through a client workspace exactly as the portal renders it — plus the pure seams the
/// composition branches on. What is asserted:</para>
/// <list type="number">
///   <item>the LANDING renders one tile per category, with counts, off ONE listing of the source and
///     renders no package card — while an install record exists on the mesh, so a landing that
///     joined the registry would have had something to show;</item>
///   <item>a CATEGORY page renders exactly that category's cards, and the card whose package is
///     installed reads as installed — the join ran, for the members and nothing else;</item>
///   <item>an EMPTY catalog renders the localized empty state, not a blank page and not a
///     progress bar that never ends.</item>
/// </list>
/// </summary>
public class CatalogCategoryFirstTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CatalogNode = "CategoryFirstCatalog";
    private const string StubType = "StubPluginCatalog";

    private static TimeSpan RenderBudget => TestTimeouts.Convergence;

    /// <summary>
    /// 🚨 <b>Assert on ASCII-only fragments of a rendered label</b> — the frame travels as JSON, and
    /// <c>System.Text.Json</c> escapes non-ASCII, so the card's <c>✓ Installed v1.0.0</c> appears in
    /// <c>frame.Value.ToString()</c> as <c>✓ Installed v1.0.0</c> and the back link's <c>←</c>
    /// as <c>←</c>. Waiting on the label verbatim is a predicate that can NEVER become true,
    /// and it fails as a 36-second TIMEOUT rather than an assertion — indistinguishable from a view
    /// that does not render. (The same rule is why the Plugins repo's own catalog test picks a
    /// fragment "that reads identically in the rendered HTML and in the JSON the store travels as".)
    /// </summary>
    private const string InstalledFragment = "Installed v1.0.0";

    /// <summary>The ASCII half of <c>ui.catalogBackToCategories</c> — see <see cref="InstalledFragment"/>.</summary>
    private const string BackFragment = "All categories";

    private readonly StubSource source = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .AddMeshNodes(
                // The same registration shape the real PluginCatalog type uses, over a source the
                // test controls — everything below RenderFromSource is the production pipeline.
                new MeshNode(StubType)
                {
                    Name = "Stub Plugin Catalog",
                    HubConfiguration = config => config
                        .AddDefaultLayoutAreas()
                        .AddLayout(layout => layout.WithView(
                            CatalogLayoutAreas.CatalogArea,
                            (host, _) => CatalogLayoutAreas.RenderFromSource(host, source, "HEAD", null, "stub"))),
                },
                new MeshNode(CatalogNode) { Name = "Catalog", NodeType = StubType });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    // ————————————————————————————————————————————— the pure seams

    [Fact]
    public void Categories_GroupCaseInsensitively_Alphabetically_UncategorizedLast()
    {
        var categories = CatalogLayoutAreas.Categories(
        [
            Package("b1", "Insurance"), Package("a1", "Education"), Package("a2", "education "),
            Package("u1", null), Package("u2", "  "),
        ]);

        categories.Select(c => (c.Key, c.Count)).Should().Equal(
            ("Education", 2), ("Insurance", 1), (CatalogLayoutAreas.Uncategorized, 2));
    }

    [Fact]
    public void Plan_LandingReadsNoRecord_CategoryReadsOnlyItsMembers_AllReadsEverything()
    {
        IReadOnlyList<PackageManifest> available =
            [Package("b1", "Insurance"), Package("a1", "Education"), Package("a2", "Education")];

        var landing = CatalogLayoutAreas.Plan(null, null, available);
        landing.Kind.Should().Be(CatalogLayoutAreas.CatalogPage.Landing);
        landing.Packages.Should().BeEmpty("the landing composes no install-record read at all");
        landing.Categories.Select(c => c.Key).Should().Equal("Education", "Insurance");
        landing.Total.Should().Be(3);

        // A stale or mistyped category falls back to the tiles, never a blank page.
        CatalogLayoutAreas.Plan("NoSuch", null, available).Kind
            .Should().Be(CatalogLayoutAreas.CatalogPage.Landing);

        var category = CatalogLayoutAreas.Plan("education", null, available);
        category.Kind.Should().Be(CatalogLayoutAreas.CatalogPage.Category);
        category.Category.Should().Be("Education", "matched case-insensitively to the source's spelling");
        category.Packages.Select(p => p.Id).Should().Equal("a1", "a2");
        category.Available.Should().HaveCount(3, "a click still resolves dependencies against the whole listing");

        var all = CatalogLayoutAreas.Plan("Education", "true", available);
        all.Kind.Should().Be(CatalogLayoutAreas.CatalogPage.All);
        all.Packages.Should().HaveCount(3);
        CatalogLayoutAreas.Plan(null, "yes", available).Kind
            .Should().Be(CatalogLayoutAreas.CatalogPage.Landing, "only a true-ish value asks for the flat list");
    }

    [Fact]
    public void InstalledRecordQueries_AreOneExactPathReadPerMember()
    {
        CatalogLayoutAreas.InstalledRecordQueries(["b1", " a1 ", "", "b1"]).Should().Equal(
            "path:Plugins/a1 nodeType:Package",
            "path:Plugins/b1 nodeType:Package");
        CatalogLayoutAreas.InstalledRecordQueries([]).Should().BeEmpty();
        CatalogLayoutAreas.AllInstalledQuery.Should().Be("path:Plugins scope:children nodeType:Package");
        CatalogLayoutAreas.InstalledIdsQuery.Should().StartWith(CatalogLayoutAreas.AllInstalledQuery)
            .And.Contain("select:")
            .And.NotContain("content", "the id listing must never load the records' installed-file baselines");
    }

    [Fact]
    public void CategoryHref_CarriesTheEncodedCategory_AndRoundTripsThroughTheReference()
    {
        var href = CatalogLayoutAreas.CategoryHref("Plugins/Catalog", "Maps & Places");
        href.Should().Contain(CatalogLayoutAreas.CatalogArea);
        href.Should().Contain($"{CatalogLayoutAreas.CategoryParam}=Maps%20%26%20Places", href);

        var reference = new LayoutAreaReference(CatalogLayoutAreas.CatalogArea)
        {
            Id = $"{CatalogLayoutAreas.CatalogArea}?{CatalogLayoutAreas.CategoryParam}=Maps%20%26%20Places",
        };
        reference.GetParameterValue(CatalogLayoutAreas.CategoryParam).Should().Be("Maps & Places");

        CatalogLayoutAreas.AllHref("Plugins/Catalog").Should().Contain($"{CatalogLayoutAreas.AllParam}=true");
    }

    [Fact]
    public void EveryLocalizationKeyTheCatalogReads_IsInBothCatalogs()
    {
        string[] keys =
        [
            "ui.pluginCatalog", "ui.mdLoadingCatalog", "ui.mdNoPackages", "ui.catalogAllPackages",
            "ui.catalogUncategorized", "ui.catalogBackToCategories", "ui.catalogNoSource",
            "ui.catalogSourceSummary", "ui.catalogRegistry", "ui.catalogInstall", "ui.catalogUpdateTo",
            "ui.catalogInstalledVersion", "ui.orphanedInstallRecords", "ui.mdOrphanedInstallRecords",
        ];
        foreach (var locale in new[] { "en", "de" })
        {
            foreach (var key in keys)
                LocalizationCatalog.Get(key, locale).Should().NotBe(key,
                    $"'{key}' renders as its raw key to a {locale} viewer when the catalog lacks it");
            LocalizationCatalog.Plural("plural.package", 1, locale).Should().NotContain("plural.package");
            LocalizationCatalog.Plural("plural.package", 2, locale).Should().NotContain("plural.package");
        }
        LocalizationCatalog.Plural("plural.package", 2, "de").Should().Be("2 Pakete");
    }

    // ————————————————————————————————————————————— the real render path

    [Fact(Timeout = 120_000)]
    public async Task Landing_RendersTheCategoriesOffOneListing_AndNoCard()
    {
        source.Packages =
        [
            Package("CfAlpha", "Education", "Alpha Course"),
            Package("CfBeta", "Education", "Beta Course"),
            Package("CfGamma", "Insurance", "Gamma Cover"),
            Package("CfDelta", null, "Delta Tool"),
        ];
        // A real install record on the mesh: a landing that joined the registry would have had a
        // card to flip to "Installed"; the tiles must render without ever asking.
        await Install(source.Packages[2]);

        var frame = await Render(null)
            .Where(f => Leaves(f).Contains("categories"))
            .FirstAsync().Timeout(RenderBudget);

        var leaves = Leaves(frame);
        leaves.Should().Contain(["cat-1", "cat-2", "cat-3", "all"]);
        leaves.Where(l => l.StartsWith("pkg-", StringComparison.Ordinal))
            .Should().BeEmpty("the landing renders tiles, never a package card");
        leaves.Where(l => l.StartsWith("orphan-", StringComparison.Ordinal)).Should().BeEmpty();

        var json = frame.Value.ToString();
        json.Should().Contain("Education").And.Contain("Insurance")
            .And.Contain(LocalizationCatalog.Get("ui.catalogUncategorized", "en"))
            .And.Contain(LocalizationCatalog.Get("ui.catalogAllPackages", "en"));
        json.Should().Contain(LocalizationCatalog.Plural("plural.package", 2, "en"), "Education holds two")
            .And.Contain(LocalizationCatalog.Plural("plural.package", 4, "en"), "the all-packages tile and the source line");
        json.Should().NotContain("Gamma Cover", "no card, so no package name on the landing")
            .And.NotContain(LocalizationCatalog.Get("ui.catalogInstall", "en"));

        source.Listings.Should().BeGreaterThan(0, "the landing IS built from the source's listing");
        source.Fetches.Should().Be(0, "nothing is fetched until somebody clicks Install");
    }

    [Fact(Timeout = 120_000)]
    public async Task Category_RendersExactlyItsCards_JoinedWithItsOwnRecords()
    {
        source.Packages =
        [
            Package("CfAlpha", "Education", "Alpha Course"),
            Package("CfBeta", "Education", "Beta Course"),
            Package("CfGamma", "Insurance", "Gamma Cover"),
        ];
        await Install(source.Packages[2]);

        // Lower-case on purpose: the request is matched to the source's own spelling.
        var insurance = Render($"{CatalogLayoutAreas.CatalogArea}?{CatalogLayoutAreas.CategoryParam}=insurance");
        var frame = await insurance
            .Where(f => f.Value.ToString().Contains(InstalledFragment, StringComparison.Ordinal))
            .FirstAsync().Timeout(RenderBudget);

        Cards(frame).Should().Equal(["pkg-1"], "one member, one card");
        var json = frame.Value.ToString();
        json.Should().Contain("Gamma Cover")
            .And.NotContain("Alpha Course").And.NotContain("Beta Course")
            .And.Contain(BackFragment);
        Leaves(frame).Should().NotContain("categories", "a category page shows cards, not tiles");

        var education = await Render($"{CatalogLayoutAreas.CatalogArea}?{CatalogLayoutAreas.CategoryParam}=Education")
            .Where(f => Cards(f).Count == 2)
            .FirstAsync().Timeout(RenderBudget);
        var educationJson = education.Value.ToString();
        educationJson.Should().Contain("Alpha Course").And.Contain("Beta Course")
            .And.NotContain("Gamma Cover", "the other category's card never renders here")
            .And.Contain(LocalizationCatalog.Get("ui.catalogInstall", "en"), "neither course is installed")
            .And.NotContain(InstalledFragment, "neither course has an install record");
    }

    [Fact(Timeout = 120_000)]
    public async Task EmptyCatalog_RendersTheLocalizedEmptyState()
    {
        source.Packages = [];
        var expected = LocalizationCatalog.Get("ui.mdNoPackages", "en");
        expected.Should().NotBe("ui.mdNoPackages");

        var frame = await Render(null)
            .Where(f => Leaves(f).Contains("empty"))
            .FirstAsync().Timeout(RenderBudget);

        frame.Value.ToString().Should().Contain(expected);
        Leaves(frame).Should().NotContain("categories").And.NotContain("all");
    }

    // ————————————————————————————————————————————— helpers

    private static PackageManifest Package(string id, string? category, string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Category = category,
        Kind = PackageKind.Content,
        TargetPartition = id,
        SourceFolder = id,
        Version = "1.0.0",
    };

    // The real installer writes the record — the same node the catalog joins against in production.
    // Awaited as an observable (never .ToTask(): a Task completed inside the Rx trampoline resumes
    // its awaiter on the signalling thread and changes what the test measures).
    private IObservable<InstallResult> Install(PackageManifest manifest) =>
        PackageInstaller.Install(Mesh, manifest, [new PackageFile($"{manifest.Id}/Doc.md", $"# {manifest.Id}")], "HEAD")
            .FirstAsync()
            .Timeout(RenderBudget);

    private IObservable<ChangeItem<JsonElement>> Render(string? id) =>
        GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(CatalogNode),
            new LayoutAreaReference(CatalogLayoutAreas.CatalogArea) { Id = id });

    /// <summary>
    /// The area names in a frame, reduced to their own last segment, so the assertions read like
    /// the code that named them.
    ///
    /// <para>🚨 <b>An <c>InstanceCollection</c> key rides JSON-ENCODED on the wire</b> — the area
    /// <c>Catalog/categories</c> arrives as the property <c>"\"Catalog/categories\""</c>
    /// (<c>LayoutAreaReference.Encode</c> is <c>JsonSerializer.Serialize</c>; the same note is on
    /// <c>MeshOperations.IsAreaMaterialized</c>). Comparing the raw property name to a plain area
    /// name never matches, and the failure shape is a TIMEOUT rather than an assertion — the
    /// predicate simply never becomes true — which reads exactly like a view that does not render.
    /// It cost three timed-out tests here before the encoding was the suspect.</para>
    /// </summary>
    private static IReadOnlyList<string> Leaves(ChangeItem<JsonElement> frame)
    {
        if (frame.Value.ValueKind != JsonValueKind.Object
            || !frame.Value.TryGetProperty(LayoutAreaReference.Areas, out var areas)
            || areas.ValueKind != JsonValueKind.Object)
            return [];
        return [.. areas.EnumerateObject().Select(p => WireName(p.Name).Split('/').Last())];
    }

    // The wire property name decoded back to the area name it stands for; left as-is if it is not
    // the JSON-encoded form, so a shape change surfaces as a readable mismatch rather than a throw.
    private static string WireName(string property)
    {
        if (!property.StartsWith('"'))
            return property;
        try { return JsonSerializer.Deserialize<string>(property) ?? property; }
        catch (JsonException) { return property; }
    }

    private static IReadOnlyList<string> Cards(ChangeItem<JsonElement> frame) =>
        [.. Leaves(frame).Where(l => l.StartsWith("pkg-", StringComparison.Ordinal)).OrderBy(l => l, StringComparer.Ordinal)];

    /// <summary>A package source that lists exactly what it is told to and counts what is asked
    /// of it — standing in for the registry's catalog read.</summary>
    private sealed class StubSource : IPackageSource
    {
        private int listings;
        private int fetches;

        public IReadOnlyList<PackageManifest> Packages { get; set; } = [];

        public int Listings => Volatile.Read(ref listings);

        public int Fetches => Volatile.Read(ref fetches);

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Defer(() =>
            {
                Interlocked.Increment(ref listings);
                return Observable.Return(Packages);
            });

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Defer(() =>
            {
                Interlocked.Increment(ref fetches);
                return Observable.Return<IReadOnlyList<PackageFile>>([]);
            });
    }
}
