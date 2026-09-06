using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Json;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 THE STREAM LETS THE HUB GO — Systemorph/MeshWeaver#3321, step 3 of 3, the step that actually
/// reclaims the leak.
///
/// <para><b>What was measured.</b> Five heap dumps of a production replica (26 h uptime, 6.9 GB
/// working set, ~210 MB/h growth, 267 full collections freeing nothing) found 1 496
/// <c>MessageHub</c>s at <c>RunLevel=Dead</c> still reachable — and the referrer walk over all
/// 51 562 911 objects named exactly ONE holder: <c>SynchronizationStream&lt;T&gt;.Hub</c>, 1 485
/// <c>&lt;MeshNode&gt;</c> plus 11 <c>&lt;JsonElement&gt;</c>, one stream per corpse, exactly. No GC
/// root pointed at a dead hub directly. <c>HostedHubsCollection</c> had done its job (1 495 of 1 496
/// entries removed) and the hubs' Autofac scopes had been closed — but closing a scope does not null
/// the fields of an object that outlives it, so each corpse kept its own <c>ILifetimeScope</c> and
/// <c>TypeRegistry</c> alive at ~390 KB apiece.</para>
///
/// <para>🚨 <b>The split that decides what "fixing it" means.</b> Of those 1 496, only <b>11</b> sat
/// under a stream that had itself been disposed — <c>SynchronizationStream</c> total 8 461,
/// <c>disposed</c> 11. So <c>Dispose()</c> clearing the field, which is how this step is usually
/// described, would have reclaimed 11 of 1 496. The other 1 485 are hosted sub-hubs killed by their
/// PARENT's teardown while the stream that created them — owned by a workspace somewhere else — was
/// never told. Both halves are tested here, and the second is the one that carries the bytes.</para>
///
/// <para><b>Why this could not simply be done.</b> The predecessor of the current constructor nulled
/// <c>Hub</c> and documented that "every code path that touches Hub goes through
/// <c>TryGetActiveHub</c>". That method was private to one file while ~96 sites dereferenced a
/// property <see cref="ISynchronizationStream"/> declares NON-NULLABLE, so no consumer honoured it
/// and none could. It cost a production NRE inside <c>LayoutAreaHost</c>'s constructor during a
/// recycle window, which reached the subscriber as a TERMINAL <c>DeliveryFailure</c>: a page told
/// "this failed forever" instead of "ask again". Steps 1 (#3380, make liveness askable) and 2
/// (#3386, migrate the dereference sites) exist so that this step is no longer that change. See
/// <c>Doc/Architecture/StreamLivenessAndTheHubReference</c>.</para>
/// </summary>
public class StreamReleasesItsHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Empty;

    /// <summary>
    /// A data source, so <see cref="ToChangeItem_OnAReleasedStream_AnswersWithItsModelledAbsence"/>
    /// runs against a stream carrying the REAL <c>PatchEntityStore</c> reducer rather than a bare
    /// stream with no <c>PatchFunction</c> — which would answer <c>null</c> for a reason that has
    /// nothing to do with #3321, and so could not fail.
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds =>
                ds.WithType<MyData>(type => type.WithKey(instance => instance.Id))));

    private SynchronizationStream<Empty> CreateStream(IMessageHub host)
        => new(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            new ReduceManager<Empty>(host),
            null);

    /// <summary>
    /// Half one: a stream that is disposed normally lets its hub go.
    ///
    /// <para>🚨 The assertion is a <see cref="WeakReference"/>, not <c>Hub is null</c>, and that is
    /// the point. "The field reads null" is a statement about one field; "the object is
    /// unreachable" is the statement this issue is actually about, and it is the only one that
    /// would have gone red on the defect for the RIGHT reason. The hub reference is created and
    /// dropped inside a non-inlined helper so no JIT-visible local can keep it alive on the
    /// test's own frame.</para>
    /// </summary>
    [HubFact]
    public void ADisposedStream_ReleasesItsHub_SoTheHubBecomesCollectable()
    {
        var host = GetHost();
        var (stream, hubRef) = CreateDisposeAndWeaklyReference(host);

        stream.Hub.Should().BeNull(
            "the stream must drop the reference on disposal — until step 3 it kept the corpse and "
            + "everything the corpse had resolved reachable for the process's whole life");
        stream.TryGetHub().Should().BeNull("and the guarded accessor agrees");

        Collect();

        hubRef.IsAlive.Should().BeFalse(
            "the hub must be UNREACHABLE, not merely unreferenced by one field — 1 496 dead hubs "
            + "were held alive in production by exactly this reference and nothing else");
    }

    /// <summary>
    /// 🚨 Half two, and the half that carries 1 485 of the 1 496: the hub dies UNDER a stream that
    /// nobody disposes. This is the production shape — a <c>sync/</c> hub is a HOSTED hub, so a
    /// Blazor circuit ending, a <c>DisposeRequest</c> or a recycle disposes it through the parent's
    /// teardown, while the stream that created it is owned by a workspace elsewhere and is never
    /// notified. A step 3 that only cleared the field in <c>Dispose()</c> would leave this case
    /// exactly as it was, which is why it is tested separately rather than folded into the one
    /// above.
    /// </summary>
    [HubFact]
    public async Task AHubDyingUnderAnUndisposedStream_IsStillReleased()
    {
        var host = GetHost();
        var stream = CreateStream(host);
        var hub = stream.Hub;
        hub.Should().NotBeNull("precondition: the stream owns a live sync hub");

        // Kill the hub the way a parent teardown does — WITHOUT disposing the stream. This is the
        // very call Workspace.EvictClientSubscriptions makes.
        hub.Dispose();
        await hub.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .Timeout(30.Seconds())
            .Await(TestContext.Current.CancellationToken);

        stream.Hub.Should().BeNull(
            "the stream learns of its hub's death through the hook registered on the hub itself — "
            + "1 485 of the 1 496 corpses in the production dump were exactly this case, held by a "
            + "stream that had never been disposed and never would be");
        stream.IsUsable().Should().BeFalse(
            "and a released stream reports itself unusable through the SAME predicate every cache "
            + "already consults — no new state, no second liveness answer");
    }

    /// <summary>
    /// The control, and it is not decoration: every assertion above is satisfied by a stream that
    /// releases its hub immediately and never works. This pins that a LIVE stream still answers
    /// with its hub, through both accessors, and still writes.
    /// </summary>
    [HubFact]
    public void ALiveStream_StillAnswersWithItsHub()
    {
        var host = GetHost();
        var stream = CreateStream(host);

        stream.Hub.Should().NotBeNull();
        stream.TryGetHub().Should().BeSameAs(stream.Hub,
            "the guarded accessor resolves the very hub the non-nullable property does");
        stream.RequireHub().Should().BeSameAs(stream.Hub,
            "and so does the require-accessor the patch reducers use");

        Action act = () => stream.Update(_ => new Empty(), _ => { });
        act.Should().NotThrow("a live stream still takes writes");
    }

    /// <summary>
    /// <see cref="SynchronizationStreamLiveness.RequireHub"/> refuses with the TRANSIENT
    /// <see cref="HubDisposingException"/>, never a <see cref="NullReferenceException"/>.
    ///
    /// <para>That difference is the whole reason step 3 is safe where the predecessor's attempt was
    /// not. <see cref="HubDisposingException"/> is an <see cref="ObjectDisposedException"/>, so it
    /// classifies as <c>ErrorType.ShuttingDown</c> — "the address may reactivate, ask again" — and
    /// the Blazor circuit's existing catch around <c>BindStream()</c> already handles it. The raw
    /// NRE the predecessor produced classified as a TERMINAL <c>DeliveryFailure</c> instead.</para>
    /// </summary>
    [HubFact]
    public void RequireHub_OnAReleasedStream_RefusesTransiently()
    {
        var host = GetHost();
        var stream = CreateStream(host);
        stream.Dispose();

        Action act = () => _ = stream.RequireHub();

        var ex = act.Should().Throw<HubDisposingException>(
            "a surface that must return a value has only a throw for a refusal, and this one is the "
            + "transient shape the framework already classifies as retryable");
        ex.Which.HubAddress.Should().Be(host.Address,
            "the refusal names the host so a caller can retry the right address");
        (ex.Which is ObjectDisposedException).Should().BeTrue(
            "staying in the ObjectDisposedException family is what keeps the existing teardown-aware "
            + "handling (Blazor's BindStream catch, SynchronizationStream.OnError) working");
    }

    /// <summary>
    /// 🚨 The reducers' single gateway absorbs that refusal into the <c>null</c> its own signature
    /// already models. Every patch reducer in the codebase — <c>StandardReducers</c>' three,
    /// <c>MeshDataSource.PatchMeshNode</c> — is invoked from exactly one place,
    /// <c>ToChangeItem</c>, and <c>null</c> is what it already returns for a stream with no
    /// <c>PatchFunction</c> registered. So a reducer racing a disposal degrades to "no patch
    /// derived", which both callers already handle, instead of to a fault.
    /// </summary>
    [HubFact]
    public async Task ToChangeItem_OnAReleasedStream_AnswersWithItsModelledAbsence()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var collection = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collection));
        ((object?)stream).Should().NotBeNull();

        // Let the stream reach a state a patch could apply to, so the negative below is about the
        // RELEASE and not about a stream that never started.
        var current = await stream!.Timeout(30.Seconds())
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        // An EMPTY patch on purpose: PatchEntityStore's operation loop then does nothing and the
        // method runs straight to its Version stamp — the read that has to answer for itself. A
        // populated patch would exercise the deserialization arms too and blur which read the test
        // is about.
        var json = JsonSerializer.SerializeToElement(current.Value, stream!.Hub.JsonSerializerOptions);
        var emptyPatch = new JsonPatch();

        stream.ToChangeItem(current.Value!, json, emptyPatch, "test")
            .Should().NotBeNull("precondition: a LIVE stream still derives a change");

        stream.Dispose();

        stream.ToChangeItem(current.Value!, json, emptyPatch, "test")
            .Should().BeNull(
                "a released stream derives nothing — and says so through the absent value this "
                + "method already returns when no PatchFunction is registered, never by NRE'ing "
                + "inside a reducer that has no way to answer 'no'");
    }

    /// <summary>
    /// 🚨 THE TWO ACCESSORS ANSWER DIFFERENT QUESTIONS, and this pins the difference — because
    /// collapsing them looks like a tidy-up and is a behaviour change.
    ///
    /// <para>A FAULTED stream still HOLDS its hub. <c>TryGetHub()</c> refuses it, correctly: it
    /// asks "should I use this stream?", and <see cref="StreamLiveness"/> counts a terminal store
    /// error exactly as much as a disposal (#2387). <c>HubIfHeld()</c> hands the hub back, also
    /// correctly: it asks "is the reference still there?", and it is.</para>
    ///
    /// <para><b>Why the distinction is not academic.</b> Step 3's first draft guarded
    /// <c>DataSource.Initialized</c> — <c>Task.WhenAll(streams.Select(s =&gt; s.Hub.Started))</c> —
    /// with <c>TryGetHub()</c>. A stream whose initial load faulted was therefore dropped from the
    /// WhenAll, so <c>Initialized</c> completed SUCCESSFULLY and a hub whose data source had thrown
    /// went on answering requests as though nothing had happened.
    /// <c>DataContextFaultedInitBeforeStreamHubBoundTest</c> caught it; this test is what stops the
    /// same substitution being made again without one.</para>
    /// </summary>
    [HubFact]
    public void AFaultedStream_StillHoldsItsHub_EvenThoughItIsNotUsable()
    {
        var host = GetHost();
        var stream = CreateStream(host);

        stream.OnError(new InvalidOperationException("boom"));

        stream.IsUsable().Should().BeFalse(
            "a terminal store error is as permanent as a disposal — a ReplaySubject replays it to "
            + "every later subscriber forever (#2387)");
        stream.TryGetHub().Should().BeNull(
            "so a CONSUMER asking 'should I use this stream?' must be told no");
        stream.HubIfHeld().Should().NotBeNull(
            "but the reference is still THERE, and an owner-side guard that asks the presence "
            + "question must not be answered as though it had asked the liveness one — that "
            + "substitution turned a faulted data-source init into a silently successful one");
        stream.RequireHub().Should().BeSameAs(stream.Hub,
            "and the throwing form asks the same presence question, so it does not throw either");
    }

    /// <summary>
    /// Forces the collection the reachability assertion reads. Two passes with a finalizer drain
    /// between them: the first may only queue the object for finalization, and a hub holds
    /// finalizable state.
    /// </summary>
    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Builds a stream, weakly references its hub and disposes the stream — all inside a frame that
    /// is GONE by the time the caller collects, so no live local on the test's own stack can root
    /// the hub and make a red test look green.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (SynchronizationStream<Empty> Stream, WeakReference HubRef) CreateDisposeAndWeaklyReference(
        IMessageHub host)
    {
        var stream = CreateStream(host);
        var hubRef = new WeakReference(stream.Hub);
        hubRef.IsAlive.Should().BeTrue("precondition: the stream owns a live sync hub");
        stream.Dispose();
        return (stream, hubRef);
    }
}
