using Xunit;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Reactive.Assertions;

namespace MeshWeaver.Testing.Xunit.Test;

/// <summary>
/// The contract of <c>ObservableAssertions.NotEmitBefore</c> (#6817): a negative assertion ended by
/// a POSITIVE settled signal. Every branch that must FAIL is pinned here, because a regression that
/// folded one of them into a pass would silently weaken every negative test built on it — and
/// both subscriptions are released whichever way it ends.
/// </summary>
public class NotEmitBeforeContractTest
{
    // Never reached: every subject and signal here is synchronous, so each case decides before the
    // call returns. It only caps a regression that would otherwise hang the host — and it is not
    // a write wait, so the framework's write bound (TestTimeouts) does not apply.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SilenceUntilTheSignal_Passes()
    {
        var subject = new Subject<int>();
        var settled = new Subject<string>();
        var assertion = subject.Should().Within(Bound).NotEmitBefore(settled, "nothing is due");

        settled.OnNext("settled");
        await assertion;
        subject.HasObservers.Should().BeFalse("the assertion released the subject on success");
        settled.HasObservers.Should().BeFalse("the assertion released the signal on success");
    }

    [Fact]
    public async Task AnEmissionBeforeTheSignal_Fails()
    {
        var subject = new Subject<int>();
        var settled = new Subject<string>();
        var assertion = subject.Should().Within(Bound).NotEmitBefore(settled, "nothing is due");

        // Synchronously armed: an emission the very next statement causes is seen.
        subject.OnNext(7);
        settled.OnNext("settled");

        var thrown = await Assert.ThrowsAsync<ObservableAssertionException>(() => assertion);
        thrown.Message.Should().Contain("it emitted 7");
        subject.HasObservers.Should().BeFalse("the assertion released the subject on failure");
        settled.HasObservers.Should().BeFalse("the assertion released the signal on failure");
    }

    [Fact]
    public async Task ASignalThatNeverArrives_Fails_NeverPasses()
    {
        var subject = new Subject<int>();
        var settled = new Subject<string>();
        var assertion = subject.Should().Within(TimeSpan.FromMilliseconds(100))
            .NotEmitBefore(settled, "nothing is due");

        var thrown = await Assert.ThrowsAsync<ObservableAssertionException>(() => assertion);
        thrown.Message.Should().Contain("did not arrive");
        subject.HasObservers.Should().BeFalse("the assertion released the subject on timeout");
        settled.HasObservers.Should().BeFalse("the assertion released the signal on timeout");
    }

    [Fact]
    public async Task ASignalThatCompletesEmpty_Fails()
    {
        var assertion = Observable.Never<int>().Should().Within(TimeSpan.FromMilliseconds(100))
            .NotEmitBefore(Observable.Empty<string>(), "nothing is due");

        await Assert.ThrowsAsync<ObservableAssertionException>(() => assertion);
    }

    [Fact]
    public async Task ASignalThatFaults_Fails()
    {
        var assertion = Observable.Never<int>().Should().Within(Bound)
            .NotEmitBefore(Observable.Throw<string>(new InvalidOperationException("boom")), "nothing is due");

        var thrown = await Assert.ThrowsAsync<ObservableAssertionException>(() => assertion);
        thrown.Message.Should().Contain("boom");
    }

    [Fact]
    public async Task TheSubjectEndingBeforeTheSignal_IsSilence_AndTheSignalStillDecides()
    {
        var subject = new Subject<int>();
        var settled = new Subject<string>();
        var assertion = subject.Should().Within(Bound).NotEmitBefore(settled, "nothing is due");

        subject.OnError(new InvalidOperationException("the subject ended"));
        settled.OnNext("settled");
        await assertion;
    }

    [Fact]
    public async Task ACancelledWait_PropagatesCancellation_AndReleasesBoth()
    {
        var subscriptions = 0;
        IObservable<T> Counted<T>() => Observable.Create<T>(_ =>
        {
            Interlocked.Increment(ref subscriptions);
            return Disposable.Create(() => Interlocked.Decrement(ref subscriptions));
        });
        using var cancel = new CancellationTokenSource();
        var assertion = Counted<int>().Should().Within(Bound)
            .NotEmitBefore(Counted<string>(), "nothing is due", cancel.Token);
        Volatile.Read(ref subscriptions).Should().Be(2, "both are subscribed before the call returns");

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assertion);
        Volatile.Read(ref subscriptions).Should().Be(0, "a cancelled wait releases both subscriptions");
    }
}
