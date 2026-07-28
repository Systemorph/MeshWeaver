using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the disposal contract of <see cref="SynchronizationStream{TStream}"/>.
///
/// <para><b>Construction against a disposing host REFUSES</b> — it does not hand back a
/// half-built stream. A synchronization stream owns a hosted sub-hub, and hosted-hub creation
/// is frozen from the first instant of <c>MessageHub.Dispose</c> (cascading through the whole
/// subtree), so on a disposing host there is no hub to give. The predecessor of this contract
/// built a "dead stream" instead — <c>isDisposed</c>, completed store, and <c>Hub = null!</c>
/// with a comment saying every consumer must go through <c>TryGetActiveHub</c>. NO CONSUMER
/// DID (<c>grep -rn TryGetActiveHub src</c> found only the stream itself, against ~96 sites
/// dereferencing the interface's NON-NULLABLE <c>Hub</c>), and <see cref="ISynchronizationStream"/>
/// exposes no liveness member for them to check even if they wanted to. It cost a production
/// NullReferenceException in <c>LayoutAreaHost</c>'s constructor whenever a page subscribed
/// during a hub recycle. Refusing with a TRANSIENT, typed
/// <see cref="HubDisposingException"/> is what lets the messaging layer answer the caller
/// "the address may reactivate — ask again" (<see cref="ErrorType.ShuttingDown"/>) instead of
/// crashing it, and it makes the non-null <c>Hub</c> declaration true again.</para>
///
/// <para><b>A stream that is DISPOSED after a healthy life stays inert, never throwing</b> —
/// <c>OnNext</c>, both <c>Update</c> overloads, <c>RegisterForDisposal</c>, <c>DeliverMessage</c>
/// and <c>Dispose</c> itself are all no-ops (or signal back to the producer). That half of the
/// original safety net is unchanged and still covered below.</para>
/// </summary>
public class DeadStreamSafetyTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Empty;

    /// <summary>Builds a healthy stream on a live host, then disposes the stream.</summary>
    private SynchronizationStream<Empty> CreateAndDispose(out bool completed)
    {
        var host = GetHost();
        var stream = new SynchronizationStream<Empty>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            new ReduceManager<Empty>(host),
            null);

        var sawCompletion = false;
        // Subscribe BEFORE disposal so the terminal notification is observable.
        stream.Subscribe(_ => { }, _ => { }, () => sawCompletion = true);
        stream.Dispose();
        completed = sawCompletion;
        return stream;
    }

    [HubFact]
    public async Task Ctor_OnDisposingHost_Refuses_WithATransientHubDisposalError()
    {
        var host = GetHost();
        host.Dispose();
        await host.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .Timeout(10.Seconds())
            .ToTask(TestContext.Current.CancellationToken);

        Action act = () => _ = new SynchronizationStream<Empty>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            new ReduceManager<Empty>(host),
            null);

        var ex = act.Should().Throw<HubDisposingException>(
            "a stream that cannot own its sub-hub is not a stream — refusing is what keeps "
            + "ISynchronizationStream.Hub's non-null declaration honest. Fabricating a hub-less "
            + "'dead stream' pushed the failure onto consumers as a NullReferenceException.");
        ex.Which.HubAddress.Should().Be(host.Address,
            "the failure must name the hub that is going down, so the caller can retry the right address");
        // The Blazor circuit already catches ObjectDisposedException around BindStream()
        // ("old circuit's hub may already be disposing"), and SynchronizationStream.OnError
        // classifies it as benign teardown. Keeping the refusal in that family is what makes it
        // fit the existing handling instead of blowing past it.
        (ex.Which is ObjectDisposedException).Should().BeTrue(
            "the refusal must stay in the ObjectDisposedException family that teardown-aware "
            + "callers (Blazor's BindStream catch, the stream's own OnError classifier) already handle");
    }

    [HubFact]
    public void DisposedStream_CompletesSubscribersAndDropsWrites()
    {
        var stream = CreateAndDispose(out var completed);
        completed.Should().BeTrue("disposing a stream must complete its subscribers");

        var act = () => stream.OnNext(new ChangeItem<Empty>(new Empty(), stream.StreamId, 0));
        act.Should().NotThrow("OnNext on a disposed stream must drop the value, never throw");
    }

    [HubFact]
    public void Update_OnDisposedStream_SignalsDisposedToProducer()
    {
        var stream = CreateAndDispose(out _);
        var updateInvoked = false;
        Exception? signaled = null;

        var act = () => stream.Update(
            _ => { updateInvoked = true; return (ChangeItem<Empty>?)null; },
            ex => signaled = ex);

        act.Should().NotThrow(
            "Update on a disposed stream must not throw — it errors back to the producer instead");
        // The update delegate itself should NOT have been invoked — TryGetActiveHub
        // returns false BEFORE Hub.Post runs.
        updateInvoked.Should().BeFalse(
            "the update delegate executes on the hub action block; a disposed stream has no hub to post to");
        // A disposed stream ERRORS incoming writes (ObjectDisposedException) so the producer — a
        // FileSystemWatcher, a remote subscription — tears down its own source instead of pushing
        // into a disposed hub. See Doc/Architecture/HubDisposalModel.
        signaled.Should().BeOfType<ObjectDisposedException>(
            "incoming writes to a disposed stream must error so the producer stops feeding it");
    }

    [HubFact]
    public void RegisterForDisposal_OnDisposedStream_DisposesImmediately()
    {
        var stream = CreateAndDispose(out _);
        var disposed = false;
        var disposable = new ActionDisposable(() => disposed = true);

        var act = () => stream.RegisterForDisposal(disposable);

        act.Should().NotThrow("RegisterForDisposal on a disposed stream must not throw");
        disposed.Should().BeTrue(
            "the registrant should be disposed immediately so the caller doesn't leak it — "
            + "the caller's intent was 'couple this disposable to the stream's lifetime', "
            + "and a disposed stream is already terminal");
    }

    [HubFact]
    public void Dispose_IsIdempotent()
    {
        var stream = CreateAndDispose(out _);
        var act = () => stream.Dispose();
        act.Should().NotThrow("a second Dispose must be a clean no-op");
    }

    /// <summary>
    /// Root-cause repro for the autocomplete-suite FATAL ObjectDisposedException: a fire-and-forget
    /// <c>.Subscribe(snapshot =&gt; hub.Post(response, ResponseFor(req)))</c> whose continuation lands
    /// while the hub is tearing down. <c>ScheduleNotify</c> already DROPS every non-shutdown message
    /// once <c>RunLevel &gt;= DisposeHostedHubs</c>, but it runs AFTER <c>postPipeline.Invoke</c> — and
    /// the pipeline (AccessContext stamping) resolves services from the now-disposed ServiceProvider,
    /// throwing ObjectDisposedException SYNCHRONOUSLY out of <c>Post</c> into the subscriber (unobserved
    /// → process-fatal). The fix hoists the teardown drop-guard ahead of the pipeline.
    ///
    /// <para>This pins it deterministically with a post-pipeline step that throws once armed
    /// (standing in for the disposed-SP resolution). Pre-fix the throw escapes <c>Post</c>; post-fix
    /// <c>Post</c> short-circuits to a dropped delivery before the pipeline ever runs.</para>
    /// </summary>
    [HubFact]
    public async Task Post_OnDisposingHost_DropsWithoutInvokingPipeline()
    {
        var armed = false;
        var pipelineInvokedWhileArmed = false;

        var host = GetHost(c => c.AddPostPipeline(p => p.AddPipeline((delivery, next) =>
        {
            if (armed)
            {
                // Pre-fix: this runs during teardown and throws straight out of Post.
                pipelineInvokedWhileArmed = true;
                throw new ObjectDisposedException("ServiceProvider",
                    "simulated disposed-ServiceProvider resolution inside the post pipeline");
            }
            return next(delivery);
        })));

        // Drive the host to teardown (RunLevel >= DisposeHostedHubs).
        host.Dispose();
        await host.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .Timeout(2.Seconds())
            .ToTask(TestContext.Current.CancellationToken);

        armed = true;

        IMessageDelivery? result = null;
        var act = () => { result = host.Post(new Empty()); };

        act.Should().NotThrow(
            "Post on a disposing hub must drop the message before invoking the post pipeline — " +
            "never let an ObjectDisposedException escape synchronously into a fire-and-forget subscriber");
        pipelineInvokedWhileArmed.Should().BeFalse(
            "the teardown guard must short-circuit ahead of postPipeline.Invoke, so the pipeline " +
            "(which touches the disposed ServiceProvider) is never run during teardown");
        result?.State.Should().Be(MessageDeliveryState.Failed,
            "a message posted during teardown is dropped as a Failed delivery, matching ScheduleNotify's drop");
    }

    private sealed class ActionDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
