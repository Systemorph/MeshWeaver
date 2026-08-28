using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Deterministic repro for <b>the recycle that strands its own audience</b> — the defect behind
/// <c>FutuReAnalysisTest.EuropeRe_AnnualReport_EmbeddedCharts_ShouldRenderViaPathResolution</c>
/// (issues #2533 / #2551) and, far more importantly, behind every framework-identity bump.
///
/// <para><b>The mechanism.</b> A per-instance hub that came up while its NodeType was still
/// compiling binds the compile-in-progress overlay, and
/// <c>NodeTypeEnrichmentHelpers.WithOverlaySelfHeal</c> then posts a self-<see cref="DisposeRequest"/>
/// the moment the type reaches a usable build — "the next access re-enriches against the settled
/// type". <b>An already-open subscription is not a next access.</b> The owner tears down, and the
/// one message that would have told the subscriber — <c>StreamEndedEvent</c> — is deliberately
/// suppressed while the owning hub is tearing down (<c>JsonSynchronizationStream</c>: "a hub must
/// speak only for itself, and never while it is dying"). The two recoveries that comment delegates
/// to cannot fire either: the recycle re-arm needs an in-flight <c>SubscribeRequest</c> to be
/// NACKed, and the change-feed latch needs a WRITE — and, as the same file says 600 lines earlier,
/// "a recycle IS NOT A WRITE". So the subscriber gets no frame, no completion and no error, and
/// holds its last snapshot forever.</para>
///
/// <para><b>Why it is a production risk and not a test nuisance.</b> A framework-identity bump
/// recompiles EVERY dynamic NodeType fleet-wide (AGENTS.md), so on that deploy every instance hub
/// takes the cold-compile path, crosses the 5 s overlay grace, is overlaid and is then recycled —
/// and every user with a page open at that moment is left on the progress page until they reload.
/// The 37.7 s of total silence in run 33150985489's trace is exactly this: the compile SUCCEEDED
/// 43 s before the test gave up.</para>
///
/// <para><b>What this test pins</b>, with no Roslyn and no overlay: a client holding a LIVE layout
/// area subscription must re-converge on the RE-ACTIVATED hub after that hub is recycled by a bare
/// <see cref="DisposeRequest"/> — the byte-for-byte idiom of the self-heal watcher — with NOBODY
/// writing the node (the MCP <c>Recycle</c> tool publishes a <c>MeshChangeEvent</c> before its
/// dispose and is therefore NOT affected; the self-heal posts the dispose alone and is). The area
/// renders a per-activation marker, so "the subscriber saw the new activation" is a fact about the
/// rendered content, never about timing.</para>
/// </summary>
public class RecycleStrandsLiveSubscriberTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/RecycleAnnounce";
    private const string MarkerArea = "ActivationMarker";
    private const string MarkerPrefix = "ACTIVATION_";

    /// <summary>
    /// One counter per TEST INSTANCE — never static (AGENTS.md → "No static collections"): its
    /// lifetime is this test's mesh, so nothing bleeds into the next test class.
    /// </summary>
    private int activations;

    // The recycle round-trip is a teardown plus a fresh activation plus a re-subscribe; a
    // REGRESSION here is a wait that runs out, so the per-test watchdogs must outlast the two
    // render budgets below or the failure reads as a harness abort instead of the assertion it is.
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(120);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(240);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            // A STATIC NodeType (no Roslyn, no compile lifecycle) whose one area renders the
            // ORDINAL of the activation serving it. That ordinal is what makes "the subscriber is
            // still bound to the dead activation" observable: the pre-recycle and post-recycle
            // renders are different strings, so the assertion never has to guess.
            .AddMeshNodes(MeshNode.FromPath(TypePath) with
            {
                Name = "RecycleAnnounce",
                State = MeshNodeState.Active,
                HubConfiguration = config =>
                {
                    var ordinal = Interlocked.Increment(ref activations);
                    return config.AddLayout(layout => layout.WithView(
                        MarkerArea,
                        (LayoutAreaHost _, RenderingContext _) =>
                            Observable.Return<UiControl?>(Controls.Html($"{MarkerPrefix}{ordinal}"))));
                }
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 240_000)]
    public async Task LiveSubscriber_ReConverges_WhenItsOwnerIsRecycled()
    {
        var instancePath = $"type/RecycleAnnounce{Guid.NewGuid():N}";

        await MeshService.CreateNode(MeshNode.FromPath(instancePath) with
        {
            Name = "Instance",
            NodeType = TypePath,
            State = MeshNodeState.Active,
            Content = JsonSerializer.SerializeToElement(new { title = "instance" }),
        }).Should().Emit();

        // 1. A LIVE subscription — the page a user has open, and the shape the FutuRe test uses.
        var client = GetClient();
        var reference = new LayoutAreaReference(MarkerArea);
        var controls = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(instancePath), reference)
            .GetControlStream(reference.Area!);

        var first = await controls.Should().Within(60.Seconds())
            .Match(c => c is HtmlControl h
                && h.Data?.ToString()?.StartsWith(MarkerPrefix, StringComparison.Ordinal) == true);
        var firstMarker = ((HtmlControl)first!).Data!.ToString()!;
        Output.WriteLine($"Live subscription is bound to {firstMarker}.");

        // 2. THE RECYCLE. A bare DisposeRequest and nothing else — exactly what
        //    NodeTypeEnrichmentHelpers.WithOverlaySelfHeal's `recycle:` callback posts. No node
        //    write, no change-feed publish: those are what every OTHER recycle path pairs with the
        //    dispose, and their absence here is the whole point.
        client.Post(new DisposeRequest(), o => o.WithTarget(new Address(instancePath)));
        Output.WriteLine($"Posted DisposeRequest to {instancePath}.");

        // 3. THE CONTRACT. The SAME subscription — never re-opened by the caller — must land on the
        //    re-activated hub. Before the fix nothing at all reaches it: no frame, no completion,
        //    no error, and this wait runs its full budget out while the healthy new activation sits
        //    unasked-for (the prod symptom: a page stuck on the compile-progress overlay).
        var healed = await controls.Should().Within(60.Seconds())
            .Match(c => c is HtmlControl h
                    && h.Data?.ToString()?.StartsWith(MarkerPrefix, StringComparison.Ordinal) == true
                    && !string.Equals(h.Data.ToString(), firstMarker, StringComparison.Ordinal),
                "a recycle must reach the subscribers the recycled hub was serving — the self-heal "
                + "exists FOR the viewer who is on the degraded page, and a teardown it is never "
                + "told about leaves that viewer on it forever");

        Output.WriteLine($"Live subscription re-converged on {((HtmlControl)healed!).Data}.");
        activations.Should().BeGreaterThan(1,
            "the re-ask must have driven a FRESH activation of the instance hub");
    }
}
