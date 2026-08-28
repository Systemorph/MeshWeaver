using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Data-path tests for OUT-OF-PROCESS renderers (the React Native leaf pack, the gRPC/SignalR
/// participants under <c>clients/</c>). Native views can't be unit-tested headlessly here, but every
/// one of them renders EXACTLY this Blazor-agnostic pipeline — the contract
/// <c>Doc/GUI/CrossRendererAuthoring</c> asks any renderer to hold to: <c>GetControlStream(area)</c> →
/// a <see cref="UiControl"/> tree; a container's <c>IContainerControl.Areas</c> → per-child-area
/// control streams; and each control's bound value. These tests pin that pipeline so a regression in
/// what a native renderer would receive is caught in CI without any native test host.
/// (Originally written against the retired <c>MeshWeaver.Maui</c> view pack; the pipeline under test
/// is renderer-agnostic and outlives it.)
/// </summary>
public class NativeRendererDataPathTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TreeView = nameof(TreeView);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            // A container (Stack) with leaf controls — the shapes an out-of-process renderer's
            // container view + label / markdown / html views consume.
            .AddLayout(layout => layout.WithView(TreeView, (_, _) => Observable.Return<UiControl>(
                Controls.Stack
                    .WithView(Controls.Label("Hello"), "Greeting")
                    .WithView(Controls.Markdown("# Title"), "Body")
                    .WithView(Controls.Html("<b>hi</b>"), "Html")
                    .WithView(Controls.DataGrid(new object[] { new { Name = "A" }, new { Name = "B" } })
                        .WithColumn(new PropertyColumnControl<string> { Property = "name" }.WithTitle("Name")), "Grid")
                    .WithView(new NavLinkControl("Home", null, "/home"), "Nav")
                    .WithView(new BadgeControl("new"), "Badge")
                    .WithView(new SelectControl("a", new Option[]
                        { new Option<string>("a", "Apple"), new Option<string>("b", "Banana") }), "Select"))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task ContainerTreeAndLeaves_ResolveThroughTheControlStreamPipeline()
    {
        var reference = new LayoutAreaReference(TreeView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        // 1. Root = a container — a renderer maps any IContainerControl to its container view.
        var root = await stream.GetControlStream(reference.Area!)
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is IContainerControl);

        var container = (IContainerControl)root!;
        container.Areas.Should().HaveCount(7, "a container view iterates IContainerControl.Areas");

        // 2. Each child area resolves to its leaf control — exactly RenderArea(stream, named.Area) per child.
        var leaves = new List<UiControl>();
        foreach (var named in container.Areas)
        {
            var leaf = await stream.GetControlStream(named.Area!.ToString()!)
                .Where(c => c is not null)
                .Should().Within(5.Seconds()).Match(c => c is not null);
            leaves.Add(leaf!);
            Output.WriteLine($"{named.Id}: {leaf!.GetType().Name}");
        }

        // 3. The leaf control TYPES (→ native views) and their bound VALUES (→ what a renderer binds).
        leaves.OfType<LabelControl>().Should().ContainSingle()
            .Which.Data!.ToString().Should().Contain("Hello");
        leaves.OfType<MarkdownControl>().Should().ContainSingle()
            .Which.Markdown!.ToString().Should().Contain("Title");
        leaves.OfType<HtmlControl>().Should().ContainSingle()
            .Which.Data!.ToString().Should().Contain("hi");
        // A data-grid view consumes Columns + the rows in Data.
        leaves.OfType<DataGridControl>().Should().ContainSingle()
            .Which.Columns.Should().NotBeEmpty();
        // Wave-2 controls reach the views too.
        leaves.OfType<NavLinkControl>().Should().ContainSingle()
            .Which.Title!.ToString().Should().Contain("Home");
        leaves.OfType<BadgeControl>().Should().ContainSingle()
            .Which.Data!.ToString().Should().Contain("new");
        // A select view reads the selected value (Data); options drive its picker items.
        leaves.OfType<SelectControl>().Should().ContainSingle()
            .Which.Data!.ToString().Should().Contain("a");
    }
}
