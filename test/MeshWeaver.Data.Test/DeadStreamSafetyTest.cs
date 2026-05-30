using System;
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

    private SynchronizationStream<Empty> CreateAgainstDisposingHost()
    {
        var host = GetHost();

        // Force the host past Started so the dead-stream branch in the ctor
        // fires. Calling Dispose() bumps RunLevel to DisposeHostedHubs and
        // ultimately ShutDown — both > Started, both trigger the dead path.
        host.Dispose();
        // RunLevel check in ctor compares against MessageHubRunLevel.Started.
        // Wait briefly for the dispose to begin so RunLevel is past Started.
        host.Disposal!.Wait(2.Seconds()).Should().BeTrue("host should dispose within 2s");

        var reduceManager = new ReduceManager<Empty>(host);
        return new SynchronizationStream<Empty>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            reduceManager,
            null);
    }

    [HubFact]
    public void Ctor_OnDisposingHost_DoesNotThrow_AndProducesDeadStream()
    {
        // Pre-fix: ObjectDisposedException at the user's call site (Reduce
        // chain). Post-fix: ctor returns a "dead" stream, no exception.
        Action act = () => CreateAgainstDisposingHost();
        act.Should().NotThrow(
            "the dead-stream branch must replace the throw with a benign no-op stream");
    }

    [HubFact]
    public void OnNext_OnDeadStream_DoesNotThrow_AndCompletesSubscribers()
    {
        var stream = CreateAgainstDisposingHost();
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
    public void Update_OnDeadStream_NoOps()
    {
        var stream = CreateAgainstDisposingHost();
        var updateInvoked = false;
        var errorInvoked = false;

        var act = () => stream.Update(
            _ => { updateInvoked = true; return null; },
            _ => errorInvoked = true);

        act.Should().NotThrow("Update on a dead stream must be a silent no-op");
        // The update delegate itself should NOT have been invoked — the dead
        // stream's TryGetActiveHub guard returns false BEFORE Hub.Post runs.
        updateInvoked.Should().BeFalse(
            "the update delegate executes on the hub action block; a dead stream has no hub to post to");
        errorInvoked.Should().BeFalse("no error path triggered when the guard returns false");
    }

    [HubFact]
    public void RegisterForDisposal_OnDeadStream_DisposesImmediately()
    {
        var stream = CreateAgainstDisposingHost();
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
    public void Dispose_OnDeadStream_DoesNotThrow()
    {
        var stream = CreateAgainstDisposingHost();
        var act = () => stream.Dispose();
        act.Should().NotThrow("Dispose on a dead stream must skip the null Hub branch cleanly");
    }

    private sealed class ActionDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
