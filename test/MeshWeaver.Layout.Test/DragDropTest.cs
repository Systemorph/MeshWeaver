using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests for the generic <see cref="DraggableControl"/> / <see cref="DropTargetControl"/> pair:
/// serialization, per-instance content-area wiring, and the drop round-trip that invokes the
/// server-side drop handler.
/// </summary>
public class DragDropTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string DraggableView = nameof(DraggableView);
    private const string DropView = nameof(DropView);

    // Instance state (xUnit builds a fresh test instance per test), so no static reset needed.
    private readonly List<object?> dropped = [];

    private UiControl DraggableViewDefinition(LayoutAreaHost host, RenderingContext ctx)
        => Controls.Draggable(Controls.Html("Drag me"), "item-1");

    private UiControl DropViewDefinition(LayoutAreaHost host, RenderingContext ctx)
        => Controls.DropTarget(Controls.Html("Drop zone"))
            .WithDropAction(context => dropped.Add(context.Payload));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout
                .WithView(DraggableView, DraggableViewDefinition)
                .WithView(DropView, DropViewDefinition));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [HubFact]
    public void Draggable_Serializes_WithTypeDiscriminatorAndPayload()
    {
        var host = GetHost();
        var serialized = JsonSerializer.Serialize(
            Controls.Draggable(Controls.Html("x"), "payload-42"),
            host.JsonSerializerOptions);

        serialized.Should().Contain("\"$type\":\"DraggableControl\"");
        serialized.Should().Contain("\"payload\":\"payload-42\"");
    }

    [HubFact]
    public void DropTarget_Serializes_WithTypeDiscriminatorAndAcceptTypes()
    {
        var host = GetHost();
        var serialized = JsonSerializer.Serialize(
            Controls.DropTarget(Controls.Html("zone")).WithAcceptTypes("card"),
            host.JsonSerializerOptions);

        serialized.Should().Contain("\"$type\":\"DropTargetControl\"");
        serialized.Should().Contain("\"acceptTypes\":\"card\"");
    }

    [HubFact]
    public void DropEvent_Serializes_WithPayload()
    {
        var host = GetHost();
        var serialized = JsonSerializer.Serialize(
            new DropEvent(DropView, "stream-1") { Payload = "item-1" },
            host.JsonSerializerOptions);

        serialized.Should().Contain("\"$type\":\"DropEvent\"");
        serialized.Should().Contain("\"payload\":\"item-1\"");
    }

    [HubFact]
    public void DropAction_IsServerSideAndNotSerialized()
    {
        var host = GetHost();
        var serialized = JsonSerializer.Serialize(
            Controls.DropTarget(Controls.Html("zone")).WithDropAction(_ => { }),
            host.JsonSerializerOptions);

        // The delegate is internal server-side state — it must never leak to the wire.
        serialized.Should().NotContain("dropAction");
    }

    [Fact]
    public async Task Draggable_RendersContentIntoPerInstanceSubArea()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(DraggableView));

        var control = await area
            .GetControlStream(DraggableView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var draggable = control.Should().BeOfType<DraggableControl>().Subject;
        draggable.Payload.Should().Be("item-1");
        draggable.ContentArea.Should().NotBeNull();
        draggable.ContentArea!.Area.Should().Be($"{DraggableView}/Content");

        // The wrapped Html must be rendered into that per-instance sub-area.
        var content = await area
            .GetControlStream($"{DraggableView}/Content")
            .Should().Within(5.Seconds()).Match(x => x is not null);

        content.Should().BeOfType<HtmlControl>();
    }

    [Fact]
    public async Task Drop_InvokesDropActionWithPayload()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(DropView));

        // Wait for the drop target to render.
        await stream
            .GetControlStream(DropView)
            .Should().Within(10.Seconds()).Match(x => x is DropTargetControl);

        // Simulate a client-side drop: the drop target posts DropEvent carrying the dragged payload.
        client.Post(
            new DropEvent(DropView, stream.StreamId) { Payload = "item-1" },
            o => o.WithTarget(CreateHostAddress()));

        // The server-side drop handler should receive the payload (probe until it lands).
        await Observable.Interval(50.Milliseconds())
            .StartWith(0L)
            .Select(_ => dropped.ToArray())
            .Where(d => d.Contains("item-1"))
            .Should().Within(5.Seconds()).Emit();

        dropped.Should().Contain("item-1");
    }
}
