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
/// Regression tests for the dead-stream safety net in
/// <see cref="SynchronizationStream{TStream}"/>: when a Reduce / construct
/// races a parent hub that has just started disposing, the resulting
/// stream's Hub field stays null — every public method that touched Hub
/// used to NRE. The IDE then surfaced "user-unhandled" first-chance breaks
/// at the call site (typically inside a Blazor circuit teardown's Rx
/// pipeline) even when the call site had a Catch downstream.
///
/// <para>The fixes:</para>
/// <list type="bullet">
/// <item><description>Constructor on a disposing parent: don't throw — log
/// Debug, mark <c>isDisposed = true</c>, complete the Store, leave Hub null.</description></item>
/// <item><description><see cref="SynchronizationStream{TStream}.OnNext"/>,
/// both <c>ISynchronizationStream&lt;TStream&gt;.Update</c> overloads,
/// <see cref="ISynchronizationStream.RegisterForDisposal"/>,
/// <c>DeliverMessage</c>: guard against <c>isDisposed</c> / Hub-null,
/// log Debug + bail (or in OnNext's case forward the failure to subscribers
/// via Store.OnError).</description></item>
/// <item><description>OnError path: don't touch Hub.FailStartup / OpenGate
/// when Hub is null.</description></item>
/// <item><description>Dispose: only call Hub.Dispose() when Hub is non-null.</description></item>
/// </list>
///
/// <para>Test strategy: build a host hub, stop it (RunLevel becomes ShutDown),
/// then construct a SynchronizationStream against it and exercise every
/// public method. None should throw NRE; all should produce a benign no-op.</para>
/// </summary>
public class DeadStreamSafetyTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Empty;

    private async Task<SynchronizationStream<Empty>> CreateAgainstDisposingHost()
    {
        var host = GetHost();

        // Force the host past Started so the dead-stream branch in the ctor
        // fires. Calling Dispose() bumps RunLevel to DisposeHostedHubs and
        // ultimately ShutDown — both > Started, both trigger the dead path.
        host.Dispose();
        // RunLevel check in ctor compares against MessageHubRunLevel.Started.
        // Wait briefly for the dispose to begin so RunLevel is past Started.
        await host.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .Timeout(2.Seconds())
            .ToTask(TestContext.Current.CancellationToken);

        var reduceManager = new ReduceManager<Empty>(host);
        return new SynchronizationStream<Empty>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            reduceManager,
            null);
    }

    [HubFact]
    public async Task Ctor_OnDisposingHost_DoesNotThrow_AndProducesDeadStream()
    {
        // Pre-fix: ObjectDisposedException at the user's call site (Reduce
        // chain). Post-fix: ctor returns a "dead" stream, no exception.
        Func<Task> act = () => CreateAgainstDisposingHost();
        await act.Should().NotThrowAsync(
            "the dead-stream branch must replace the throw with a benign no-op stream");
    }

    [HubFact]
    public async Task OnNext_OnDeadStream_DoesNotThrow_AndCompletesSubscribers()
    {
        var stream = await CreateAgainstDisposingHost();
        var emitted = false;
        var errored = false;
        var completed = false;
        using var sub = ((IObservable<ChangeItem<Empty>>)stream).Subscribe(
            _ => emitted = true,
            _ => errored = true,
            () => completed = true);

        var act = () => stream.OnNext(new ChangeItem<Empty>(new Empty(), stream.StreamId, 0));
        act.Should().NotThrow(
            "OnNext on a dead stream must not NRE on the null Hub — " +
            "this was the original Blazor-circuit-teardown crash");

        // Store.OnCompleted was called from the ctor — subscribers see completion,
        // never see emissions or errors from the dead stream's OnNext.
        completed.Should().BeTrue("dead-stream subscribers receive Store.OnCompleted from the ctor");
        emitted.Should().BeFalse("dead stream cannot emit values");
        errored.Should().BeFalse("dead stream OnNext must not OnError subscribers — that path is reserved for live-stream Hub.Post failures");
    }

    [HubFact]
    public async Task Update_OnDeadStream_SignalsDisposedToProducer()
    {
        var stream = await CreateAgainstDisposingHost();
        var updateInvoked = false;
        Exception? signaled = null;

        var act = () => stream.Update(
            _ => { updateInvoked = true; return (ChangeItem<Empty>?)null; },
            ex => signaled = ex);

        act.Should().NotThrow(
            "Update on a dead stream must not throw — it errors back to the producer instead");
        // The update delegate itself should NOT have been invoked — the dead
        // stream's TryGetActiveHub guard returns false BEFORE Hub.Post runs.
        updateInvoked.Should().BeFalse(
            "the update delegate executes on the hub action block; a dead stream has no hub to post to");
        // New contract: a dead/disposed stream ERRORS incoming writes (ObjectDisposedException) so the
        // producer — a FileSystemWatcher, a remote subscription — tears down its own source instead of
        // pushing into a disposed hub. See Doc/Architecture/HubDisposalModel.
        signaled.Should().BeOfType<ObjectDisposedException>(
            "incoming writes to a dead stream must error so the producer stops feeding it");
    }

    [HubFact]
    public async Task RegisterForDisposal_OnDeadStream_DisposesImmediately()
    {
        var stream = await CreateAgainstDisposingHost();
        var disposed = false;
        var disposable = new ActionDisposable(() => disposed = true);

        var act = () => stream.RegisterForDisposal(disposable);

        act.Should().NotThrow(
            "RegisterForDisposal on a dead stream must not NRE on the null Hub");
        disposed.Should().BeTrue(
            "the registrant should be disposed immediately so the caller doesn't leak it — " +
            "the caller's intent was 'couple this disposable to the stream's lifetime', " +
            "and a dead stream is already terminal");
    }

    [HubFact]
    public async Task Dispose_OnDeadStream_DoesNotThrow()
    {
        var stream = await CreateAgainstDisposingHost();
        var act = () => stream.Dispose();
        act.Should().NotThrow("Dispose on a dead stream must skip the null Hub branch cleanly");
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
