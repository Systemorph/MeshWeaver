using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 <b>A layout area can name the viewer it is rendering for — and two viewers of the SAME area
/// get different answers.</b>
///
/// <para>This is the seam every per-tab decision hangs off. The stack is already per-tab all the
/// way down: a Blazor circuit is one tab, the portal hub is keyed on the circuit id, and an area
/// subscription is keyed <c>(Subscriber, StreamId)</c> — so one node rendered into two tabs is two
/// <see cref="LayoutAreaHost"/>s with two different subscribers. Then a value is written into a mesh
/// NODE, where the isolation ends: a node is shared by everything that can read it, including the
/// same person's other tab (<c>Doc/Architecture/PerTabSessionState</c>, MeshWeaver#3060).</para>
///
/// <para><see cref="LayoutAreaHost.Viewer"/> is what lets state that means "<i>this viewer, right
/// now, in this window</i>" carry an ADDRESSEE onto such a node, so a consumer can act only on a
/// signal addressed to it. It is deliberately the same address, normalized the same way, that
/// <see cref="LayoutAreaHost.NavigateTo"/> posts to — a stamp written here and a command posted
/// there must name the same tab, or addressing would be a coin toss.</para>
/// </summary>
public class LayoutAreaViewerTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ViewerView = nameof(ViewerView);
    private const string ViewerArea = "Viewer";

    /// <summary>Renders the area's own viewer, so a subscriber can read back who the server thinks
    /// it is rendering for.</summary>
    private static IObservable<UiControl?> Viewer(LayoutAreaHost host, RenderingContext ctx) =>
        Observable.Return<UiControl?>(
            Controls.Stack.WithView(
                Controls.Label(host.Viewer?.ToString() ?? "<none>"), ViewerArea));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout.WithView(ViewerView, Viewer));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private async Task<string> RenderedViewerFor(IMessageHub client)
    {
        var reference = new LayoutAreaReference(ViewerView);
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);
        var text = await stream.GetControlStream($"{reference.Area}/{ViewerArea}")
            .Should().Within(10.Seconds())
            .Match(c => c is LabelControl { Data: string s } && s != "<none>",
                "the area must render, and must know its subscriber");
        return (string)((LabelControl)text!).Data!;
    }

    /// <summary>The area renders for the hub that subscribed to it, and says so.</summary>
    [HubFact]
    public async Task AnAreaKnowsWhichViewerItIsRenderingFor()
    {
        var client = GetClient();
        (await RenderedViewerFor(client)).Should().Be(client.Address.ToString(),
            "the viewer IS the subscriber — in the portal, the tab's own portal hub");
    }

    /// <summary>
    /// 🚨 <b>The property the whole per-tab scheme rests on.</b> Two subscribers to the SAME area of
    /// the SAME host are two renders with two different viewers. If they ever agreed, an addressee
    /// stamped from one tab would match the other and "addressed" would mean nothing — the exact
    /// cross-talk of MeshWeaver#3060, one layer lower.
    /// </summary>
    [HubFact]
    public async Task TwoSubscribersToOneArea_EachRenderForThemselves()
    {
        var first = GetClient();
        var second = GetClient();

        var viewerOfFirst = await RenderedViewerFor(first);
        var viewerOfSecond = await RenderedViewerFor(second);

        viewerOfFirst.Should().Be(first.Address.ToString());
        viewerOfSecond.Should().Be(second.Address.ToString());
        viewerOfFirst.Should().NotBe(viewerOfSecond,
            "two viewers of one area must be distinguishable, or nothing can be addressed to one of them");
    }
}
