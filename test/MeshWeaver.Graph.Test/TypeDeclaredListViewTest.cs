using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Every children catalog PAGES in the store, and a NodeType DECLARES how its instances are listed
/// (<see cref="NodeTypeDefinition.ListView"/>) — Doc/GUI/MeshSearch → "A type declares its list".
///
/// <para>The request this answers: a page listing hundreds of benchmark runs loaded every one of
/// them as a card. A catalog now asks the store for one page at a time, and a page whose children
/// are all of a type that declares a table renders that table. The decision reads the TYPES the
/// catalog lists (one row per distinct type, no content) and each type's definition — never the
/// children's content.</para>
/// </summary>
public class TypeDeclaredListViewTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "Lv";
    private const string RunType = "Lv/Run";
    private const string RunsPath = "Lv/Runs";
    private const string MixedPath = "Lv/Mixed";
    private const string PlainPath = "Lv/Plain";

    private static readonly NodeTypeListView RunTable = new()
    {
        Mode = NodeTypeListMode.Table,
        Columns =
        [
            new MeshSearchColumn("Model", "content.model"),
            new MeshSearchColumn("Finished", "content.finishedAt") { Format = "yyyy-MM-dd HH:mm" },
        ],
        PageSize = 25,
        Sort = "content.finishedAt-desc",
    };

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(
            new MeshNode(Space) { NodeType = "Space", Name = Space },
            new MeshNode("Run", Space)
            {
                Name = "Run",
                NodeType = MeshNode.NodeTypePath,
                Content = new NodeTypeDefinition { Description = "A run", ListView = RunTable },
            },
            Page("Runs"), Page("Mixed"), Page("Plain"));

    /// <summary>
    /// The listed children are CREATED, never registered: a catalog lists content (<c>is:content</c>),
    /// and nodes contributed by <c>AddMeshNodes</c> are registrations, which it rightly leaves out.
    /// </summary>
    private async Task SeedChildren()
    {
        foreach (var node in new[]
                 {
                     Run(RunsPath, "r1"), Run(RunsPath, "r2"), Run(RunsPath, "r3"),
                     Run(MixedPath, "m1"), Markdown(MixedPath, "notes"),
                     Markdown(PlainPath, "a"), Markdown(PlainPath, "b"),
                 })
            await NodeFactory.CreateNode(node).Should().Within(TestTimeouts.Convergence).Emit();
    }

    private static MeshNode Page(string id) => new(id, Space)
    {
        Name = id,
        NodeType = "Markdown",
        Content = new MarkdownContent { Content = $"# {id}\n\n@@(\"area/Search\")\n" },
    };

    private static MeshNode Run(string ns, string id) => new(id, ns) { Name = id, NodeType = RunType };

    private static MeshNode Markdown(string ns, string id) => new(id, ns)
    {
        Name = id,
        NodeType = "Markdown",
        Content = new MarkdownContent { Content = $"# {id}\n" },
    };

    // ── paging is the default ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheContentCatalog_PagesInTheStore_ByDefault()
    {
        var catalog = MeshNodeLayoutAreas.BuildCatalog(RunsPath, new MeshNodeLayoutAreas.CatalogOptions());

        catalog.PageSize.Should().Be(MeshNodeLayoutAreas.DefaultCatalogPageSize,
            "no catalog may load an unbounded set — the store answers one page at a time");
        catalog.Sections?.ItemLimit.Should().BeNull(
            "a client-side cap would clip the grown window and hide 'Load more' past the first page");
    }

    [Fact]
    public void ATypesInstanceCatalog_PagesInTheStore_ByDefault()
    {
        var query = MeshNodeLayoutAreas.NodeTypeCatalogQuery.From(RunType, new NodeTypeDefinition());
        var search = MeshNodeLayoutAreas.BuildNodeTypeCatalog(RunType, new MeshNodeLayoutAreas.CatalogOptions(), query);

        search.PageSize.Should().Be(MeshNodeLayoutAreas.DefaultCatalogPageSize);
        search.RenderMode.Should().Be(MeshSearchRenderMode.NamespaceTree,
            "a type that declares nothing keeps the catalog's own shape");
    }

    [Fact]
    public void AnExplicitLimit_StillCapsTheSections()
    {
        var catalog = MeshNodeLayoutAreas.BuildCatalog(RunsPath, new MeshNodeLayoutAreas.CatalogOptions { ItemLimit = 7 });

        catalog.Sections!.ItemLimit.Should().Be(7, "?limit=N is an explicit request and is honoured");
    }

    // ── the decision, pure ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTypeProbe_ReadsOneRowPerType_AndNoContent()
    {
        const string hidden = "namespace:Lv/Runs is:main is:content";

        MeshNodeLayoutAreas.TypeProbeQuery(hidden, []).Should().Be($"{hidden} select:path,nodeType limit:1");
        MeshNodeLayoutAreas.TypeProbeQuery(hidden, ["Lv/Run", "Markdown"]).Should().Be(
            $"{hidden} -nodeType:Lv/Run -nodeType:Markdown select:path,nodeType limit:1",
            "each probe excludes the types already found, so N types cost N+1 one-row queries");
    }

    [Fact]
    public void ASoleDeclaringType_ShapesTheWholeCatalog()
    {
        var catalog = MeshNodeLayoutAreas.BuildCatalog(RunsPath, new MeshNodeLayoutAreas.CatalogOptions());
        var shaped = MeshNodeLayoutAreas.ApplyTypeViews(catalog, new MeshNodeLayoutAreas.CatalogOptions(), Listed([RunType], RunTable));

        shaped.RenderMode.Should().Be(MeshSearchRenderMode.Table);
        shaped.Columns!.Select(c => c.Field).Should().Equal("content.model", "content.finishedAt");
        shaped.PageSize.Should().Be(25, "the type's declared page size wins over the default");
        shaped.Sections!.MaxRows.Should().BeNull(
            "a table is not a card grid — the collapsed-rows cap would show nine rows of a page");
        ((string)shaped.HiddenQuery!).Should().EndWith("sort:content.finishedAt-desc",
            "the declared order is applied in the store, after the catalog's own filters");
    }

    [Fact]
    public void AnExplicitPageSize_WinsOverTheDeclaration()
    {
        var o = new MeshNodeLayoutAreas.CatalogOptions { PageSize = 10, PageSizeExplicit = true };
        var shaped = MeshNodeLayoutAreas.ApplyTypeViews(MeshNodeLayoutAreas.BuildCatalog(RunsPath, o), o, Listed([RunType], RunTable));

        shaped.PageSize.Should().Be(10);
    }

    [Fact]
    public void AnExplicitShape_WinsOverTheDeclaration_ButKeepsTheSectionEntry()
    {
        var o = new MeshNodeLayoutAreas.CatalogOptions
        {
            Mode = MeshSearchRenderMode.Grouped, GroupByProperty = "NodeType", ShapeExplicit = true,
        };
        var shaped = MeshNodeLayoutAreas.ApplyTypeViews(MeshNodeLayoutAreas.BuildCatalog(RunsPath, o), o, Listed([RunType], RunTable));

        shaped.RenderMode.Should().Be(MeshSearchRenderMode.Grouped, "?groupBy= chose the shape");
        shaped.TypeViews.Should().ContainSingle().Which.Should().Be(
            MeshNodeLayoutAreas.ToTypeView(RunType, RunTable));
    }

    [Fact]
    public void AMixedListing_RendersEachDeclaringTypesSection_ByItsDeclaration()
    {
        var o = new MeshNodeLayoutAreas.CatalogOptions();
        var catalog = MeshNodeLayoutAreas.BuildCatalog(MixedPath, o);
        var shaped = MeshNodeLayoutAreas.ApplyTypeViews(catalog, o, Listed([RunType, "Markdown"], RunTable));

        shaped.RenderMode.Should().Be(catalog.RenderMode, "a mixed listing keeps the catalog's own shape");
        var entry = shaped.TypeViews.Should().ContainSingle("only the declaring type gets an entry").Subject;
        entry.NodeType.Should().Be(RunType);
        entry.RenderMode.Should().Be(MeshSearchRenderMode.Table);
        entry.Columns!.Should().HaveCount(2);
    }

    [Fact]
    public void WithoutADeclaration_TheCatalogStaysCards()
    {
        var o = new MeshNodeLayoutAreas.CatalogOptions();
        var catalog = MeshNodeLayoutAreas.BuildCatalog(PlainPath, o);

        MeshNodeLayoutAreas.ApplyTypeViews(catalog, o, Listed(["Markdown"])).Should().Be(catalog);
        MeshNodeLayoutAreas.ApplyTypeViews(catalog, o, MeshNodeLayoutAreas.ListedTypes.None).Should().Be(catalog);
        MeshNodeLayoutAreas.ApplyTypeViews(catalog, o,
                Listed([RunType], RunTable) with { Truncated = true })
            .RenderMode.Should().Be(catalog.RenderMode, "a probe that stopped early cannot say ALL children share a type");
    }

    [Fact]
    public void ATypesOwnInstanceCatalog_HonoursItsDeclaration()
    {
        var query = MeshNodeLayoutAreas.NodeTypeCatalogQuery.From(RunType, new NodeTypeDefinition { ListView = RunTable });
        query.ListView.Should().Be(RunTable);

        var search = MeshNodeLayoutAreas.BuildNodeTypeCatalog(RunType, new MeshNodeLayoutAreas.CatalogOptions(), query);
        search.RenderMode.Should().Be(MeshSearchRenderMode.Table);
        search.PageSize.Should().Be(25);

        MeshNodeLayoutAreas.NodeTypeCatalogQuery.From(RunType,
                new NodeTypeDefinition { ListView = new NodeTypeListView { Mode = NodeTypeListMode.Cards } })
            .ListView.Should().BeNull("declaring Cards is declaring the default");
    }

    [Fact]
    public void TheDeclaration_IsAuthoredAsJson()
    {
        const string json = """
            {
              "listView": {
                "mode": "Table",
                "columns": [ { "title": "Model", "field": "content.model" },
                             { "title": "Wall s", "field": "content.wallSeconds", "format": "N0", "sortable": false } ],
                "pageSize": 40,
                "sort": "content.finishedAt-desc"
              }
            }
            """;
        var definition = JsonSerializer.Deserialize<NodeTypeDefinition>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        definition.ListView!.Mode.Should().Be(NodeTypeListMode.Table);
        definition.ListView.Columns!.Select(c => (c.Title, c.Field, c.Format, c.Sortable)).Should().Equal(
            ("Model", "content.model", null, true), ("Wall s", "content.wallSeconds", "N0", false));
        definition.ListView.PageSize.Should().Be(40);
        definition.ListView.Sort.Should().Be("content.finishedAt-desc");
    }

    // ── on a mesh ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchOnAnInstance_IsATemplate_TheListInASkeletonSlot()
    {
        var page = MeshNodeLayoutAreas.CatalogTemplate(new Address(RunsPath), RunsPath, "?groupBy=type");

        var slot = MarkdownEditIsATemplateTest.Descendants(page).OfType<LayoutAreaControl>().Should().ContainSingle().Subject;
        slot.Reference.Area.Should().Be(MeshNodeLayoutAreas.CatalogListArea);
        slot.Reference.Id.Should().Be("?groupBy=type", "every catalog knob still reaches the list");
        slot.SpinnerType.Should().Be(SpinnerType.Skeleton);

        var (stream, area) = Open(RunsPath, MeshNodeLayoutAreas.SearchArea);
        await stream.WalkTree(area).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is LayoutAreaControl { Reference.Area: MeshNodeLayoutAreas.CatalogListArea },
                "the Search page of an ordinary node is the frame with the catalog slot");
    }

    [Fact]
    public async Task ChildrenAllOfADeclaringType_RenderAsItsPagedTable()
    {
        await SeedChildren();
        var search = await CatalogOf(RunsPath, s => Mode(s) == nameof(MeshSearchRenderMode.Table));

        search.Columns!.Select(c => c.Title).Should().Equal("Model", "Finished");
        search.PageSize.Should().Be(25);
        search.HiddenQuery!.ToString().Should().Contain("sort:content.finishedAt-desc");
    }

    [Fact]
    public async Task MixedChildren_GroupedByType_CarryTheDeclaringTypesSection()
    {
        await SeedChildren();
        var search = await CatalogOf(MixedPath, s => s.TypeViews is { Count: > 0 }, "?groupBy=type");

        Mode(search).Should().Be(nameof(MeshSearchRenderMode.Grouped));
        var entry = search.TypeViews!.Should().ContainSingle().Subject;
        entry.NodeType.Should().Be(RunType);
        entry.RenderMode.Should().Be(MeshSearchRenderMode.Table);
        search.PageSize.Should().Be(MeshNodeLayoutAreas.DefaultCatalogPageSize);
    }

    [Fact]
    public async Task ChildrenOfUndeclaredTypes_StayTheNavigatorsCards_Paged()
    {
        await SeedChildren();
        var search = await CatalogOf(PlainPath, s => s.PageSize is not null);

        Mode(search).Should().Be(nameof(MeshSearchRenderMode.GraphNavigator));
        (search.TypeViews ?? []).Should().BeEmpty();
        search.PageSize.Should().Be(MeshNodeLayoutAreas.DefaultCatalogPageSize);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    private static MeshNodeLayoutAreas.ListedTypes Listed(string[] types, NodeTypeListView? runView = null)
        => new(types.ToImmutableList(), false,
            runView is null
                ? ImmutableDictionary<string, NodeTypeListView>.Empty
                : ImmutableDictionary<string, NodeTypeListView>.Empty.Add(RunType, runView));

    private static string? Mode(MeshSearchControl s) => s.RenderMode switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } n => ((MeshSearchRenderMode)n.GetInt32()).ToString(),
        JsonElement je => je.GetString(),
        { } other => other.ToString(),
        null => null,
    };

    private async Task<MeshSearchControl> CatalogOf(string path, Func<MeshSearchControl, bool> predicate, string? queryString = null)
    {
        var (stream, area) = Open(path, MeshNodeLayoutAreas.CatalogListArea, queryString);
        return (MeshSearchControl)(await stream.WalkTree(area).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is MeshSearchControl s && predicate(s), "the catalog slot renders the decided catalog"))!;
    }

    private (ISynchronizationStream<JsonElement> Stream, string Area) Open(string path, string area, string? id = null)
    {
        var reference = new LayoutAreaReference(area) { Id = id };
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(path), reference);
        return (stream, reference.Area!);
    }
}
