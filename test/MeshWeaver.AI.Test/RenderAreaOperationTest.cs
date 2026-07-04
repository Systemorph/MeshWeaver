using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the <see cref="MeshOperations.RenderArea"/> verb — the shared core behind
/// <c>POST /api/mesh/render-area</c> (the SSR seeding endpoint; REST is a thin transport
/// wrapper over this method, per the MeshApiEndpoints convention):
/// <list type="bullet">
/// <item>renders a REAL layout area (a Markdown node's <c>Overview</c> from the standard
/// AddGraph fixture) and returns the wire <c>{areas, data}</c> frame with <c>$type</c>
/// discriminators and JSON-encoded instance keys;</item>
/// <item>a default-area render carries the <c>areas[""]</c> NamedArea indirection AND the
/// resolved area's rendered control (first-paint fidelity);</item>
/// <item>an unknown path returns the clean <c>"Not found: …"</c> sentinel;</item>
/// <item>a timeout faults with <see cref="TimeoutException"/> (the REST layer maps it to 504).</item>
/// </list>
/// It ALSO pins the MCP <c>get</c> AREA-READ path shapes documented on the tool
/// (<c>{path}/layoutAreas/</c> and <c>{path}/area/{Name}</c> — <c>McpMeshPlugin.Get</c>):
/// both routed through <see cref="MeshOperations.Get"/>, which must dispatch them to the
/// node hub's layoutAreas listing and to the same settled RenderArea pipeline respectively
/// (live defect: both returned "Not found: …" for every node — the router never recognised
/// the documented segments).
/// Leak coverage: <see cref="MonolithMeshTestBase.DisposeAsync"/> fails any test in this class
/// that leaves the render subscription (or its SubscribeRequest callback) pending past the
/// quiescing budget — RenderArea's <c>Finally(stream.Dispose)</c> is what keeps that green.
/// </summary>
public class RenderAreaOperationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    private async Task<string> SeedMarkdownNodeAsync(string marker)
    {
        var id = $"render-{Guid.NewGuid():N}"[..20];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Render Fixture",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = $"# {marker}" }
        }).Should().Emit();
        return $"{TestPartition}/{id}";
    }

    [Fact(Timeout = 60000)]
    public async Task RenderArea_ExplicitArea_ReturnsWireAreasAndData()
    {
        var path = await SeedMarkdownNodeAsync("explicit-area");

        var result = await new MeshOperations(Mesh).RenderArea($"@{path}", "Overview")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

        result.Should().NotStartWith("Error").And.NotStartWith("Not found");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.TryGetProperty("areas", out var areas).Should().BeTrue(
            "the frame must be the wire {areas,data} EntityStore snapshot");
        root.TryGetProperty("data", out _).Should().BeTrue(
            "the frame must carry the data collection alongside areas");

        // InstanceCollection keys ride JSON-ENCODED on the wire (InstanceCollectionConverter):
        // "Overview" arrives as the property "\"Overview\"" — the exact shape the gRPC client folds.
        areas.TryGetProperty("\"Overview\"", out var overview).Should().BeTrue(
            "the requested area's control must have materialised in the returned frame");
        overview.TryGetProperty("$type", out _).Should().BeTrue(
            "controls must keep their $type discriminator so the client renders without translation");
    }

    [Fact(Timeout = 60000)]
    public async Task RenderArea_DefaultArea_CarriesIndirectionAndResolvedControl()
    {
        var path = await SeedMarkdownNodeAsync("default-area");

        var result = await new MeshOperations(Mesh).RenderArea($"@{path}")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

        result.Should().NotStartWith("Error").And.NotStartWith("Not found");
        using var doc = JsonDocument.Parse(result);
        var areas = doc.RootElement.GetProperty("areas");

        // The base frame statically seeds areas[""] = NamedAreaControl(resolvedArea) …
        areas.TryGetProperty("\"\"", out var indirection).Should().BeTrue(
            "a default-area subscription's frame must carry the areas[\"\"] indirection");
        var resolvedArea =
            (indirection.TryGetProperty("area", out var a) ? a
                : indirection.GetProperty("Area")).GetString();
        resolvedArea.Should().NotBeNullOrEmpty("the indirection must point at the resolved default area");

        // … and RenderArea must NOT return that shell alone: the resolved area's own control
        // must have rendered too, otherwise the SSR first paint would be an empty indirection.
        areas.TryGetProperty(JsonSerializer.Serialize(resolvedArea), out _).Should().BeTrue(
            $"the resolved default area '{resolvedArea}' must have materialised in the frame");
    }

    [Fact(Timeout = 60000)]
    public async Task RenderArea_UnknownPath_ReturnsNotFound()
    {
        var result = await new MeshOperations(Mesh)
            .RenderArea($"@{TestPartition}/does-not-exist-{Guid.NewGuid():N}", "Overview", timeoutSeconds: 20)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

        result.Should().StartWith("Not found",
            "an unresolvable path must surface the clean sentinel, not hang or fault");
    }

    /// <summary>
    /// A URL-shaped path whose trailing segment is no node resolves to the closest ancestor with
    /// the segment as remainder — the same split the Blazor AreaPage applies. The framework then
    /// renders its visible "Area not found" placeholder control at that area key, so the caller
    /// gets the SAME error UI the portal URL would show — a clean frame, never a hang.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RenderArea_UnknownRemainderSegment_RendersAreaNotFoundPlaceholder()
    {
        var result = await new MeshOperations(Mesh)
            .RenderArea($"@{TestPartition}/no-such-area-{Guid.NewGuid():N}"[..30], timeoutSeconds: 30)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

        result.Should().NotStartWith("Error");
        result.Should().Contain("Area not found",
            "an unknown area renders the framework's visible placeholder — portal-URL parity");
    }

    /// <summary>
    /// MCP `get {path}/layoutAreas/` — the documented listing of layout areas on the node —
    /// must return the area definitions declared on the node's hub configuration (a Markdown
    /// node declares Overview/Edit/… via <c>AddMarkdownViews</c>), not "Not found".
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Get_LayoutAreasRoute_ReturnsDeclaredAreaNames()
    {
        var path = await SeedMarkdownNodeAsync("layoutareas-route");

        var result = await new MeshOperations(Mesh).Get($"@{path}/layoutAreas/")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

        result.Should().NotStartWith("Not found").And.NotStartWith("Error");
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array,
            "the layoutAreas listing is a JSON array of LayoutAreaDefinition");
        var areaNames = doc.RootElement.EnumerateArray()
            .Select(e => (e.TryGetProperty("area", out var a) ? a : e.GetProperty("Area")).GetString())
            .ToList();
        areaNames.Should().Contain("Overview").And.Contain("Edit",
            because: "the listing must come from the node hub's declared area definitions");
    }

    /// <summary>The docs show a trailing slash, but the bare form must route identically.</summary>
    [Fact(Timeout = 60000)]
    public async Task Get_LayoutAreasRoute_NoTrailingSlash_RoutesIdentically()
    {
        var path = await SeedMarkdownNodeAsync("layoutareas-bare");

        var result = await new MeshOperations(Mesh).Get($"@{path}/layoutAreas")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

        result.Should().NotStartWith("Not found").And.NotStartWith("Error");
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    /// <summary>
    /// MCP `get {path}/area/{Name}` — the documented rendered-payload read — must return the
    /// SETTLED wire frame (same materialised-control wait as RenderArea, never the base-frame
    /// shell), not "Not found".
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Get_AreaRoute_ReturnsMaterializedControlPayload()
    {
        var path = await SeedMarkdownNodeAsync("area-route");

        var result = await new MeshOperations(Mesh).Get($"@{path}/area/Overview")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

        result.Should().NotStartWith("Not found").And.NotStartWith("Error");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.TryGetProperty("areas", out var areas).Should().BeTrue(
            "the area payload must be the wire {areas,data} frame");
        areas.TryGetProperty("\"Overview\"", out var overview).Should().BeTrue(
            "the requested area's control must have materialised (no base-frame shell)");
        overview.TryGetProperty("$type", out _).Should().BeTrue(
            "controls keep their $type discriminator on the wire");
    }

    /// <summary>
    /// The pre-existing `get` path shapes must keep working around the new area routes:
    /// the node itself, children (`/*`), `data/` (default data reference = the node), and
    /// `schema/`.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Get_ExistingPathShapes_KeepWorking()
    {
        var path = await SeedMarkdownNodeAsync("regression-shapes");
        var ops = new MeshOperations(Mesh);

        var node = await ops.Get($"@{path}")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);
        node.Should().Contain("\"nodeType\":\"Markdown\"", "the plain node read must return the node");

        var children = await ops.Get($"@{TestPartition}/*")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);
        using (var childrenDoc = JsonDocument.Parse(children))
            childrenDoc.RootElement.ValueKind.Should().Be(JsonValueKind.Array, "children listing stays an array");

        var data = await ops.Get($"@{path}/data/")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);
        data.Should().NotStartWith("Not found").And.NotStartWith("Error");

        var schema = await ops.Get($"@{path}/schema/")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);
        schema.Should().NotStartWith("Not found").And.NotStartWith("Error");
    }

    [Fact(Timeout = 60000)]
    public async Task RenderArea_Timeout_FaultsWithTimeoutException()
    {
        var path = await SeedMarkdownNodeAsync("timeout");

        // A zero budget elapses before any frame can arrive — deterministic. Materialize folds
        // the OnError into a value so the assertion is reactive (try/catch around an awaited
        // FirstAsync can miss an Rx OnError).
        var notification = await new MeshOperations(Mesh)
            .RenderArea($"@{path}", "Overview", timeoutSeconds: 0)
            .Materialize()
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask(Ct);

        notification.Kind.Should().Be(NotificationKind.OnError,
            "an elapsed budget must FAULT the observable — the REST layer maps it to a 504 JSON error");
        notification.Exception.Should().BeOfType<TimeoutException>();
    }
}
