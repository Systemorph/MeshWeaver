using System;
using System.Reactive.Subjects;
using MeshWeaver.Blazor.Components;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Unit tests for <see cref="StreamHandover"/> — the LayoutAreaView navigation hand-over
/// protocol: the outgoing (retiring) area stream is disposed exactly when the NEW stream's
/// first frame arrives, never at transition begin (the still-visible previous content's
/// interactive elements keep posting into it during the gap), with at most one retiring
/// stream at a time and full release on component dispose.
/// </summary>
public class StreamHandoverTest
{
    /// <summary>Counts disposals so double-dispose and premature dispose are both visible.</summary>
    private sealed class FakeStream : IDisposable
    {
        public int DisposeCount { get; private set; }
        public bool IsDisposed => DisposeCount > 0;
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void TransitionBegin_DoesNotDisposeTheOutgoingStream()
    {
        // The old content is still rendered (keep-last-good) — its stream must survive
        // the transition begin so e.g. slide click-to-advance keeps working.
        var handover = new StreamHandover();
        var oldStream = new FakeStream();

        handover.BeginTransition(oldStream);

        oldStream.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public void FirstFrameOfNewStream_DisposesRetiringStream_ExactlyOnce()
    {
        var handover = new StreamHandover();
        var oldStream = new FakeStream();
        var newFrames = new Subject<int>();

        handover.BeginTransition(oldStream);
        using var trigger = handover.CompleteOnFirstFrame(newFrames);

        // Not disposed until the new content's first frame actually lands.
        oldStream.IsDisposed.Should().BeFalse();

        newFrames.OnNext(1);
        oldStream.DisposeCount.Should().Be(1);

        // Subsequent frames must not re-dispose (Take(1) trigger).
        newFrames.OnNext(2);
        oldStream.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void SecondTransitionBeforeAnyFrame_DisposesPreviouslyRetiringStreamImmediately()
    {
        // At most ONE stream retires at a time: an older transition is superseded and its
        // stream released right away — only the most recent previous content is kept alive.
        var handover = new StreamHandover();
        var streamA = new FakeStream();
        var streamB = new FakeStream();
        var framesB = new Subject<int>();
        var framesC = new Subject<int>();

        handover.BeginTransition(streamA);              // A retires, B binds…
        using var triggerB = handover.CompleteOnFirstFrame(framesB);

        handover.BeginTransition(streamB);              // …but B is superseded by C before any frame
        using var triggerC = handover.CompleteOnFirstFrame(framesC);

        streamA.DisposeCount.Should().Be(1);            // superseded → disposed immediately
        streamB.IsDisposed.Should().BeFalse();          // now the (only) retiring stream

        // A late frame on the SUPERSEDED stream B (its trigger is still wired) must be a
        // no-op — in particular it must not dispose B (itself) or double-dispose A.
        framesB.OnNext(1);
        streamA.DisposeCount.Should().Be(1);
        streamB.IsDisposed.Should().BeFalse();

        // Only the CURRENT transition's first frame retires B.
        framesC.OnNext(1);
        streamB.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_DisposesPendingRetiringStream()
    {
        // Component teardown mid-hand-over: the new stream's first frame will never
        // arrive, so Dispose releases the pending retiring stream.
        var handover = new StreamHandover();
        var oldStream = new FakeStream();
        var newFrames = new Subject<int>();

        handover.BeginTransition(oldStream);
        using var trigger = handover.CompleteOnFirstFrame(newFrames);

        handover.Dispose();
        oldStream.DisposeCount.Should().Be(1);

        // A frame arriving after dispose must not double-dispose.
        newFrames.OnNext(1);
        oldStream.DisposeCount.Should().Be(1);
    }
}
