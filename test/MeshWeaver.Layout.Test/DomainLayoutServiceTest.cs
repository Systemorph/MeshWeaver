using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

public class DomainLayoutServiceTest(ITestOutputHelper output) : HubTestBase(output)
{

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r =>
                r.RouteAddress(ClientType, (_, d) => d.Package())
            )
            // 🚨 The SECOND type is the whole point of TestCatalogOfATypeThatIsNotAWorkspaceCollection
            // (#5065): registered in the hub's TypeRegistry, mapped to NO data source. That is the
            // ordinary production state of every content type a NodeType declares, and it is the one
            // case the Catalog area's guard used to walk straight past.
            .WithTypes(typeof(UnmappedRecord))
            .AddData(data =>
                data.AddSource(
                    ds =>
                        ds.WithType<DataRecord>(t =>
                            t.WithInitialData(DataRecord.InitialData)
                        )
                )
            )
            .AddLayout(l => l.AddDomainLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithRoutes(r =>
                r.RouteAddress(HostType, (_, d) => d.Package())
            )
            .AddLayoutClient();
    }

    [HubFact]
    public async Task TestEntityView()
    {
        var reference = DomainLayoutAreas.GetDetailsReference(nameof(DataRecord), "Hello");
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null);
        var stack = content
            .Should()
            .BeOfType<StackControl>()
            .Which;

        // The last area is a reactive view that resolves to BuildPropertyForm() → StackControl
        var controlFromStream = await stream
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Should().Within(10.Seconds()).Match(x => x != null);
        var formStack = controlFromStream.Should().BeOfType<StackControl>().Which;

        // Navigate into the form to find a control with DataContext:
        // formStack → first area (LayoutGridControl) → first property area (StackControl) → last area (reactive control with DataContext)
        var gridAreaId = formStack.Areas.First().Area.ToString()!;
        var gridControl = await stream
            .GetControlStream(gridAreaId)
            .Should().Within(10.Seconds()).Match(x => x != null);
        var grid = gridControl.Should().BeOfType<LayoutGridControl>().Which;

        var propAreaId = grid.Areas.First().Area.ToString()!;
        var propControl = await stream
            .GetControlStream(propAreaId)
            .Should().Within(10.Seconds()).Match(x => x != null);
        var propStack = propControl.Should().BeOfType<StackControl>().Which;

        var reactiveAreaId = propStack.Areas.Last().Area.ToString()!;
        var reactiveControl = await stream
            .GetControlStream(reactiveAreaId)
            .Should().Within(10.Seconds()).Match(x => x != null);
        var dataContext = reactiveControl!.DataContext;
        dataContext.Should().NotBeNullOrWhiteSpace();


        var namePointer = new JsonPointerReference($"displayName");
        var nameStream = stream.DataBind<string>(namePointer, dataContext);
        var value = await nameStream
            .Where(x => x != null)
            .Should().Within(10.Seconds()).Match(x => x != null);
        value.Should().NotBeNull();
        value.Should().Be("Hello");

        var objectStream = stream.DataBind<JsonElement>(new(dataContext!));
        var obj = await objectStream.Should().Within(10.Seconds()).Emit();
        const string Universe = nameof(Universe);

        var jsonModel = obj.AsNode()?.ToJsonString();
        var model = new ModelParameter<JsonElement>(string.IsNullOrEmpty(jsonModel) ? default : JsonDocument.Parse(jsonModel).RootElement, (m,r)=>m.GetValueFromModel(r));
        model.Update(new JsonPatch(PatchOperation.Replace(JsonPointer.Parse("/displayName"), JsonNode.Parse($"\"{Universe}\""))));

        var log = await stream.SubmitModel(model)
            .Should().Emit();
        log.Status.Should().Be(ActivityStatus.Succeeded);

        value = await stream
            .DataBind<string>(namePointer, dataContext!, (x, _) => (string)x!)
            .Where(x => x != "Hello")
            .Should().Within(10.Seconds()).Match(x => x != null);
        value.Should().Be(Universe);
        stream.Dispose();
        stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );
        // After re-opening, the form is again a StackControl
        controlFromStream = await stream
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Should().Within(10.Seconds()).Match(x => x != null);

        controlFromStream.Should().BeOfType<StackControl>();
        value = await stream
            .GetDataBoundObservable<string>(namePointer, dataContext!)
            .Should().Within(10.Seconds()).Match(x => x != null);

        value.Should().Be(Universe);
    }


    [HubFact]
    public async Task TestCatalog()
    {
        var host = GetHost();
        var reference = DomainLayoutAreas.GetCatalogReference(nameof(DataRecord));
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null);
        var stack = content
            .Should()
            .BeOfType<StackControl>()
            .Which;

        var control = await stream
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Should().Within(10.Seconds()).Match(x => x is not null);
        var dataGrid = control.Should().BeOfType<DataGridControl>().Which;
        var pointer = dataGrid.Data.Should().BeOfType<JsonPointerReference>().Which;
        var dataStream = await stream
            .GetDataStream<IEnumerable<object>>(pointer)
            .Should().Within(10.Seconds()).Match(x => x is not null);
        dataStream.Should().BeOfType<IEnumerable<object>>().Which.Should().HaveCount(2);

    }

    /// <summary>
    /// The negative control for <see cref="TestCatalog"/>, and the reproduction of
    /// Systemorph/MeshWeaver#5065: a type the hub's <c>ITypeRegistry</c> knows but that NO data source
    /// maps renders the area's actionable caution, rather than letting
    /// <c>WorkspaceStreams.CreateWorkspaceStream</c> throw <c>"Collections X are not mapped to any
    /// source"</c> out of the render.
    ///
    /// <para>The two tests sit on opposite sides of exactly one condition — <c>DataRecord</c> is
    /// mapped, <c>UnmappedRecord</c> is not — and both types are in the same TypeRegistry, which is
    /// why the old guard could not tell them apart.</para>
    ///
    /// <para>Asserting on the CONTROL TYPE is deliberate: before the fix the failing render still put
    /// a control in the area (the host's top-level <c>Catch</c> renders an error panel), so "the area
    /// produced something" proves nothing. What distinguishes the two outcomes is a
    /// <see cref="MarkdownControl"/> carrying the area's own guidance versus the render-error control
    /// carrying a framework exception message.</para>
    /// </summary>
    [HubFact]
    public async Task TestCatalogOfATypeThatIsNotAWorkspaceCollection()
    {
        var reference = DomainLayoutAreas.GetCatalogReference(nameof(UnmappedRecord));
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        var content = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null);

        var text = content.Should().BeOfType<MarkdownControl>().Subject.Markdown?.ToString()
                   ?? string.Empty;
        text.Should().Contain(nameof(UnmappedRecord),
            "the author needs to know WHICH type they named");
        text.Should().NotContain("not mapped to any source",
            "the framework's own workspace diagnostic must never reach a viewer");
    }

    /// <summary>Every area of a frame: its id and its control.</summary>
    private static IEnumerable<(string Id, JsonElement Control)> Areas(JsonElement frame)
        => frame.TryGetProperty("areas", out var areas) && areas.ValueKind == JsonValueKind.Object
            ? areas.EnumerateObject().Where(a => a.Value.ValueKind == JsonValueKind.Object)
                .Select(a => (a.Name, a.Value))
            : [];

    private static string? Prop(JsonElement control, string name)
        => control.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Is(JsonElement control, string type) => Prop(control, "$type") == type;

    /// <summary>The navigation glyph link <paramref name="glyph"/> in a frame, or null.</summary>
    private static JsonElement? NavLink(JsonElement frame, string glyph)
        => Areas(frame).Where(a => Is(a.Control, nameof(LinkControl)) && Prop(a.Control, "text") == glyph)
            .Select(a => (JsonElement?)a.Control).FirstOrDefault();

    private IObservable<JsonElement> Frames(LayoutAreaReference reference)
        => GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference)
            .Select(change => change.Value);

    /// <summary>
    /// The Catalog's header is the platform's controls (maintainer, 2026-10-18: hand-built HTML
    /// replaced fleet-wide): the type's name as a heading LABEL, the ⧉ Data Model link as a
    /// <see cref="LinkControl"/> with its hover text — no <see cref="HtmlControl"/> anywhere in the frame.
    /// </summary>
    [HubFact]
    public async Task TestCatalog_HeaderIsStandardControls()
    {
        var frame = await Frames(DomainLayoutAreas.GetCatalogReference(nameof(DataRecord)))
            .Should().Within(10.Seconds())
            .Match(f => NavLink(f, "⧉") is not null && Areas(f).Any(a => Is(a.Control, nameof(DataGridControl))),
                "the Catalog renders its header and grid");

        var link = NavLink(frame, "⧉")!.Value;
        Prop(link, "tooltip").Should().Be("Data Model");
        Prop(link, "href").Should().EndWith($"/DataModel/{nameof(DataRecord)}");
        Areas(frame).Should().Contain(a => Is(a.Control, nameof(LabelControl)) && !string.IsNullOrEmpty(Prop(a.Control, "data")),
            "the type's name is a heading label");
        Areas(frame).Should().NotContain(a => Is(a.Control, nameof(HtmlControl)), "the Catalog holds no hand-built markup");
    }

    /// <summary>
    /// The Data Model page of one type: an H1 label with the type name, the ⧉ overview link, and the
    /// property table as Markdown — the navigation lives in controls, not in markup inside the Markdown.
    /// </summary>
    [HubFact]
    public async Task TestDataModelTypePage_HeaderIsStandardControls()
    {
        var frame = await Frames(new LayoutAreaReference(nameof(DataModelLayoutArea.DataModel)) { Id = nameof(DataRecord) })
            .Should().Within(10.Seconds())
            .Match(f => NavLink(f, "⧉") is not null
                    && Areas(f).Any(a => Is(a.Control, nameof(MarkdownControl))
                        && (Prop(a.Control, "markdown") ?? "").Contains("## Properties")),
                "the type page renders its header and property table");

        Prop(NavLink(frame, "⧉")!.Value, "tooltip").Should().Be("Data Model Overview");
        Areas(frame).Should().Contain(a => Is(a.Control, nameof(LabelControl)) && Prop(a.Control, "data") == nameof(DataRecord),
            "the type name is a heading label");
        Areas(frame).Should().NotContain(a => Is(a.Control, nameof(HtmlControl)), "the type page holds no hand-built markup");
        Areas(frame).Where(a => Is(a.Control, nameof(MarkdownControl)))
            .Should().NotContain(a => (Prop(a.Control, "markdown") ?? "").Contains("<a "),
                "no navigation markup is left inside the Markdown body");
    }

    /// <summary>
    /// A type registered for serialization only — no data source, therefore no workspace collection.
    /// Deliberately minimal: what matters is that it is resolvable by name in the TypeRegistry and
    /// absent from <c>DataContext.TypeSources</c>.
    /// </summary>
    public record UnmappedRecord(string Id, string Label);
}
