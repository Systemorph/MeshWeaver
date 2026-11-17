using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// "Bring the menu we have on the Markdown page to all the pages": every node's landing page offers
/// the node's actions — Edit and the ⋯ More dropdown that lists every option of <c>$Menu:Node</c> —
/// not only the pages that draw the standard header.
///
/// <para>Rendered through the layout client as a person sees it, one node per way a landing page can
/// be registered: the Markdown page and the generic page (both draw the standard header), a type's own
/// page registered without an opinion (observable and synchronous forms), and a page that declined the
/// node chrome but opted back in to the menu. Each must carry the ⋯ trigger, and the options it offers
/// (⋯ entries plus the Edit button, which the Markdown page folds into ⋯) must be the SAME set.</para>
///
/// <para>The negative control is a type that declines the menu explicitly: the same probe, on the same
/// mesh and viewer, finds no actions row there — so a hit elsewhere is the composition and not a probe
/// that would find a menu anywhere.</para>
/// </summary>
public class NodeMenuOnEveryPageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CardArea = "Card";

    private const string ObservablePageType = "MenuProbeObservablePage";
    private const string SyncPageType = "MenuProbeSyncPage";
    private const string DeclinedOptInType = "MenuProbeDeclinedOptIn";
    private const string OptedOutType = "MenuProbeOptedOut";

    private const string MarkdownNode = "menuprobe-markdown";
    private const string GenericNode = "menuprobe-generic";
    private const string ObservableNode = "menuprobe-observable";
    private const string SyncNode = "menuprobe-sync";
    private const string DeclinedOptInNode = "menuprobe-declined-optin";
    private const string OptedOutNode = "menuprobe-optedout";

    private const string CardText = "the type's own card";

    private static IObservable<UiControl?> Card(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<UiControl?>(Controls.Markdown(CardText));

    private static MarkdownControl SyncCard(LayoutAreaHost host, RenderingContext ctx)
        => Controls.Markdown(CardText);

    private static MeshNode PageType(string id, Func<LayoutDefinition, LayoutDefinition> layout)
        => new(id)
        {
            Name = id,
            HubConfiguration = config => config
                .AddDefaultLayoutAreas()
                .AddLayout(l => layout(l.WithDefaultArea(CardArea))),
        };

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                PageType(ObservablePageType, l => l.WithNodePage(CardArea, Card)),
                PageType(SyncPageType, l => l.WithNodePage(CardArea, SyncCard)),
                PageType(DeclinedOptInType, l => l.WithNodePage(CardArea, Card,
                    NodePageProvenance.Declined("the card reports its own facts"),
                    NodePageMenu.FrameworkSupplied)),
                PageType(OptedOutType, l => l.WithNodePage(CardArea, Card, null,
                    NodePageMenu.Declined("the probe's negative control: this page must carry no actions row"))),
                new MeshNode(MarkdownNode) { Name = "Markdown probe", NodeType = MarkdownNodeType.NodeType },
                new MeshNode(GenericNode) { Name = "Generic probe" },
                new MeshNode(ObservableNode) { Name = "Observable probe", NodeType = ObservablePageType },
                new MeshNode(SyncNode) { Name = "Sync probe", NodeType = SyncPageType },
                new MeshNode(DeclinedOptInNode) { Name = "Declined opt-in probe", NodeType = DeclinedOptInType },
                new MeshNode(OptedOutNode) { Name = "Opted-out probe", NodeType = OptedOutType });

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    /// <summary>
    /// The page's controls by area, plus the options its actions row offers: every ⋯ entry's target
    /// and the Edit button's, written relative to the node (<c>/{node}/X</c> → <c>/@/X</c>) so two
    /// nodes' menus compare as sets.
    /// </summary>
    private sealed record Page(IReadOnlyDictionary<string, UiControl> Controls, IReadOnlySet<string> Options)
    {
        public bool HasMoreTrigger => Controls.Values.OfType<MenuItemControl>()
            .Any(m => m.Class?.ToString() == MeshNodeLayoutAreas.MoreActionsClass);

        public bool HasCard => Controls.Values.OfType<MarkdownControl>()
            .Any(m => m.Markdown?.ToString() == CardText);
    }

    private IObservable<Page> Render(string node, string area, string? query = null)
    {
        var options = GetClient().JsonSerializerOptions;
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(node), new LayoutAreaReference(area) { Id = query })
            .Select(change => Read(change, options, node));
    }

    private static Page Read(ChangeItem<JsonElement> store, JsonSerializerOptions options, string node)
    {
        var controls = new Dictionary<string, UiControl>();
        if (store.Value.ValueKind == JsonValueKind.Object
            && store.Value.TryGetProperty(LayoutAreaReference.Areas, out var areas)
            && areas.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in areas.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    continue;
                try
                {
                    if (entry.Value.Deserialize<UiControl>(options) is { } control)
                        controls[entry.Name.Trim('"')] = control;
                }
                catch (JsonException) { /* a control type this client does not register — not an action */ }
                catch (NotSupportedException) { }
            }
        }

        var editHref = MeshNodeLayoutAreas.BuildUrl(node, MeshNodeLayoutAreas.EditArea);
        var prefix = "/" + node + "/";
        var targets = controls.Values.OfType<ButtonControl>()
            .Where(b => b.Class?.ToString() == MeshNodeLayoutAreas.MoreActionsItemClass
                        || b.NavigateToHref?.ToString() == editHref)
            .Select(b => b.NavigateToHref?.ToString() ?? "")
            .Select(href => href.StartsWith(prefix, StringComparison.Ordinal) ? "/@/" + href[prefix.Length..] : href)
            .ToHashSet();
        return new Page(controls, targets);
    }

    /// <summary>
    /// The menu is UP on a page once it offers Recycle (an action the platform always serves) and
    /// Files — a fact about the rendered list, never a point in time.
    /// </summary>
    private static bool MenuIsUp(Page page)
        => page.HasMoreTrigger
           && page.Options.Contains("/@/" + MeshNodeLayoutAreas.RecycleArea)
           && page.Options.Contains("/@/" + MeshNodeLayoutAreas.FilesArea)
           && page.Options.Contains("/@/" + MeshNodeLayoutAreas.EditArea);

    private async Task<Page> RenderedMenu(string node, string area)
    {
        Page last = new(new Dictionary<string, UiControl>(), new HashSet<string>());
        try
        {
            return await Render(node, area)
                .Do(p => last = p)
                .Where(MenuIsUp)
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence).Await();
        }
        catch (TimeoutException)
        {
            Assert.Fail($"{node}/{area} never carried the ⋯ More menu with Edit, Files and Recycle. Options seen: "
                        + string.Join(", ", last.Options) + "; areas: "
                        + string.Join(", ", last.Controls.Select(c => $"{c.Key}={c.Value.GetType().Name}")));
            throw;
        }
    }

    [HubFact]
    public async Task EveryLandingPage_OffersTheSameMenuAsTheMarkdownPage()
    {
        var markdown = await RenderedMenu(MarkdownNode, MeshNodeLayoutAreas.OverviewArea);
        Output.WriteLine("Markdown options: " + string.Join(", ", markdown.Options.Order()));

        foreach (var (node, area) in new[]
                 {
                     (GenericNode, MeshNodeLayoutAreas.OverviewArea),
                     (ObservableNode, CardArea),
                     (SyncNode, CardArea),
                     (DeclinedOptInNode, CardArea),
                 })
        {
            var page = await RenderedMenu(node, area);
            Output.WriteLine($"{node} options: " + string.Join(", ", page.Options.Order()));
            Assert.Equal(markdown.Options.Order(), page.Options.Order());
        }
    }

    [HubFact]
    public async Task ATypesOwnPage_KeepsItsContentBelowTheMenu()
    {
        var page = await Render(ObservableNode, CardArea)
            .Where(p => MenuIsUp(p) && p.HasCard)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.Contains(page.Controls.Keys, k => k.EndsWith("/" + MeshNodeLayoutAreas.NodePageMenuArea, StringComparison.Ordinal));
        Assert.Contains(page.Controls.Keys, k => k.EndsWith("/" + MeshNodeLayoutAreas.NodePageBodyArea, StringComparison.Ordinal));
    }

    /// <summary>
    /// Negative control: a page that declines the menu renders its own card — proving it rendered —
    /// and no actions row, while the same probe finds the row on every other page above.
    /// </summary>
    [HubFact]
    public async Task APageThatDeclinesTheMenu_HasNone()
    {
        var page = await Render(OptedOutNode, CardArea)
            .Where(p => p.HasCard)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.False(page.HasMoreTrigger);
        Assert.Empty(page.Options);
        Assert.DoesNotContain(page.Controls.Keys, k => k.EndsWith("/" + MeshNodeLayoutAreas.NodePageMenuArea, StringComparison.Ordinal));
    }

    /// <summary>An <c>@@</c> embed of a composed page carries the page alone, as the Markdown page drops its header there.</summary>
    [HubFact]
    public async Task AnEmbed_CarriesNoMenu()
    {
        var page = await Render(ObservableNode, CardArea, "?showHeader=false")
            .Where(p => p.HasCard)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.False(page.HasMoreTrigger);
        Assert.DoesNotContain(page.Controls.Keys, k => k.EndsWith("/" + MeshNodeLayoutAreas.NodePageMenuArea, StringComparison.Ordinal));
    }

    /// <summary>The verdicts are recorded beside the renderer, and a provenance decline keeps the page as it is unless it opts in.</summary>
    [Fact]
    public void TheMenuVerdictFollowsTheProvenanceVerdict_UnlessStated()
    {
        Assert.Equal(NodePageMenuKind.FrameworkSupplied, NodePageMenu.For(NodePageProvenance.FrameworkSupplied).Kind);
        Assert.Equal(NodePageMenuKind.RenderedByThePage, NodePageMenu.For(NodePageProvenance.RenderedByThePage).Kind);
        var declined = NodePageMenu.For(NodePageProvenance.Declined("a chat message"));
        Assert.Equal(NodePageMenuKind.Declined, declined.Kind);
        Assert.Contains("a chat message", declined.Reason);
        Assert.Throws<ArgumentException>(() => NodePageMenu.Declined(" "));
    }
}
