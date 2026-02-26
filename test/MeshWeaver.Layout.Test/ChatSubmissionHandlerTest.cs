using System;
using MeshWeaver.Blazor.Components;
using Xunit;
using static MeshWeaver.Blazor.Components.ChatSubmissionHandler;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests for ChatSubmissionHandler to verify submission lifecycle,
/// concurrent submission prevention, and input enable/disable state.
/// </summary>
public class ChatSubmissionHandlerTest
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Helper that creates a handler with a controllable timeout callback.
    /// Returns the handler and an Action that fires the timeout when called.
    /// </summary>
    private static (ChatSubmissionHandler handler, Action fireTimeout) CreateWithControllableTimeout(TimeSpan? timeout = null)
    {
        Action? storedCallback = null;

        IDisposable ScheduleTimeout(TimeSpan delay, Action callback)
        {
            storedCallback = callback;
            return new CallbackDisposable(() => storedCallback = null);
        }

        var handler = new ChatSubmissionHandler(timeout ?? TestTimeout, ScheduleTimeout);
        void FireTimeout()
        {
            storedCallback?.Invoke();
        }

        return (handler, FireTimeout);
    }

    [Fact]
    public void SingleSubmission_Lifecycle()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Initially idle
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.True(handler.IsInputEnabled);
        Assert.Equal(0, handler.SubmissionCount);

        // Act — begin submission
        var result = handler.TryBeginSubmit("Hello");
        Assert.True(result);
        Assert.Equal(SubmissionState.Submitting, handler.State);
        Assert.False(handler.IsInputEnabled);
        Assert.Equal("Hello", handler.LastSubmittedText);
        Assert.Equal(1, handler.SubmissionCount);

        // Transition to WaitingForResponse
        handler.OnMessagePosted();
        Assert.Equal(SubmissionState.WaitingForResponse, handler.State);
        Assert.False(handler.IsInputEnabled);

        // Response appears
        handler.OnResponseAppeared();
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.True(handler.IsInputEnabled);
    }

    [Fact]
    public void RapidEnter_WhileSubmitting_ShouldReject()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Act — first enter succeeds
        Assert.True(handler.TryBeginSubmit("First message"));
        Assert.Equal(1, handler.SubmissionCount);

        // Second enter while still submitting → rejected
        Assert.False(handler.TryBeginSubmit("Second message"));
        Assert.Equal(1, handler.SubmissionCount);
        Assert.Equal("First message", handler.LastSubmittedText);
    }

    [Fact]
    public void TripleRapidEnter_ShouldSubmitOnlyOnce()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Act — three rapid enters
        var first = handler.TryBeginSubmit("Message");
        var second = handler.TryBeginSubmit("Message");
        var third = handler.TryBeginSubmit("Message");

        // Assert — only the first succeeded
        Assert.True(first);
        Assert.False(second);
        Assert.False(third);
        Assert.Equal(1, handler.SubmissionCount);
    }

    [Fact]
    public void SubmitCaptures_FullText_NotTruncated()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Act — simulate passing the full text directly (as GetValueAsync would)
        var fullText = "This is a complete message with no truncation";
        var result = handler.TryBeginSubmit(fullText);

        // Assert
        Assert.True(result);
        Assert.Equal(fullText, handler.LastSubmittedText);
    }

    [Fact]
    public void InputDisabled_DuringSubmission()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Initially enabled
        Assert.True(handler.IsInputEnabled);

        // Submitting → disabled
        handler.TryBeginSubmit("Hello");
        Assert.False(handler.IsInputEnabled);

        // WaitingForResponse → still disabled
        handler.OnMessagePosted();
        Assert.False(handler.IsInputEnabled);
    }

    [Fact]
    public void InputReEnabled_OnResponse()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        handler.TryBeginSubmit("Hello");
        handler.OnMessagePosted();

        // Act
        handler.OnResponseAppeared();

        // Assert
        Assert.True(handler.IsInputEnabled);
        Assert.Equal(SubmissionState.Idle, handler.State);
    }

    [Fact]
    public void Timeout_ReleasesInput()
    {
        // Arrange
        var (handler, fireTimeout) = CreateWithControllableTimeout(TimeSpan.FromSeconds(30));

        handler.TryBeginSubmit("Hello");
        handler.OnMessagePosted();
        Assert.False(handler.IsInputEnabled);

        // Act — fire the timeout
        fireTimeout();

        // Assert — should be back to Idle
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.True(handler.IsInputEnabled);
    }

    [Fact]
    public void RapidTypeSubmitTypeSubmit()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Act — first submission
        Assert.True(handler.TryBeginSubmit("Hello"));
        Assert.Equal(1, handler.SubmissionCount);
        Assert.Equal("Hello", handler.LastSubmittedText);

        handler.OnMessagePosted();

        // Attempt second submit while waiting → rejected
        Assert.False(handler.TryBeginSubmit("World"));
        Assert.Equal(1, handler.SubmissionCount);

        // Response arrives → back to Idle
        handler.OnResponseAppeared();

        // Second submission now succeeds
        Assert.True(handler.TryBeginSubmit("World"));
        Assert.Equal(2, handler.SubmissionCount);
        Assert.Equal("World", handler.LastSubmittedText);
    }

    [Fact]
    public void SubmitEmptyText_ShouldReject()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Act & Assert — null
        Assert.False(handler.TryBeginSubmit(null));
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.Equal(0, handler.SubmissionCount);

        // Empty string
        Assert.False(handler.TryBeginSubmit(""));
        Assert.Equal(SubmissionState.Idle, handler.State);

        // Whitespace only
        Assert.False(handler.TryBeginSubmit("   "));
        Assert.Equal(SubmissionState.Idle, handler.State);

        // Tab + newline
        Assert.False(handler.TryBeginSubmit("\t\n"));
        Assert.Equal(SubmissionState.Idle, handler.State);

        Assert.Equal(0, handler.SubmissionCount);
    }

    [Fact]
    public void StateTracking_IsAccurate()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Initially
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.Null(handler.LastSubmittedText);
        Assert.Equal(0, handler.SubmissionCount);
        Assert.True(handler.IsInputEnabled);

        // After TryBeginSubmit
        handler.TryBeginSubmit("First");
        Assert.Equal(SubmissionState.Submitting, handler.State);
        Assert.Equal("First", handler.LastSubmittedText);
        Assert.Equal(1, handler.SubmissionCount);
        Assert.False(handler.IsInputEnabled);

        // After OnMessagePosted
        handler.OnMessagePosted();
        Assert.Equal(SubmissionState.WaitingForResponse, handler.State);
        Assert.Equal("First", handler.LastSubmittedText);
        Assert.Equal(1, handler.SubmissionCount);
        Assert.False(handler.IsInputEnabled);

        // After OnResponseAppeared
        handler.OnResponseAppeared();
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.Equal("First", handler.LastSubmittedText); // LastSubmittedText preserved
        Assert.Equal(1, handler.SubmissionCount);
        Assert.True(handler.IsInputEnabled);

        // Second submission
        handler.TryBeginSubmit("Second");
        Assert.Equal(SubmissionState.Submitting, handler.State);
        Assert.Equal("Second", handler.LastSubmittedText);
        Assert.Equal(2, handler.SubmissionCount);
    }

    [Fact]
    public void ConcurrentSubmitAttempts_OnlyOneSucceeds()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Simulate multiple near-simultaneous TryBeginSubmit calls
        // (on Blazor's single synchronization context, these would be sequential)
        var results = new bool[5];
        for (int i = 0; i < results.Length; i++)
        {
            results[i] = handler.TryBeginSubmit($"Message {i}");
        }

        // Assert — only first wins
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
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        handler.TryBeginSubmit("Hello");
        handler.OnMessagePosted();
        Assert.Equal(SubmissionState.WaitingForResponse, handler.State);

        // Act
        handler.ForceRelease();

        // Assert
        Assert.Equal(SubmissionState.Idle, handler.State);
        Assert.True(handler.IsInputEnabled);
    }

    [Fact]
    public void Timeout_CancelledByResponseAppeared()
    {
        // Arrange
        var (handler, fireTimeout) = CreateWithControllableTimeout();

        handler.TryBeginSubmit("Hello");
        handler.OnMessagePosted();

        // Response arrives before timeout
        handler.OnResponseAppeared();
        Assert.Equal(SubmissionState.Idle, handler.State);

        // Now fire the timeout (should have been cancelled, so no effect)
        fireTimeout();
        Assert.Equal(SubmissionState.Idle, handler.State);
    }

    [Fact]
    public void OnMessagePosted_IgnoredWhenNotSubmitting()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Call OnMessagePosted when Idle — should be a no-op
        handler.OnMessagePosted();
        Assert.Equal(SubmissionState.Idle, handler.State);
    }

    [Fact]
    public void DisposedHandler_RejectsSubmissions()
    {
        // Arrange
        var (handler, _) = CreateWithControllableTimeout();

        // Act
        handler.Dispose();

        // Assert
        Assert.False(handler.TryBeginSubmit("Hello"));
        Assert.Equal(0, handler.SubmissionCount);
    }

    /// <summary>
    /// Helper disposable that runs a callback on disposal.
    /// </summary>
    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
