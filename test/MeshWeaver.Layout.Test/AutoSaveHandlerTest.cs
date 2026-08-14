using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using MeshWeaver.Blazor.Components;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests for AutoSaveHandler to verify throttled auto-save behavior.
/// These tests reproduce the issue where only the first edit is saved
/// when typing a full sentence.
/// </summary>
public class AutoSaveHandlerTest
{
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void SingleEdit_ShouldSaveAfterThrottleInterval()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Act - make a single edit
        handler.OnValueChanged("Hello");

        // Assert - should not save immediately
        Assert.Empty(savedValues);

        // Advance time past throttle interval
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Assert - should save after throttle
        Assert.Single(savedValues);
        Assert.Equal("Hello", savedValues[0]);
    }

    [Fact]
    public void RapidEdits_ShouldSaveOnlyFinalValue()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Act - simulate typing "Hello World" character by character, rapidly
        var text = "Hello World";
        for (int i = 1; i <= text.Length; i++)
        {
            handler.OnValueChanged(text[..i]);
            // Small delay between keystrokes (50ms, less than throttle interval)
            scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        }

        // Assert - should not have saved anything yet (still within throttle window)
        Assert.Empty(savedValues);

        // Advance past throttle interval to trigger save
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Assert - should save only the final value, not intermediate values
        Assert.Single(savedValues);
        Assert.Equal("Hello World", savedValues[0]);
    }

    [Fact]
    public void MultipleEditSessions_ShouldSaveEachSession()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Act - first editing session
        handler.OnValueChanged("First");
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Second editing session
        handler.OnValueChanged("Second");
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Third editing session
        handler.OnValueChanged("Third");
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Assert - should have saved all three values
        Assert.Equal(3, savedValues.Count);
        Assert.Equal("First", savedValues[0]);
        Assert.Equal("Second", savedValues[1]);
        Assert.Equal("Third", savedValues[2]);
    }

    [Fact]
    public void EditSession_ThenPause_ThenMoreEdits_ShouldSaveBoth()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Act - type "Hello", pause, type " World"
        handler.OnValueChanged("H");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("He");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hel");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hell");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hello");

        // Pause for longer than throttle interval
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Assert - first save should have occurred
        Assert.Single(savedValues);
        Assert.Equal("Hello", savedValues[0]);

        // Continue typing
        handler.OnValueChanged("Hello ");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hello W");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hello Wo");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hello Wor");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hello Worl");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hello World");

        // Pause for longer than throttle interval
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Assert - second save should have occurred
        Assert.Equal(2, savedValues.Count);
        Assert.Equal("Hello World", savedValues[1]);
    }

    [Fact]
    public void ContinuousTyping_ShouldNotSaveUntilPause()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Act - continuous typing without any pause longer than throttle
        for (int i = 0; i < 100; i++)
        {
            handler.OnValueChanged($"Text{i}");
            // Each keystroke is 100ms apart, less than 500ms throttle
            scheduler.AdvanceBy(TimeSpan.FromMilliseconds(100).Ticks);
        }

        // Assert - nothing should be saved yet because we never paused long enough
        Assert.Empty(savedValues);

        // Finally pause
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Assert - should save only the final value
        Assert.Single(savedValues);
        Assert.Equal("Text99", savedValues[0]);
    }

    [Fact]
    public void SameValueTwice_ShouldSaveOnlyOnce()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Act - same value twice with pause in between
        handler.OnValueChanged("Hello");
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        handler.OnValueChanged("Hello");
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Assert - should save only once (skip duplicate sync of same value)
        Assert.Single(savedValues);
        Assert.Equal("Hello", savedValues[0]);
    }

    /// <summary>
    /// Bug reproduction: Fast typing with stale echo arriving mid-typing should not lose characters.
    /// Scenario:
    /// 1. User types "H", sync sends "H"
    /// 2. User types "e" before stream responds, current value is "He"
    /// 3. Stream echoes back "H" - this should NOT overwrite "He"
    /// </summary>
    [Fact]
    public void FastTyping_WithStaleEcho_ShouldNotLoseCharacters()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Act - simulate fast typing
        handler.OnValueChanged("H");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("He");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);

        // At this point, no sync has happened yet (still within throttle)
        // Simulate receiving stale echo from stream (the "H" we sent earlier)
        // This should be rejected because we have pending local changes
        Assert.False(handler.ShouldApplyExternalUpdate("H"),
            "Stale echo 'H' should be rejected when local value is 'He'");

        // Continue typing
        handler.OnValueChanged("Hel");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hell");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hello");

        // Wait for throttle to trigger sync
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Assert - sync should have saved "Hello"
        Assert.Single(savedValues);
        Assert.Equal("Hello", savedValues[0]);
        Assert.Equal("Hello", handler.LastSyncedValue);

        // Now if stream echoes back "Hello", it should be accepted
        // (but marked as no-op since it matches our synced value)
        Assert.False(handler.ShouldApplyExternalUpdate("Hello"),
            "Echo of our own synced value should be rejected as no-op");
    }

    /// <summary>
    /// Bug reproduction: External update during local editing should not overwrite local changes.
    /// Scenario: Another user makes a change while we're typing - we should not lose our work.
    /// </summary>
    [Fact]
    public void ExternalUpdate_DuringLocalEditing_ShouldNotOverwriteLocalChanges()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Start typing
        handler.OnValueChanged("My ");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("My text");

        // External update arrives while we're still typing (before throttle fires)
        // This should be rejected to protect our local work
        Assert.False(handler.ShouldApplyExternalUpdate("Someone else's text"),
            "External update should be rejected while we have pending local changes");

        // Continue typing
        handler.OnValueChanged("My text here");

        // Wait for sync
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Our value should have been saved
        Assert.Single(savedValues);
        Assert.Equal("My text here", savedValues[0]);
    }

    /// <summary>
    /// After sync is complete and no local changes, external updates should be applied.
    /// </summary>
    [Fact]
    public void ExternalUpdate_AfterSyncComplete_ShouldBeApplied()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Type and wait for sync
        handler.OnValueChanged("Hello");
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Sync completed
        Assert.Single(savedValues);
        Assert.Equal("Hello", handler.LastSyncedValue);
        Assert.Equal("Hello", handler.CurrentValue);

        // Now an external update arrives (from another user)
        // Since we have no pending changes, this should be accepted
        Assert.True(handler.ShouldApplyExternalUpdate("Hello World"),
            "External update should be accepted when no pending local changes");

        // Apply the update
        handler.OnExternalUpdateApplied("Hello World");

        // Verify state is updated
        Assert.Equal("Hello World", handler.LastSyncedValue);
        Assert.Equal("Hello World", handler.CurrentValue);
    }

    /// <summary>
    /// Verify that echo of last synced value is always rejected (prevents flicker).
    /// </summary>
    [Fact]
    public void EchoOfLastSyncedValue_ShouldAlwaysBeRejected()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Sync "Hello"
        handler.OnValueChanged("Hello");
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        Assert.Equal("Hello", handler.LastSyncedValue);

        // Echo of our own sync should be rejected
        Assert.False(handler.ShouldApplyExternalUpdate("Hello"),
            "Echo of our own synced value should always be rejected");
    }

    /// <summary>
    /// Verify LastSyncedValue and CurrentValue are tracked correctly.
    /// </summary>
    [Fact]
    public void StateTracking_ShouldBeAccurate()
    {
        // Arrange
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        using var handler = new AutoSaveHandler(
            ThrottleInterval,
            value => savedValues.Add(value),
            scheduler);

        // Initially null
        Assert.Null(handler.LastSyncedValue);
        Assert.Null(handler.CurrentValue);

        // Type first character
        handler.OnValueChanged("H");
        Assert.Null(handler.LastSyncedValue); // Not synced yet
        Assert.Equal("H", handler.CurrentValue); // Tracked immediately

        // Type more
        handler.OnValueChanged("He");
        handler.OnValueChanged("Hel");
        Assert.Null(handler.LastSyncedValue); // Still not synced
        Assert.Equal("Hel", handler.CurrentValue); // Always current

        // Wait for sync
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Now synced
        Assert.Equal("Hel", handler.LastSyncedValue);
        Assert.Equal("Hel", handler.CurrentValue);

        // Type more
        handler.OnValueChanged("Hello");
        Assert.Equal("Hel", handler.LastSyncedValue); // Still old value
        Assert.Equal("Hello", handler.CurrentValue); // Updated
    }

    /// <summary>
    /// 🚨 Issue #1606 — the learner's edit that never reached the node.
    ///
    /// <para>A course example cell renders as an inline editor and re-renders on every emission of
    /// the node it is bound to. The Blazor views create this handler in <c>BindData</c> and register
    /// it with <c>AddBinding</c>, and <c>OnParametersSet</c> runs <c>DisposeBindings()</c> →
    /// <c>BindData()</c> — so a re-render inside the 500 ms throttle window disposed the handler
    /// while <c>Throttle</c> was still holding the keystroke. The value went nowhere, with no error
    /// anywhere: the learner typed, reloaded, and their work was gone.</para>
    /// </summary>
    [Fact]
    public void DisposeInsideTheThrottleWindow_FlushesThePendingEdit()
    {
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();
        var handler = new AutoSaveHandler(ThrottleInterval, savedValues.Add, scheduler);

        handler.OnValueChanged("the learner's edit");
        // Less than the throttle interval: the value is still held, nothing has been saved.
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(200).Ticks);
        Assert.Empty(savedValues);

        handler.Dispose();

        Assert.Equal(new[] { "the learner's edit" }, savedValues);
    }

    /// <summary>
    /// The flush must not manufacture a write. A handler disposed with nothing pending — or with
    /// only what was already saved — writes nothing, or every re-render would post a redundant
    /// patch for a cell nobody touched.
    /// </summary>
    [Fact]
    public void DisposeWithNothingPending_SavesNothing()
    {
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();
        var handler = new AutoSaveHandler(ThrottleInterval, savedValues.Add, scheduler);

        handler.Dispose();

        Assert.Empty(savedValues);
    }

    [Fact]
    public void DisposeAfterTheThrottleAlreadyFired_DoesNotSaveTwice()
    {
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();
        var handler = new AutoSaveHandler(ThrottleInterval, savedValues.Add, scheduler);

        handler.OnValueChanged("saved once");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(600).Ticks);
        Assert.Equal(new[] { "saved once" }, savedValues);

        handler.Dispose();

        Assert.Equal(new[] { "saved once" }, savedValues);
    }

    /// <summary>
    /// An external update that the editor adopted is the new baseline, so disposing afterwards must
    /// not write it back — that would turn a read into a write on every re-render.
    /// </summary>
    [Fact]
    public void DisposeAfterAnExternalUpdateWasApplied_SavesNothing()
    {
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();
        var handler = new AutoSaveHandler(ThrottleInterval, savedValues.Add, scheduler);

        handler.OnExternalUpdateApplied("text from the stream");
        handler.Dispose();

        Assert.Empty(savedValues);
    }

    /// <summary>
    /// The realistic sequence: type, re-render (dispose + rebuild), type again, re-render again.
    /// Every keystroke run must survive its re-render — a cell bound to a node that emits on each
    /// save re-renders constantly, which is why the drop was total rather than occasional.
    /// </summary>
    [Fact]
    public void EveryRebindFlushesItsOwnPendingEdit()
    {
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();

        var first = new AutoSaveHandler(ThrottleInterval, savedValues.Add, scheduler);
        first.OnValueChanged("edit one");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(100).Ticks);
        first.Dispose();

        var second = new AutoSaveHandler(ThrottleInterval, savedValues.Add, scheduler);
        second.OnValueChanged("edit one, edit two");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(100).Ticks);
        second.Dispose();

        Assert.Equal(new[] { "edit one", "edit one, edit two" }, savedValues);
    }

    [Fact]
    public void OnValueChangedAfterDispose_IsStillIgnored()
    {
        var scheduler = new TestScheduler();
        var savedValues = new List<string>();
        var handler = new AutoSaveHandler(ThrottleInterval, savedValues.Add, scheduler);

        handler.Dispose();
        handler.OnValueChanged("too late");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(600).Ticks);

        Assert.Empty(savedValues);
    }
}
