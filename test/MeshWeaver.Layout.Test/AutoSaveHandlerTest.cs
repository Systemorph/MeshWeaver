using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using MeshWeaver.Blazor.Components;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests for AutoSaveHandler which uses leading-edge throttle (ThrottleImmediate):
/// first value emits immediately, then subsequent values are suppressed for the
/// cooldown interval. After cooldown, the latest suppressed value is emitted.
/// </summary>
public class AutoSaveHandlerTest
{
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void SingleEdit_ShouldSaveImmediately()
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

        // Assert - leading-edge: should save immediately
        Assert.Single(savedValues);
        Assert.Equal("Hello", savedValues[0]);

        // Advance time past throttle interval — no additional saves
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);
        Assert.Single(savedValues);
    }

    [Fact]
    public void RapidEdits_ShouldSaveFirstAndFinalValue()
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

        // Assert - first character saved immediately at leading edge
        Assert.Equal("H", savedValues[0]);

        // Advance past throttle interval to trigger pending save
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Assert - final value should also be saved (from pending after cooldown)
        Assert.Equal("Hello World", savedValues[^1]);
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

        // Assert - should have saved all three values (each as leading edge of its session)
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

        // Act - type "Hello" character by character
        handler.OnValueChanged("H");       // t=0: emit immediately (leading edge)
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("He");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hel");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hell");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hello");

        // First value saved immediately
        Assert.Equal("H", savedValues[0]);

        // Pause for longer than throttle interval
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // "Hello" should be saved as the pending value after cooldown
        Assert.Contains("Hello", savedValues);

        var countAfterFirstSession = savedValues.Count;

        // Continue typing second session
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

        // Assert - "Hello World" should be the final saved value
        Assert.Equal("Hello World", savedValues[^1]);
        Assert.True(savedValues.Count > countAfterFirstSession,
            "Second editing session should have produced additional saves");
    }

    [Fact]
    public void ContinuousTyping_ShouldSavePeriodically()
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

        // Leading-edge throttle saves periodically: first immediately, then every ~500ms
        // With 100ms intervals and 500ms cooldown, expect ~20 saves during 10s of typing
        Assert.True(savedValues.Count > 1, "Leading-edge throttle should save periodically during continuous typing");
        Assert.Equal("Text0", savedValues[0]); // First value saved immediately

        // Finally pause
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Final value should be saved
        Assert.Equal("Text99", savedValues[^1]);
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
        handler.OnValueChanged("H");  // saved immediately (leading edge)
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("He");  // pending (in cooldown)
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);

        // Stale echo of "H" from stream — should be rejected because we have pending local changes
        Assert.False(handler.ShouldApplyExternalUpdate("H"),
            "Stale echo 'H' should be rejected when local value is 'He'");

        // Continue typing
        handler.OnValueChanged("Hel");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hell");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("Hello");

        // Wait for throttle cooldown to trigger pending save
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // "Hello" should be the final saved value
        Assert.Equal("Hello", savedValues[^1]);
        Assert.Equal("Hello", handler.LastSyncedValue);

        // Echo of our own synced value should be rejected as no-op
        Assert.False(handler.ShouldApplyExternalUpdate("Hello"),
            "Echo of our own synced value should be rejected as no-op");
    }

    /// <summary>
    /// External update during local editing should not overwrite local changes.
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

        // Start typing — first value saved immediately
        handler.OnValueChanged("My ");
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        handler.OnValueChanged("My text");

        // External update arrives while we're still typing (in cooldown)
        // This should be rejected to protect our local work
        Assert.False(handler.ShouldApplyExternalUpdate("Someone else's text"),
            "External update should be rejected while we have pending local changes");

        // Continue typing
        handler.OnValueChanged("My text here");

        // Wait for sync
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        // Our final value should be saved
        Assert.Equal("My text here", savedValues[^1]);
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

        // Type and sync immediately (leading edge)
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

        // Sync "Hello" — immediate save (leading edge)
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

        // Type first character — leading edge: saved immediately
        handler.OnValueChanged("H");
        Assert.Equal("H", handler.LastSyncedValue); // Synced immediately
        Assert.Equal("H", handler.CurrentValue);

        // Type more (in cooldown — not synced yet)
        handler.OnValueChanged("He");
        handler.OnValueChanged("Hel");
        Assert.Equal("H", handler.LastSyncedValue); // Still first value
        Assert.Equal("Hel", handler.CurrentValue); // Always current

        // Wait for cooldown — pending value syncs
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);

        Assert.Equal("Hel", handler.LastSyncedValue);
        Assert.Equal("Hel", handler.CurrentValue);

        // Type more — note: emitting "Hel" started a new cooldown, so "Hello" is pending
        handler.OnValueChanged("Hello");
        Assert.Equal("Hel", handler.LastSyncedValue); // Still in cooldown from "Hel" emit
        Assert.Equal("Hello", handler.CurrentValue); // Tracked immediately

        // Wait for cooldown to emit "Hello"
        scheduler.AdvanceBy(ThrottleInterval.Ticks + 1);
        Assert.Equal("Hello", handler.LastSyncedValue); // Now synced
        Assert.Equal("Hello", handler.CurrentValue);
    }
}
