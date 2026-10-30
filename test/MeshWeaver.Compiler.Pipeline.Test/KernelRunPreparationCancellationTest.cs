using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Kernel.Hub;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// #7362 review — the two seams a cancel of a run parked in a preparation stage goes through:
/// <see cref="KernelExecutor.UntilCancelled{T}"/> (which side claims the outcome, and what happens to
/// a value that loses) and <see cref="CellSurfaceAssemblyProvider.CollectOwned"/> (who owns a
/// lease buffered while another entry is still resolving). Pure Rx, no clock.
/// </summary>
public class KernelRunPreparationCancellationTest
{
    private static (IList<int> Values, IList<Exception> Errors, Func<bool> Completed) Observe(
        IObservable<int> source, out IDisposable subscription)
    {
        var values = new List<int>();
        var errors = new List<Exception>();
        var completed = false;
        subscription = source.Subscribe(values.Add, errors.Add, () => completed = true);
        return (values, errors, () => completed);
    }

    /// <summary>
    /// A run cancelled while queued meets a stage that answers SYNCHRONOUSLY: the cancel came first,
    /// so the stage is never even subscribed and the run never reaches the script.
    /// </summary>
    [Fact]
    public void AnAlreadyCancelledToken_WinsOverASynchronousStage_WhichIsNeverSubscribed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var subscribed = false;
        var stage = Observable.Create<int>(observer =>
        {
            subscribed = true;
            observer.OnNext(1);
            observer.OnCompleted();
            return Disposable.Empty;
        });

        var (values, errors, _) = Observe(KernelExecutor.UntilCancelled(stage, cts.Token), out _);

        values.Should().BeEmpty("a cancelled run must not carry the stage's value on to the script");
        errors.Should().ContainSingle().Which.Should().BeOfType<OperationCanceledException>();
        subscribed.Should().BeFalse("the stage of a run that is already cancelled is never started");
    }

    /// <summary>A stage that answers before the cancel delivers its value, and the cancel is ignored.</summary>
    [Fact]
    public void AValueClaimedFirst_IsDelivered_AndALaterCancelChangesNothing()
    {
        using var cts = new CancellationTokenSource();
        var stage = new Subject<int>();
        var (values, errors, completed) = Observe(KernelExecutor.UntilCancelled(stage, cts.Token), out _);

        stage.OnNext(7);
        cts.Cancel();

        values.Should().ContainSingle().Which.Should().Be(7);
        errors.Should().BeEmpty();
        completed().Should().BeTrue();
    }

    /// <summary>
    /// The cancel claims first: the stage is unsubscribed, so its later value never reaches the
    /// observer. (What a stage owns when it is unsubscribed is the stage's to release — see
    /// <see cref="AnUnsubscribeBeforeCompletion_DisposesTheBufferedLeases"/>; a value that races the
    /// claim inside the window is handed to <c>release</c>.)
    /// </summary>
    [Fact]
    public void AValueAfterTheCancel_IsNeverDelivered_AndTheStageIsUnsubscribed()
    {
        using var cts = new CancellationTokenSource();
        var stage = new Subject<int>();
        var (values, errors, _) = Observe(
            KernelExecutor.UntilCancelled(stage, cts.Token, _ => { }), out _);

        cts.Cancel();
        stage.OnNext(7);

        errors.Should().ContainSingle().Which.Should().BeOfType<OperationCanceledException>();
        values.Should().BeEmpty();
        stage.HasObservers.Should().BeFalse("the cancel unsubscribes the stage");

    }

    private static CellSurfaceAssembly Entry(string name, Action onRelease) =>
        new($"Type/{name}", $"/tmp/{name}.dll", typeof(KernelRunPreparationCancellationTest).Assembly,
            Disposable.Create(onRelease));

    /// <summary>
    /// The run is cancelled while one cell-surface entry is resolved and another still resolving:
    /// the buffered entry's lease is disposed. A plain <c>ToList</c> (the control below) drops it.
    /// </summary>
    [Fact]
    public void AnUnsubscribeBeforeCompletion_DisposesTheBufferedLeases()
    {
        var disposed = 0;
        var entries = new Subject<CellSurfaceAssembly>();
        var subscription = CellSurfaceAssemblyProvider.CollectOwned(entries).Subscribe(_ => { });

        entries.OnNext(Entry("a", () => disposed++));
        subscription.Dispose();

        disposed.Should().Be(1, "nobody else will ever dispose a lease the abandoned collection held");
    }

    /// <summary>Negative control: the unfixed shape, <c>ToList</c>, leaves the buffered lease undisposed.</summary>
    [Fact]
    public void Control_APlainToList_LeavesTheBufferedLeaseUndisposed()
    {
        var disposed = 0;
        var entries = new Subject<CellSurfaceAssembly>();
        var subscription = entries.ToList().Subscribe(_ => { });

        entries.OnNext(Entry("a", () => disposed++));
        subscription.Dispose();

        disposed.Should().Be(0, "this is the leak CollectOwned closes");
    }

    /// <summary>A completed collection hands every lease over, disposing none.</summary>
    [Fact]
    public void ACompletedCollection_HandsTheLeasesOver()
    {
        var disposed = 0;
        var entries = new Subject<CellSurfaceAssembly>();
        IReadOnlyCollection<CellSurfaceAssembly>? received = null;
        var subscription = CellSurfaceAssemblyProvider.CollectOwned(entries).Subscribe(list => received = list);

        entries.OnNext(Entry("a", () => disposed++));
        entries.OnNext(Entry("b", () => disposed++));
        entries.OnCompleted();
        subscription.Dispose();

        received.Should().NotBeNull();
        received!.Count.Should().Be(2);
        disposed.Should().Be(0, "the receiver owns the leases now");
    }

    /// <summary>A faulted collection disposes what it had buffered.</summary>
    [Fact]
    public void AFaultedCollection_DisposesTheBufferedLeases()
    {
        var disposed = 0;
        var entries = new Subject<CellSurfaceAssembly>();
        Exception? error = null;
        CellSurfaceAssemblyProvider.CollectOwned(entries).Subscribe(_ => { }, ex => error = ex);

        entries.OnNext(Entry("a", () => disposed++));
        entries.OnError(new InvalidOperationException("the other entry failed"));

        error.Should().NotBeNull();
        disposed.Should().Be(1);
    }
}
