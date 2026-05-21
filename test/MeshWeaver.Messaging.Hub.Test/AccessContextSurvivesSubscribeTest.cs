using System;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pure in-process unit tests for the cross-cutting
/// <see cref="AccessContextCaptureExtensions.CarryAccessContext{T}"/> helper.
/// Each test sets an <see cref="AccessService.Context"/> on the test thread,
/// invokes <see cref="AccessContextCaptureExtensions.CarryAccessContext{T}"/>
/// to wrap a source observable, then asserts that the Subscribe callback
/// observes the SAME <see cref="AccessContext"/> on AsyncLocal even when the
/// emission lands on another thread.
///
/// <para>These tests pin the foundation of the "MessageHub sets, framework
/// primitive preserves" model. The higher layers
/// (<c>IMeshService.CreateNode</c>, <c>MeshNodeStreamHandle.Update</c>,
/// <c>IMeshNodeStreamCache.GetStream</c>) all delegate to this helper, so the
/// invariants pinned here propagate everywhere.</para>
/// </summary>
public class AccessContextSurvivesSubscribeTest : IDisposable
{
    private readonly AccessService _access = new();
    private readonly ServiceCollection _services = new();
    private readonly IServiceProvider _serviceProvider;

    public AccessContextSurvivesSubscribeTest()
    {
        _services.AddSingleton(_access);
        _serviceProvider = _services.BuildServiceProvider();
    }

    public void Dispose() => (_serviceProvider as IDisposable)?.Dispose();

    /// <summary>
    /// Test 1 — sets Context to user "alice", subscribes to a
    /// <see cref="Subject{T}"/> wrapped with <c>CarryAccessContext</c>,
    /// then emits from <see cref="Task.Run(Action)"/>. The Subscribe callback
    /// MUST observe <c>Context.ObjectId == "alice"</c> even though the emission
    /// landed on a thread-pool thread that never had the AsyncLocal set.
    /// </summary>
    [Fact]
    public async Task Captured_Context_Restored_OnNext_From_Different_Thread()
    {
        _access.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var subject = new Subject<int>();
        var wrapped = subject.CarryAccessContext(_serviceProvider);
        var observed = new TaskCompletionSource<string?>();
        wrapped.Subscribe(_ => observed.TrySetResult(_access.Context?.ObjectId));

        await Task.Run(() => subject.OnNext(1));
        var result = await observed.Task.WaitAsync(5.Seconds());

        result.Should().Be("alice",
            because: "CarryAccessContext captured AsyncLocal at wrap time on the test thread " +
                     "(where Context = alice) and re-stamps it on every emission. The Subscribe " +
                     "callback runs on the thread-pool thread that OnNext fired from — that " +
                     "thread has no AsyncLocal value of its own, so without the wrap it would " +
                     "observe null.");
    }

    /// <summary>
    /// Test 2 — even when the emitter has explicitly suppressed
    /// <see cref="ExecutionContext"/> flow, the wrap must still restore the
    /// captured context. This pins that we are not relying on default
    /// AsyncLocal flow but actively re-stamping per emission.
    /// </summary>
    [Fact]
    public async Task Captured_Context_Restored_OnNext_From_TaskScheduler_With_Suppressed_Flow()
    {
        _access.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var subject = new Subject<int>();
        var wrapped = subject.CarryAccessContext(_serviceProvider);
        var observed = new TaskCompletionSource<string?>();
        wrapped.Subscribe(_ => observed.TrySetResult(_access.Context?.ObjectId));

        await Task.Run(() =>
        {
            using (ExecutionContext.SuppressFlow())
            {
                subject.OnNext(1);
            }
        });
        var result = await observed.Task.WaitAsync(5.Seconds());

        result.Should().Be("alice",
            because: "even with ExecutionContext.SuppressFlow, the wrap re-stamps AsyncLocal " +
                     "on each emission via .Do(_ => SetContext(captured)) — the captured value " +
                     "rides the closure, not the AsyncLocal flow.");
    }

    /// <summary>
    /// Test 3 — when no context is set, the wrap is a pass-through. The
    /// Subscribe callback must still receive the value (no NullRef, no
    /// silent drop) and the AsyncLocal stays cleanly null.
    /// </summary>
    [Fact]
    public async Task Null_Context_Is_PassThrough_No_NullRef()
    {
        _access.SetContext(null);

        var subject = new Subject<int>();
        var wrapped = subject.CarryAccessContext(_serviceProvider);
        var observed = new TaskCompletionSource<int>();
        wrapped.Subscribe(observed.SetResult);

        subject.OnNext(42);
        var result = await observed.Task.WaitAsync(5.Seconds());

        result.Should().Be(42,
            because: "with no ambient context the wrap returns the source unchanged " +
                     "(no Defer, no Do) — the test pins that emission still flows.");
        _access.Context.Should().BeNull(
            because: "the wrap must not invent a context when none was captured.");
    }

    /// <summary>
    /// Test 4 — Context is null but CircuitContext is set. The wrap reads
    /// Context first, falls back to CircuitContext. Subscribe callback
    /// observes the CircuitContext.
    /// </summary>
    [Fact]
    public async Task Capture_Reads_AccessService_Context_First_CircuitContext_Fallback_Second()
    {
        _access.SetContext(null);
        _access.SetCircuitContext(new AccessContext { ObjectId = "bob-circuit", Name = "Bob" });

        var subject = new Subject<int>();
        var wrapped = subject.CarryAccessContext(_serviceProvider);
        var observed = new TaskCompletionSource<string?>();
        wrapped.Subscribe(_ => observed.TrySetResult(_access.Context?.ObjectId));

        await Task.Run(() => subject.OnNext(1));
        var result = await observed.Task.WaitAsync(5.Seconds());

        result.Should().Be("bob-circuit",
            because: "the wrap's capture is `Context ?? CircuitContext`; with Context null " +
                     "it falls back to CircuitContext (the Blazor session identity).");
    }

    /// <summary>
    /// Test 5 — two concurrent wraps with different users must not bleed
    /// each other's captured contexts. Each emission restores its own.
    /// </summary>
    [Fact]
    public async Task Concurrent_Wraps_Do_Not_Bleed()
    {
        // Two subjects, each wrapped under a different captured context.
        // Both subscribers emit interleaved on the shared thread pool.
        _access.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });
        var aliceSubject = new Subject<int>();
        var aliceWrapped = aliceSubject.CarryAccessContext(_serviceProvider);

        _access.SetContext(new AccessContext { ObjectId = "bob", Name = "Bob" });
        var bobSubject = new Subject<int>();
        var bobWrapped = bobSubject.CarryAccessContext(_serviceProvider);

        // Clear ambient so the test thread doesn't see either captured value.
        _access.SetContext(null);

        var aliceObserved = new ConcurrentBag<string?>();
        var bobObserved = new ConcurrentBag<string?>();
        aliceWrapped.Subscribe(_ => aliceObserved.Add(_access.Context?.ObjectId));
        bobWrapped.Subscribe(_ => bobObserved.Add(_access.Context?.ObjectId));

        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 5; i++) aliceSubject.OnNext(i); }),
            Task.Run(() => { for (var i = 0; i < 5; i++) bobSubject.OnNext(i); }));

        await Task.Delay(100); // let emissions drain

        aliceObserved.Should().OnlyContain(id => id == "alice",
            because: "alice's wrap captured 'alice' at wrap time — emissions on her subject " +
                     "must restore that value regardless of bob's interleaved emissions.");
        bobObserved.Should().OnlyContain(id => id == "bob",
            because: "symmetric — captured-value-by-closure prevents cross-contamination.");
    }

    /// <summary>
    /// Test 6 — nested composition. Wrap source A, SelectMany into source B
    /// which is itself wrapped, then Subscribe. Subscribe callback observes
    /// the OUTER user's identity (the wrap that ran last on the emission
    /// path) — both wraps captured the SAME ambient context at wrap time,
    /// so they restore the same value.
    /// </summary>
    [Fact]
    public async Task Restored_Context_Survives_SelectMany_Composition()
    {
        _access.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var outer = new Subject<int>();
        var inner = new Subject<int>();
        var composed = outer
            .CarryAccessContext(_serviceProvider)
            .SelectMany(_ => inner.CarryAccessContext(_serviceProvider));

        var observed = new TaskCompletionSource<string?>();
        composed.Subscribe(_ => observed.TrySetResult(_access.Context?.ObjectId));

        await Task.Run(() => outer.OnNext(1));
        await Task.Delay(50); // let SelectMany subscribe to inner
        await Task.Run(() => inner.OnNext(2));
        var result = await observed.Task.WaitAsync(5.Seconds());

        result.Should().Be("alice",
            because: "both wraps captured 'alice' at construction time on the test thread; " +
                     "the SelectMany composition routes the inner emission through the inner " +
                     "wrap's restore, then up through the outer wrap's restore — both stamp " +
                     "alice. Cross-cutting composes.");
    }
}
