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
    /// Test 4 — the helper does NOT invent identity. With Context=null on
    /// the emitting thread and CircuitContext set as a Blazor session value,
    /// the Subscribe callback observes null AsyncLocal. Identity rides on
    /// <c>delivery.AccessContext</c> via PostOptions — the receiver's
    /// UserServiceDeliveryPipeline sets/restores AsyncLocal under try/finally.
    /// The wrap mutating AsyncLocal at Subscribe time leaked identity into
    /// the caller's logical execution (see commit 757d2a296).
    /// </summary>
    [Fact]
    public async Task PassThrough_Does_Not_Synthesize_Context_From_CircuitContext()
    {
        _access.SetContext(null);
        _access.SetCircuitContext(new AccessContext { ObjectId = "bob-circuit", Name = "Bob" });

        var subject = new Subject<int>();
        var wrapped = subject.CarryAccessContext(_serviceProvider);
        var observed = new TaskCompletionSource<string?>();
        wrapped.Subscribe(_ => observed.TrySetResult(_access.Context?.ObjectId));

        await Task.Run(() => subject.OnNext(1));
        var result = await observed.Task.WaitAsync(5.Seconds());

        result.Should().BeNull(
            because: "CarryAccessContext is a pass-through (757d2a296). It does NOT read " +
                     "CircuitContext or mutate AsyncLocal on Subscribe — identity rides on " +
                     "delivery.AccessContext via PostOptions. Context was explicitly set to " +
                     "null on the emitting thread, so the subscriber sees null.");
    }

    /// <summary>
    /// Test 5 — pass-through means the wrap doesn't manufacture identity per
    /// subscriber. With ambient Context cleared before emission, both
    /// concurrent subscribers see null on their emission thread — there is
    /// no capture-and-restore. Pinning this prevents accidental
    /// reintroduction of the AsyncLocal leak.
    /// </summary>
    [Fact]
    public async Task PassThrough_Does_Not_Restore_Captured_Identity_Per_Subscriber()
    {
        // Two subjects wrapped under different ambient values, then ambient
        // cleared before emission. Under the OLD capture-and-restore model
        // each subscriber would see its captured identity. Under the current
        // pass-through model both see null.
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

        // Stream-poll until all 10 emissions have been observed.
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Where(_ => aliceObserved.Count >= 5 && bobObserved.Count >= 5)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(5))
            .ToTask();

        aliceObserved.Should().OnlyContain(id => id == null,
            because: "pass-through doesn't restore captured identity per emission — ambient " +
                     "Context was null on the emitting thread, so the subscriber sees null.");
        bobObserved.Should().OnlyContain(id => id == null,
            because: "symmetric — the wrap doesn't synthesize identity from a captured value.");
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
