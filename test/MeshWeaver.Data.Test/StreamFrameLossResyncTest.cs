using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Deterministic repro + regression guard for MID-BURST FRAME LOSS on a sync-stream mirror
/// (issue #1081).
///
/// <para>The data-sync protocol is a delta protocol over a transport that is at-most-once:
/// an Orleans memory stream silently drops a frame published before the subscriber's stream
/// subscription attached, and can drop under pressure. Pre-fix, a lost mid-burst frame was
/// INVISIBLE: later patches kept applying cleanly on top (they touch other entities/areas),
/// so the mirror tracked the owner forever at a constant deficit with no error anywhere —
/// measured in the Orleans suite as the compile-error-overlay Full (v6) vanishing while
/// v1–v5, v7, v8 all arrived, leaving the page on "awaiting first data" for the full 45 s
/// detector.</para>
///
/// <para>The fix chains consecutive frames: every <see cref="DataChangedEvent"/> carries
/// <c>BasedOnVersion</c> — the version of the frame the owner SENT immediately before it
/// (<c>JsonSynchronizationStream.ToDataChanged</c>). The mirror detects the gap (a Patch
/// chained onto a version it never applied) and requests a fresh authoritative snapshot
/// (<c>SynchronizationStream.UpdateStream</c> → <c>RequestFreshSnapshot</c>, storm-gated).
/// This test eats exactly ONE mid-burst Patch on the client's inbound pipeline — the wire
/// loss — and asserts the mirror still converges to the owner's complete state. FAILS
/// pre-fix (the mirror never materialises the eaten write; the wait times out).</para>
/// </summary>
public class StreamFrameLossResyncTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>1 after the pipeline has eaten its one frame. Instance field — per-test lifetime.</summary>
    private int droppedPatches;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))))
            // THE WIRE LOSS: eat exactly one mid-burst Patch frame as it leaves the owner —
            // an Ignored post is dropped by ScheduleNotify (NotifyAsync early-returns on any
            // non-Submitted state), exactly the shape of the at-most-once transport losing a
            // published frame between owner and mirror.
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
                d.Message is DataChangedEvent { ChangeType: ChangeType.Patch }
                && Interlocked.Exchange(ref droppedPatches, 1) == 0
                    ? d.Ignored()
                    : next.Invoke(d)));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    [HubFact]
    public async Task MirrorDetectsLostMidBurstFrame_AndResyncsToOwnerState()
    {
        var host = GetHost();
        var client = GetClient();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;

        var clientStream = client.GetWorkspace()
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));

        // Base Full applied — the mirror is live before the burst starts.
        await clientStream.Should().Within(10.Seconds()).Emit();

        // A burst of three sequential owner writes → three Patch frames. The pipeline above
        // eats the FIRST one (doc-1); doc-2/doc-3's patches chain onto the eaten frame's
        // version, which is what makes the loss DETECTABLE.
        for (var i = 1; i <= 3; i++)
            host.Post(
                new DataChangeRequest().WithUpdates(new MyData($"doc-{i}", $"value-{i}")),
                o => o.WithAccessContext(accessService.Context!));

        // The mirror must converge to ALL THREE docs — including the one whose frame the wire
        // ate. Pre-fix this waits forever (doc-1 is never re-sent; later patches apply cleanly),
        // so the bounded wait turns the silent deficit into a loud red.
        await clientStream
            .Where(ci => ci.Value?.Collections.GetValueOrDefault(collectionName) is { } coll
                && coll.Instances.Values.OfType<MyData>()
                    .Count(d => d.Text?.StartsWith("value-") == true) == 3)
            .Take(1)
            .Should().Within(15.Seconds())
            .Emit();

        // Prove the wire really ate a frame — otherwise this test demonstrates nothing.
        Volatile.Read(ref droppedPatches).Should().Be(1,
            "the delivery pipeline must have dropped exactly one mid-burst Patch frame");
    }
}
