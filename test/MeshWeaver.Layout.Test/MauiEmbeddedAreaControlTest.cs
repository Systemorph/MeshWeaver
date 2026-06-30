using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Deterministic data-path repro for the native MAUI RECURSIVE EMBED — the cross-host / <c>@@/address/area</c>
/// case. A framework area ("Home") embeds a <see cref="LayoutAreaControl"/> child pointing at another area
/// ("Embedded") carried with an explicit Address — the exact shape the <c>@@/address/area</c> markdown embed
/// and a cross-host composer produce. The native <c>LayoutAreaControlView</c> renders such a child by opening
/// its OWN remote stream (<c>GetRemoteStream(address, reference)</c>) and <c>RenderArea</c>-ing it, recursing
/// through the same view map. This pins that path with NO Xcode/MAUI runtime: open Home over the remote
/// stream, find the LayoutAreaControl child, then open the embedded area EXACTLY as the view does and assert
/// its content + a NESTED sub-area resolve — proving the embed isn't a dead placeholder.
/// </summary>
public class MauiEmbeddedAreaControlTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Home = nameof(Home);
    private const string Embedded = nameof(Embedded);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                // Home embeds a LayoutAreaControl child carrying the host Address + a reference to "Embedded"
                // — the LayoutAreaControlView cross-host branch (what @@/address/area resolves to).
                .WithView(Home, (_, _) => Observable.Return<UiControl>(
                    Controls.Stack
                        .WithView(Controls.Label("home leaf"), "Leaf")
                        .WithView(Controls.LayoutArea(CreateHostAddress(), Embedded), "Embed")))
                // Embedded is itself a container with a NESTED sub-area, so the embed must resolve recursively.
                .WithView(Embedded, (_, _) => Observable.Return<UiControl>(
                    Controls.Stack
                        .WithView(Controls.Stack.WithView(Controls.Label("deep embedded leaf"), "Deep"), "Inner"))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task EmbeddedLayoutAreaControl_OpensItsOwnStream_AndResolvesNestedContent()
    {
        var workspace = GetClient().GetWorkspace();
        var homeStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Home));

        // Home is a container; its "Embed" child is a LayoutAreaControl carrying the embedded area's coordinates.
        await homeStream.GetControlStream(Home)
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is IContainerControl);

        var embed = await homeStream.GetControlStream($"{Home}/Embed")
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is LayoutAreaControl);
        var embedControl = (LayoutAreaControl)embed!;
        embedControl.Reference.Area.Should().Be(Embedded);

        // Exactly what LayoutAreaControlView does for the cross-host branch: open the embed's OWN remote
        // stream (string→Address implicit conversion, mirroring the view's `Model.Address?.ToString()`).
        Address embeddedAddress = embedControl.Address?.ToString() ?? "";
        var embeddedStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            embeddedAddress, embedControl.Reference);

        await embeddedStream.GetControlStream(Embedded)
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is IContainerControl);

        // The embed's NESTED sub-area must resolve over the embed's OWN stream (recursion through the view map).
        var deep = await embeddedStream.GetControlStream($"{Embedded}/Inner/Deep")
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is LabelControl);
        ((LabelControl)deep!).Data!.ToString().Should().Contain("deep embedded leaf");
    }
}
