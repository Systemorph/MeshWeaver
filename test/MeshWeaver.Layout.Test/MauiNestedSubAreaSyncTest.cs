using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Deterministic repro for the <c>NativeMauiRendering.md</c> "keystone" claim — that a remote framework
/// area renders its TOP control but its NESTED sub-areas never resolve across the sync boundary (the native
/// MAUI client reads a synced copy via <c>GetRemoteStream</c> and a sub-area read never travels back to the
/// owning hub to trigger a lazy render).
///
/// <para><see cref="MauiViewDataPathTest"/> already proves ONE level of synchronous child areas DOES sync
/// over the remote stream. This pins the harder cases the keystone is actually about: (1) a container nested
/// inside a container (two levels deep), and (2) a sub-area rendered ASYNCHRONOUSLY (after the initial Full,
/// the way a data-driven area emits via <c>UpdateArea</c>). All reads go through the SAME
/// <c>GetRemoteStream</c> + <c>GetControlStream</c> path the MAUI <c>NamedAreaView</c>/<c>RenderArea</c> use,
/// so a pass here means the native pack would render these sub-areas; a hang (5s timeout) reproduces the
/// keystone with no Xcode / MAUI runtime.</para>
/// </summary>
public class MauiNestedSubAreaSyncTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string DeepView = nameof(DeepView);
    private const string AsyncView = nameof(AsyncView);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            // (1) A container holding a NESTED container (two levels deep): DeepView → Inner → Leaf.
            .AddLayout(layout => layout
                .WithView(DeepView, (_, _) => Observable.Return<UiControl>(
                    Controls.Stack
                        .WithView(Controls.Label("Top leaf"), "Top")
                        .WithView(
                            Controls.Stack.WithView(Controls.Label("Deep leaf"), "Leaf"),
                            "Inner")))
                // (2) A sub-area emitted ASYNCHRONOUSLY — the child's renderer stream emits on the thread
                //     pool, so the control lands via a post-Full UpdateArea, the way a data-driven area does.
                .WithView(AsyncView, (_, _) => Observable.Return<UiControl>(
                    Controls.Stack
                        .WithView(
                            Observable.Return<UiControl?>(Controls.Label("Async leaf"))
                                .ObserveOn(System.Reactive.Concurrency.TaskPoolScheduler.Default),
                            "Async"))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task DeepNestedContainer_ResolvesOverRemoteStream()
    {
        var reference = new LayoutAreaReference(DeepView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        // Top container + its first-level children (the case MauiViewDataPathTest already covers).
        var root = await stream.GetControlStream(reference.Area!)
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is IContainerControl);
        ((IContainerControl)root!).Areas.Should().HaveCount(2);

        var inner = await stream.GetControlStream($"{DeepView}/Inner")
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is IContainerControl);

        // The KEYSTONE assertion: the SECOND-level leaf, reached only by recursing into the nested
        // container, must resolve over the same synced remote stream.
        var deepLeaf = await stream.GetControlStream($"{DeepView}/Inner/Leaf")
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is LabelControl);
        ((LabelControl)deepLeaf!).Data!.ToString().Should().Contain("Deep leaf");
    }

    [HubFact]
    public async Task AsyncSubArea_ResolvesOverRemoteStream()
    {
        var reference = new LayoutAreaReference(AsyncView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        await stream.GetControlStream(reference.Area!)
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is IContainerControl);

        // A sub-area whose control arrives AFTER the initial Full (data-driven shape) must still reach the
        // synced remote copy via the post-Full UpdateArea emission.
        var asyncLeaf = await stream.GetControlStream($"{AsyncView}/Async")
            .Where(c => c is not null)
            .Should().Within(5.Seconds()).Match(c => c is LabelControl);
        ((LabelControl)asyncLeaf!).Data!.ToString().Should().Contain("Async leaf");
    }
}
