using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The plugin catalog is a TEMPLATE whose per-card Install / Update button is a ROW-SCOPED action
/// (Doc/GUI/DataBinding → "Templates first, data later" and "Row-scoped actions"): the cards are one
/// bound row template, the button is declared ONCE in it, and the click carries the card it was
/// clicked in. Driven through the real catalog render (<see cref="CatalogLayoutAreas.RenderFromSource"/>)
/// and the owner's real click dispatch; the package source records what the installer was asked to
/// fetch and hands it nothing, so no install ever writes a node.
/// </summary>
public class CatalogActionIdentityTest : HubTestBase
{
    private const string Area = CatalogLayoutAreas.CatalogArea;
    private const string Category = "Fixtures";

    private static string InstallArea => $"{Area}/cards/{ItemTemplateControl.ViewArea}/install";

    private static string CardsPointer => LayoutAreaReference.GetDataPointer(CatalogLayoutAreas.CardsDataId);

    private readonly BehaviorSubject<IReadOnlyList<PackageManifest>> listing = new(Initial);
    private readonly ReplaySubject<string> fetched = new();
    private readonly RecordingSource source;

    /// <summary>A source whose listing never answers — the template must not wait for it.</summary>
    private readonly SilentSource silent = new();

    public CatalogActionIdentityTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<AccessService>();
        source = new RecordingSource(listing, fetched);
    }

    private static PackageManifest Package(string id) => new()
    {
        Id = id, Name = $"Package {id}", Version = "2.0.0", Kind = PackageKind.Content,
        Category = Category, TargetPartition = "Fixture" + id,
    };

    private static readonly ImmutableList<PackageManifest> Initial = [Package("a"), Package("b"), Package("c")];

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(PackageManifest), typeof(CatalogPageView), typeof(CatalogTileRow),
                typeof(CatalogCardRow), typeof(CatalogOrphanRow))
            .AddLayout(layout => layout
                .WithView(Area, (host, _) => CatalogLayoutAreas.RenderFromSource(host, source, "HEAD", null, "fixture"))
                .WithView("Silent", (host, _) => CatalogLayoutAreas.RenderFromSource(host, silent, "HEAD", null, "silent")));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>The page renders AT ONCE — its first control is the whole template, with the loading
    /// line showing — while the source has not answered and never will.</summary>
    [HubFact]
    public async Task TheCatalogIsATemplate_ItsFirstRenderWaitsOnNoData()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference("Silent"));
        var root = await stream.GetControlStream("Silent").Should().Within(10.Seconds()).Match(
            c => c is StackControl, "the template is returned at once, before any listing",
            TestContext.Current.CancellationToken);
        Assert.IsAssignableFrom<StackControl>(root).Areas.Select(a => a.Id?.ToString()).Should().Contain(
            ["title", "loading", "categories", "cards", "orphans"], "every section is declared up front");
        (await stream.GetControlStream($"Silent/cards").Should().Within(10.Seconds()).Match(
                c => c is ItemTemplateControl, "the cards are one bound row template",
                TestContext.Current.CancellationToken))
            .Should().NotBeNull();
        silent.Listings.Should().BeGreaterThanOrEqualTo(1, "the listing was asked for — and the page did not wait for it");
    }

    /// <summary>N cards, ONE Install button: clicking it in row k installs package k, for every k.</summary>
    [HubFact]
    public async Task TheInstallButtonInRowKInstallsPackageK()
    {
        var stream = await OpenCategory();
        var rendered = await RenderedCards(stream, Initial.Count);

        for (var k = 0; k < Initial.Count; k++)
        {
            var id = await ClickAndReadFetch(stream, AsTheClientRendersIt(rendered, k));
            id.Should().Be(Initial[k].Id, $"the Install button in row {k} was clicked");
        }
    }

    /// <summary>The listing changes between the render and the click — a package inserted above, one
    /// removed — and the click still installs the package that was clicked, not the one that moved
    /// into its slot.</summary>
    [HubFact]
    public async Task AfterTheListChanged_TheClickStillInstallsTheClickedPackage()
    {
        var stream = await OpenCategory();
        var clicked = AsTheClientRendersIt(await RenderedCards(stream, Initial.Count), 1); // "b"

        listing.OnNext([Package("0"), Package("a"), Package("b"), Package("c")]); // "b" moves to slot 2
        var after = await RenderedCards(stream, 4);
        after[1].GetProperty("id").GetString().Should().Be("a", "premise: another package now sits in slot 1");

        (await ClickAndReadFetch(stream, clicked)).Should().Be("b");
    }

    /// <summary>A card whose package has LEFT the listing since the render installs nothing — the row
    /// names the package, and the server's current page decides whether it is on offer.</summary>
    [HubFact]
    public async Task APackageThatLeftTheListingInstallsNothing()
    {
        var stream = await OpenCategory();
        var clicked = AsTheClientRendersIt(await RenderedCards(stream, Initial.Count), 1); // "b"

        listing.OnNext([Package("a"), Package("c")]);
        await RenderedCards(stream, 2);

        Submit(stream, clicked);
        await fetched.Should().NotEmit(500.Milliseconds(),
            "the clicked package is no longer offered, and no other package may take its place",
            TestContext.Current.CancellationToken);
    }

    /// <summary>NEGATIVE CONTROL: the same button clicked with no row installs nothing — the package
    /// comes from the row on the click and from nothing else.</summary>
    [HubFact]
    public async Task AClickWithNoRowInstallsNothing()
    {
        var stream = await OpenCategory();
        await RenderedCards(stream, Initial.Count);

        Submit(stream, row: null);
        await fetched.Should().NotEmit(500.Milliseconds(),
            "with no row the action cannot know which package, and must not guess",
            TestContext.Current.CancellationToken);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private async Task<ISynchronizationStream<JsonElement>> OpenCategory()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(Area) { Id = $"{Area}?{CatalogLayoutAreas.CategoryParam}={Category}" });
        await stream.GetControlStream(InstallArea).Should().Within(10.Seconds()).Match(
            c => c is ButtonControl, "the card template's Install button is rendered once, in the template",
            TestContext.Current.CancellationToken);
        return stream;
    }

    private static async Task<ImmutableArray<JsonElement>> RenderedCards(
        ISynchronizationStream<JsonElement> stream, int count)
    {
        var cards = await stream.GetDataStream<JsonElement>(new JsonPointerReference(CardsPointer))
            .Should().Within(10.Seconds()).Match(
                c => c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == count,
                $"the client mirror holds the {count} cards it renders",
                TestContext.Current.CancellationToken);
        return [.. cards.EnumerateArray().Select(c => c.Clone())];
    }

    /// <summary>What the Blazor <c>ItemTemplate</c> cascades for row k: its pointer, index and the value it rendered.</summary>
    private static RowContext AsTheClientRendersIt(ImmutableArray<JsonElement> cards, int k) => new()
    {
        Pointer = $"{CardsPointer}/{k}",
        Index = k,
        Value = cards[k].Clone(),
    };

    private static void Submit(ISynchronizationStream<JsonElement> stream, RowContext? row)
        => stream.SubmitUserAction(new ClickedEvent(InstallArea, stream.StreamId) { Row = row },
            actingUser: null, onRefused: null, onAccepted: null);

    private async Task<string> ClickAndReadFetch(ISynchronizationStream<JsonElement> stream, RowContext row)
    {
        var next = new ReplaySubject<string>(1);
        using var _ = fetched.Skip(Fetches).Subscribe(next);
        Submit(stream, row);
        var id = await next.Should().Within(10.Seconds()).Emit(
            "the Install click asks the source for the package's files", TestContext.Current.CancellationToken);
        Fetches++;
        Assert.NotNull(id);
        return id;
    }

    private int Fetches { get; set; }

    private sealed class RecordingSource(
        IObservable<IReadOnlyList<PackageManifest>> listing, IObserver<string> fetched) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) => listing;

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
            => Observable.Defer(() =>
            {
                fetched.OnNext(package.Id);
                // Not even an empty payload: the installer never runs, so nothing is written.
                return Observable.Empty<IReadOnlyList<PackageFile>>();
            });
    }

    private sealed class SilentSource : IPackageSource
    {
        private int listings;

        public int Listings => Volatile.Read(ref listings);

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
            => Observable.Defer(() =>
            {
                Interlocked.Increment(ref listings);
                return Observable.Never<IReadOnlyList<PackageManifest>>();
            });

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
            => Observable.Empty<IReadOnlyList<PackageFile>>();
    }
}
