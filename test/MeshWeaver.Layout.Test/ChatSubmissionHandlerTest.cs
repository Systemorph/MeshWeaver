using System;
using MeshWeaver.Blazor.Components;
using Xunit;
using static MeshWeaver.Blazor.Components.ChatSubmissionHandler;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests for ChatSubmissionHandler to verify submission lifecycle,
/// concurrent submission prevention, and input enable/disable state.
/// Timeout-based auto-release was removed: state transitions are driven
/// purely by reactive signals (OnResponseAppeared) or explicit ForceRelease.
/// </summary>
public class ChatSubmissionHandlerTest
{
    [Fact]
    public void SingleSubmission_Lifecycle()
    {
        var handler = new ChatSubmissionHandler();

        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.True(handler.IsInputEnabled);
        Assert.Equal(0, handler.SubmissionCount);

        var result = handler.TryBeginSubmit("Hello");
        Assert.True(result);
        Assert.Equal(SubmissionState.Submitting, handler.State);
        Assert.False(handler.IsInputEnabled);
        Assert.Equal("Hello", handler.LastSubmittedText);
        Assert.Equal(1, handler.SubmissionCount);

        handler.OnMessagePosted();
        Assert.Equal(SubmissionState.WaitingForResponse, handler.State);
        Assert.False(handler.IsInputEnabled);

        handler.OnResponseAppeared();
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.True(handler.IsInputEnabled);
    }

    [Fact]
    public void RapidEnter_WhileSubmitting_ShouldReject()
    {
        var handler = new ChatSubmissionHandler();

        Assert.True(handler.TryBeginSubmit("First message"));
        Assert.Equal(1, handler.SubmissionCount);

        Assert.False(handler.TryBeginSubmit("Second message"));
        Assert.Equal(1, handler.SubmissionCount);
        Assert.Equal("First message", handler.LastSubmittedText);
    }

    [Fact]
    public void TripleRapidEnter_ShouldSubmitOnlyOnce()
    {
        var handler = new ChatSubmissionHandler();

        var first = handler.TryBeginSubmit("Message");
        var second = handler.TryBeginSubmit("Message");
        var third = handler.TryBeginSubmit("Message");

        Assert.True(first);
        Assert.False(second);
        Assert.False(third);
        Assert.Equal(1, handler.SubmissionCount);
    }

    [Fact]
    public void SubmitCaptures_FullText_NotTruncated()
    {
        var handler = new ChatSubmissionHandler();

        var fullText = "This is a complete message with no truncation";
        var result = handler.TryBeginSubmit(fullText);

        Assert.True(result);
        Assert.Equal(fullText, handler.LastSubmittedText);
    }

    [Fact]
    public void InputDisabled_DuringSubmission()
    {
        var handler = new ChatSubmissionHandler();

        Assert.True(handler.IsInputEnabled);

        handler.TryBeginSubmit("Hello");
        Assert.False(handler.IsInputEnabled);

        handler.OnMessagePosted();
        Assert.False(handler.IsInputEnabled);
    }

    [Fact]
    public void InputReEnabled_OnResponse()
    {
        var handler = new ChatSubmissionHandler();

        handler.TryBeginSubmit("Hello");
        handler.OnMessagePosted();

        handler.OnResponseAppeared();

        Assert.True(handler.IsInputEnabled);
        Assert.Equal(SubmissionState.Idle, handler.State);
    }

    [Fact]
    public void RapidTypeSubmitTypeSubmit()
    {
        var handler = new ChatSubmissionHandler();

        Assert.True(handler.TryBeginSubmit("Hello"));
        Assert.Equal(1, handler.SubmissionCount);
        Assert.Equal("Hello", handler.LastSubmittedText);

        handler.OnMessagePosted();

        Assert.False(handler.TryBeginSubmit("World"));
        Assert.Equal(1, handler.SubmissionCount);

        handler.OnResponseAppeared();

        Assert.True(handler.TryBeginSubmit("World"));
        Assert.Equal(2, handler.SubmissionCount);
        Assert.Equal("World", handler.LastSubmittedText);
    }

    [Fact]
    public void SubmitEmptyText_ShouldReject()
    {
        var handler = new ChatSubmissionHandler();

        Assert.False(handler.TryBeginSubmit(null));
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.Equal(0, handler.SubmissionCount);

        Assert.False(handler.TryBeginSubmit(""));
        Assert.Equal(SubmissionState.Idle, handler.State);

        Assert.False(handler.TryBeginSubmit("   "));
        Assert.Equal(SubmissionState.Idle, handler.State);

        Assert.False(handler.TryBeginSubmit("\t\n"));
        Assert.Equal(SubmissionState.Idle, handler.State);

        Assert.Equal(0, handler.SubmissionCount);
    }

    [Fact]
    public void StateTracking_IsAccurate()
    {
        var handler = new ChatSubmissionHandler();

        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.Null(handler.LastSubmittedText);
        Assert.Equal(0, handler.SubmissionCount);
        Assert.True(handler.IsInputEnabled);

        handler.TryBeginSubmit("First");
        Assert.Equal(SubmissionState.Submitting, handler.State);
        Assert.Equal("First", handler.LastSubmittedText);
        Assert.Equal(1, handler.SubmissionCount);
        Assert.False(handler.IsInputEnabled);

        handler.OnMessagePosted();
        Assert.Equal(SubmissionState.WaitingForResponse, handler.State);
        Assert.Equal("First", handler.LastSubmittedText);
        Assert.Equal(1, handler.SubmissionCount);
        Assert.False(handler.IsInputEnabled);

        handler.OnResponseAppeared();
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.Equal("First", handler.LastSubmittedText);
        Assert.Equal(1, handler.SubmissionCount);
        Assert.True(handler.IsInputEnabled);

        handler.TryBeginSubmit("Second");
        Assert.Equal(SubmissionState.Submitting, handler.State);
        Assert.Equal("Second", handler.LastSubmittedText);
        Assert.Equal(2, handler.SubmissionCount);
    }

    /// <summary>
    /// Reproduces the prod "twice generating response" symptom: ThreadChatView.SubmitMessageCore
    /// calls ForceRelease immediately after Submit so the input stays enabled for queueing.
    /// A double-click (or Enter+button race) then re-enters TryBeginSubmit with the SAME text
    /// — the state is already Idle, so the second call wrongly succeeds, the second user cell
    /// is created, and the server watcher dispatches a second round.
    /// </summary>
    [Fact]
    public void DoubleClick_SameTextWithinDebounce_RejectsSecondSubmission()
    {
        var handler = new ChatSubmissionHandler();

        Assert.True(handler.TryBeginSubmit("Hello"));
        Assert.Equal(1, handler.SubmissionCount);

        handler.ForceRelease();

        Assert.False(handler.TryBeginSubmit("Hello"),
            "duplicate Send within the debounce window should be ignored");
        Assert.Equal(1, handler.SubmissionCount);
    }

    /// <summary>
    /// Genuinely different text after force-release must still go through — that's the
    /// queueing UX (user types another message while the previous is processing).
    /// </summary>
    [Fact]
    public void ForceRelease_ThenDifferentText_SecondSubmissionAccepted()
    {
        var handler = new ChatSubmissionHandler();

        Assert.True(handler.TryBeginSubmit("Hello"));
        handler.ForceRelease();

        Assert.True(handler.TryBeginSubmit("How are you"),
            "different text after force-release is a real second submission, must go through");
        Assert.Equal(2, handler.SubmissionCount);
    }

    [Fact]
    public void ConcurrentSubmitAttempts_OnlyOneSucceeds()
    {
        var handler = new ChatSubmissionHandler();

        var results = new bool[5];
        for (int i = 0; i < results.Length; i++)
        {
            results[i] = handler.TryBeginSubmit($"Message {i}");
        }

        Assert.True(results[0]);
        for (int i = 1; i < results.Length; i++)
        {
            Assert.False(results[i]);
        }

        Assert.Equal(1, handler.SubmissionCount);
        Assert.Equal("Message 0", handler.LastSubmittedText);
    }

    [Fact]
    public void ForceRelease_ResetsToIdle()
    {
        var handler = new ChatSubmissionHandler();

        handler.TryBeginSubmit("Hello");
        handler.OnMessagePosted();
        Assert.Equal(SubmissionState.WaitingForResponse, handler.State);

        handler.ForceRelease();

        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.True(handler.IsInputEnabled);
    }

    [Fact]
    public void OnMessagePosted_IgnoredWhenNotSubmitting()
    {
        var handler = new ChatSubmissionHandler();

        handler.OnMessagePosted();
        Assert.Equal(SubmissionState.Idle, handler.State);
    }

    [Fact]
    public void DisposedHandler_RejectsSubmissions()
    {
        var handler = new ChatSubmissionHandler();

        handler.Dispose();

        Assert.False(handler.TryBeginSubmit("Hello"));
        Assert.Equal(0, handler.SubmissionCount);
    }

    /// <summary>
    /// Regression for GitHub issue #380 ("the agent chat still requires refresh from time to time"):
    /// ThreadChatView.SubmitMessageCore latches State = Submitting via TryBeginSubmit and only clears
    /// it with the trailing ForceRelease on the happy path. Before the fix, any throw between those
    /// two points left State latched at Submitting forever — IsInputEnabled stayed false, so the Send
    /// button rendered as a dead "Sending…" spinner and every later TryBeginSubmit early-returned;
    /// only a page refresh (a fresh handler) recovered. The BeginSubmitScope failsafe now guarantees
    /// the release: disposing the scope while a submission is still in flight (exactly what the
    /// compiler-generated `using` finally does during stack unwind) forces the handler back to Idle.
    /// </summary>
    [Fact]
    public void SubmitScope_BodyThrowsAfterTryBeginSubmit_ReleasesToIdle()
    {
        var handler = new ChatSubmissionHandler();
        var rethrown = false;

        try
        {
            Assert.True(handler.TryBeginSubmit("Hello"));
            Assert.Equal(SubmissionState.Submitting, handler.State);
            Assert.False(handler.IsInputEnabled);

            using (handler.BeginSubmitScope())
            {
                // Simulate a throw somewhere in the submit pipeline (ns computation, StartThread,
                // JSON serialization, …) BEFORE the trailing ForceRelease would run.
                throw new InvalidOperationException("simulated submit-pipeline failure");
            }
        }
        catch (InvalidOperationException)
        {
            rethrown = true;
        }

        Assert.True(rethrown, "the failsafe scope must not swallow the exception — the caller still logs/surfaces it");
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.True(handler.IsInputEnabled);

        // And the composer is usable again immediately — no refresh needed.
        Assert.True(handler.TryBeginSubmit("Another message"));
    }

    /// <summary>
    /// Happy path: the caller releases (ForceRelease for Claude-Code-style queueing) BEFORE the
    /// scope exits, so disposing the scope is a no-op and leaves the queueing state untouched.
    /// </summary>
    [Fact]
    public void SubmitScope_ReleasedInsideScope_DisposeIsNoOp()
    {
        var handler = new ChatSubmissionHandler();

        Assert.True(handler.TryBeginSubmit("Hello"));
        using (handler.BeginSubmitScope())
        {
            // The submit body posts the message and force-releases so the input stays enabled
            // for queueing while the round is processed server-side.
            handler.ForceRelease();
            Assert.Equal(SubmissionState.Idle, handler.State);
        }

        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.True(handler.IsInputEnabled);
    }
}
