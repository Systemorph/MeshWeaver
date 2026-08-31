using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 <b>A layout area must be able to open a thread BESIDE the page, not only instead of it.</b>
///
/// <para>The framework has carried the whole side-panel navigation chain for a while —
/// <c>NavigationRequest.Target</c>, the portal handler that forwards it, the
/// <c>SidePanelNavigationRequested</c> event, and the layout that opens the chat panel on it. But
/// <see cref="LayoutAreaHost"/> exposed only <c>NavigateTo(uri, forceLoad, replace)</c>, which
/// posts the request with no target — so server-side area code could reach the chain's every link
/// EXCEPT the first one, and an embedded course composer had no choice but to navigate the whole
/// page to the thread it created. A learner mid-exercise lost the exercise (AgenticBusiness
/// lesson 3, reported 2026-08-31): the chat "took over the entire screen".</para>
///
/// <para>Added as a NEW method rather than an optional parameter on the existing one: adding a
/// parameter replaces the method's signature, and compiled module DLLs calling
/// <c>NavigateTo(string, bool, bool)</c> would throw <c>MissingMethodException</c> at runtime —
/// the exact image/module atomicity hazard the plugins repo documents.</para>
/// </summary>
public class NavigateToSidePanelTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string SendView = nameof(SendView);

    /// <summary>What the click posted back to the subscriber — completed by the client handler.</summary>
    private readonly ReplaySubject<NavigationRequest> received = new(1);

    private static IObservable<UiControl?> Send(LayoutAreaHost host, RenderingContext ctx) =>
        Observable.Return<UiControl?>(
            Controls.Stack.WithView(
                Controls.Button("open")
                    .WithClickAction(click =>
                    {
                        click.Host.NavigateToSidePanel("/acme/_Thread/t-1");
                        return Task.CompletedTask;
                    }),
                "Open"));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout.WithView(SendView, Send));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddLayoutClient(d => d)
            .WithHandler<NavigationRequest>((_, delivery) =>
            {
                received.OnNext(delivery.Message);
                return delivery.Processed();
            });

    [HubFact]
    public async Task AClick_CanOpenAPathInTheSidePanel_InsteadOfNavigatingThePage()
    {
        var reference = new LayoutAreaReference(SendView);
        var client = GetClient();
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        await stream.GetControlStream($"{reference.Area}/Open")
            .Should().Within(10.Seconds()).Match(x => x != null, "the button must render first");

        client.Post(
            new ClickedEvent($"{reference.Area}/Open", stream.StreamId),
            o => o.WithTarget(CreateHostAddress()));

        var request = await received
            .Should().Within(10.Seconds()).Emit("the click posts a NavigationRequest to the subscriber");

        request.Uri.Should().Be("/acme/_Thread/t-1");
        request.Target.Should().Be("SidePanel",
            "an untargeted request navigates the whole page — the take-over this API exists to avoid");
    }
}
