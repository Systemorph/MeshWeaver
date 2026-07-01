using System;
using MeshWeaver.Hosting.Orleans;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the bounded grain keep-alive decision (issue #147). A hung long-running operation — an AI stream
/// with no timeout — must NOT extend the grain lifetime forever: doing so pinned the thread grain in
/// memory (1376-message backlog, 1h+ wedge, recovery only via pod restart) because the 1-minute keep-alive
/// timer re-armed <c>DelayDeactivation</c> indefinitely while <c>_activeOperations &gt; 0</c>.
/// <see cref="MessageHubGrain.LongRunningOperationCapExceeded"/> is the pure predicate the timer uses to
/// STOP re-arming (and request deactivation) once the active run exceeds the cap, so Orleans idle-collects
/// the grain and <c>executionCts.Cancel()</c> (RegisterForDisposal) tears down the stuck call.
/// </summary>
public class MessageHubGrainKeepAliveTest
{
    private static readonly long Max = TimeSpan.FromMinutes(30).Ticks;

    [Fact]
    public void NoActiveRun_NeverExpires()
        // startedTicks == 0 means no long-running run is active (or the clock was cleared on →0) — never
        // capped, even at an arbitrarily-far "now".
        => Assert.False(MessageHubGrain.LongRunningOperationCapExceeded(
            startedTicks: 0, nowTicks: TimeSpan.FromHours(10).Ticks, maxDurationTicks: Max));

    [Fact]
    public void FreshRun_NotExpired()
        => Assert.False(MessageHubGrain.LongRunningOperationCapExceeded(
            startedTicks: 1000, nowTicks: 1000 + TimeSpan.FromMinutes(29).Ticks, maxDurationTicks: Max));

    [Fact]
    public void ExactlyAtCap_NotYetExpired()
        // Strictly-greater-than boundary — exactly at the cap is not yet expired.
        => Assert.False(MessageHubGrain.LongRunningOperationCapExceeded(
            startedTicks: 1000, nowTicks: 1000 + Max, maxDurationTicks: Max));

    [Fact]
    public void RunPastCap_Expired()
        // The #147 hung stream: active past 30 min → capped → the timer stops extending and calls
        // DeactivateOnIdle so the grain can recover.
        => Assert.True(MessageHubGrain.LongRunningOperationCapExceeded(
            startedTicks: 1000, nowTicks: 1000 + TimeSpan.FromMinutes(31).Ticks, maxDurationTicks: Max));
}
