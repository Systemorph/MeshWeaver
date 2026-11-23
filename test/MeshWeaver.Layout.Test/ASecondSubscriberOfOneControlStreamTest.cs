using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// One control-stream observable waited on TWICE: each subscription is replayed the current area.
/// The "first frame" flag of <c>GetStream</c> used to be shared by all subscribers of one call, so
/// a late subscriber, replayed a patch frame that updated a DIFFERENT area, saw nothing at all —
/// the second wait of TypedPageIndexRailTest's re-shape test timed out on a settled page.
/// </summary>
public class ASecondSubscriberOfOneControlStreamTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "TwoAreaProbe";
    private readonly Subject<UiControl> Dynamic = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(Area, (_, _) => Controls.Stack
                .WithView(Controls.Html("static"), "Static")
                .WithView((_, _) => Dynamic.StartWith(Controls.Html("v0")), "Dynamic")));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [HubFact]
    public async Task ALateSubscriber_IsReplayedTheAreaEvenWhenTheLastFrameUpdatedAnotherArea()
    {
        var ct = System.Threading.CancellationToken.None;
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        var root = await stream.GetControlStream(Area).Should().Within(10.Seconds())
            .Match(c => c is IContainerControl { Areas.Count: 2 }, cancellationToken: ct);
        var keys = ((IContainerControl)root!).Areas.Select(a => a.Area!.ToString()!).ToArray();
        var staticKey = keys[0];
        var dynamicKey = keys[1];

        var staticArea = stream.GetControlStream(staticKey);
        await staticArea.Should().Within(10.Seconds()).Match(c => c is HtmlControl, cancellationToken: ct);

        // The LAST frame now updates the dynamic area, not the static one.
        Dynamic.OnNext(Controls.Html("v1"));
        await stream.GetControlStream(dynamicKey).Should().Within(10.Seconds())
            .Match(c => c is HtmlControl h && h.Data?.ToString() == "v1", cancellationToken: ct);

        await staticArea.Should().Within(10.Seconds())
            .Match(c => c is HtmlControl, "a second subscription is replayed the current area", cancellationToken: ct);
    }
}
