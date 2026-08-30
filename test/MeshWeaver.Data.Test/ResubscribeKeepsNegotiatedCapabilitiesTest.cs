using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A RE-subscribe must declare the same negotiated wire capabilities as the initial subscribe.
///
/// <para><see cref="SubscribeRequest.AcceptsStringSplice"/> is the one negotiated capability of the
/// owner→subscriber fan-out (#1284/#1414): a changed string leaf travels as the changed span rather
/// than the whole new string, but ONLY for a subscriber that said it can apply one. The owner reads
/// that claim off the <see cref="SubscribeRequest"/> it uses to BUILD the server-side stream — and a
/// re-subscribe builds a fresh one whenever the owner cannot match it to a live stream: the
/// subscriber was evicted on the router's <c>TargetUnserved</c> verdict (#2620), the owner recycled,
/// or the per-subscriber sync hub was torn down.</para>
///
/// <para>So a capability declared on the initial subscribe and omitted on a re-subscribe is
/// <b>withdrawn for the rest of that mirror's life</b>, and nothing anywhere fails: the owner simply
/// goes back to whole-string <c>replace</c> frames, reinstating the per-subscriber quadratic on
/// exactly the paths that fire during a resync storm (memex-cloud, #2641). No assertion on the
/// FRAMES could catch it — a <c>replace</c> is a shape every subscriber understands. The only place
/// it is visible is the request itself, which is what this test reads.</para>
///
/// <para>The trigger is the deterministic wire loss <see cref="StreamFrameLossResyncTest"/> uses: eat
/// exactly one mid-burst Patch, and the mirror's gap detection answers with
/// <c>RequestFreshSnapshot()</c> — a second <see cref="SubscribeRequest"/> on the same stream. Before
/// the fix that request carried <c>AcceptsStringSplice = false</c> and this test is RED on its
/// second row.</para>
/// </summary>
public class ResubscribeKeepsNegotiatedCapabilitiesTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>1 after the pipeline has eaten its one frame. Instance field — per-test lifetime.</summary>
    private int droppedPatches;

    /// <summary>Every SubscribeRequest the subscriber posted, in order. Instance, never static.</summary>
    private readonly ConcurrentQueue<SubscribeRequest> subscribeRequests = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))))
            // THE WIRE LOSS: eat exactly one mid-burst Patch frame as it leaves the owner, so the
            // mirror detects the gap and posts the RE-subscribe this test is about.
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
                d.Message is DataChangedEvent { ChangeType: ChangeType.Patch }
                && Interlocked.Exchange(ref droppedPatches, 1) == 0
                    ? d.Ignored()
                    : next.Invoke(d)));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))))
            // Record every SubscribeRequest the SUBSCRIBER posts — the initial one and the
            // frame-loss resync — because the declaration is the only place a withdrawn
            // capability is observable at all.
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                if (d.Message is SubscribeRequest sr)
                    subscribeRequests.Enqueue(sr);
                return next.Invoke(d);
            }));

    [HubFact]
    public async Task TheFrameLossResubscribe_StillDeclaresStringSplice()
    {
        var host = GetHost();
        var client = GetClient();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;

        var clientStream = client.GetWorkspace()
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));

        await clientStream.Should().Within(10.Seconds()).Emit();

        for (var i = 1; i <= 3; i++)
            host.Post(
                new DataChangeRequest().WithUpdates(new MyData($"doc-{i}", $"value-{i}")),
                o => o.WithAccessContext(accessService.Context!));

        // Converged again — which is only possible via the resync, so the second SubscribeRequest
        // has certainly been delivered by the time this completes.
        await clientStream
            .Where(ci => ci.Value?.Collections.GetValueOrDefault(collectionName) is { } coll
                && coll.Instances.Values.OfType<MyData>()
                    .Count(d => d.Text?.StartsWith("value-") == true) == 3)
            .Take(1)
            .Should().Within(15.Seconds())
            .Emit();

        Volatile.Read(ref droppedPatches).Should().Be(1,
            "the delivery pipeline must have dropped exactly one mid-burst Patch frame — without the "
            + "loss there is no resync and this test would assert nothing");

        var requests = subscribeRequests.ToArray();
        requests.Should().HaveCountGreaterThan(1,
            "the mirror must have RE-subscribed after detecting the gap; with only the initial "
            + "subscribe there is no second request to compare and the test proves nothing");

        requests.Should().OnlyContain(r => r.AcceptsStringSplice,
            "every SubscribeRequest this assembly posts declares the capabilities of the applier it "
            + "ships — a re-subscribe that omits AcceptsStringSplice silently downgrades the owner's "
            + "fan-out to whole-string replaces for the rest of the mirror's life");
    }
}
