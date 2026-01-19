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
    public void SameValueTwice_ShouldSaveBothTimes()
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

        // Assert - both should be saved (Throttle doesn't use DistinctUntilChanged)
        Assert.Equal(2, savedValues.Count);
        Assert.Equal("Hello", savedValues[0]);
        Assert.Equal("Hello", savedValues[1]);
    }
}
