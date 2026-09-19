using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins how <see cref="MessageHubGrain"/> REPORTS a missing hub (Systemorph/MeshWeaver#3243).
///
/// <para>The defect: the grain could not tell an expected teardown race from a hub configuration
/// that threw — <c>GetHostedHub</c> answered null for both — so it logged one fail-level line that
/// named both possibilities and committed to neither. The level a call site chooses is a TICKETING
/// decision (<c>Doc/Architecture/LogWatchTriage</c>: everything red becomes an incident and an
/// issue), so every pod rollout fingerprinted and ticketed a shutdown the message itself
/// anticipated — incident <c>e2028eb86d6a85a6</c> and its sibling <c>8bd7c9c44c12e40b</c>.</para>
///
/// <para>Both halves are pure functions on purpose — the classification IS the fix, so it is
/// pinned here rather than inferred from reading the call site, and no cluster is needed
/// (same shape as <c>MessageHubGrainActivationSourceTest</c>).</para>
/// </summary>
public class HubConstructionOutcomeReportingTest
{
    private static MeshNode Node() => new("Admin/PlatformVersion") { NodeType = "Markdown" };

    /// <summary>
    /// The expected shutdown race: benign, so it must not reach <c>fail:</c> — and it must SAY it
    /// is a shutdown rather than listing possibilities.
    /// </summary>
    [Fact]
    public void HostShuttingDown_IsNotReportedAsAFault()
    {
        MessageHubGrain.HubConstructionFailureLevel(HostedHubOutcome.HostShuttingDown)
            .Should().Be(LogLevel.Debug,
                "a teardown race is expected: nothing failed, nothing was written, and the next "
                + "access re-activates on a live host — logging it red tickets every pod rollout");

        var reason = MessageHubGrain.HubConstructionFailureReason(Node(), HostedHubOutcome.HostShuttingDown);

        reason.Should().Contain("Admin/PlatformVersion").And.Contain("Markdown",
            "the caller is still answered, and answered accurately");
        reason.Should().Contain("shutting down");
        reason.Should().NotContain("Either the hub configuration threw",
            "the whole defect was a sentence that named both conditions and committed to neither");
    }

    /// <summary>
    /// The real fault: stays loud, and keeps pointing at the entry that carries the stack.
    /// Downgrading THIS would hide a broken NodeType.
    /// </summary>
    [Fact]
    public void ConfigurationThatThrows_StaysAtFailLevel()
    {
        MessageHubGrain.HubConstructionFailureLevel(HostedHubOutcome.ConstructionFaulted)
            .Should().Be(LogLevel.Error, "a configuration that threw is a defect someone must fix");

        var reason = MessageHubGrain.HubConstructionFailureReason(Node(), HostedHubOutcome.ConstructionFaulted);

        reason.Should().Contain("Admin/PlatformVersion").And.Contain("Markdown");
        reason.Should().Contain("Failed to create hosted hub",
            "the real exception is logged there, and the reader must be sent to it");
        reason.Should().NotContain("expected teardown race");
    }

    /// <summary>
    /// An unclassified answer is an UNKNOWN, not a shutdown — the one way this fix could silently
    /// become a mute button is by treating "no classification" as benign.
    /// </summary>
    [Theory]
    [InlineData(HostedHubOutcome.Unclassified)]
    [InlineData(HostedHubOutcome.Available)]
    [InlineData(HostedHubOutcome.Absent)]
    public void EverythingElse_StaysAtFailLevel(HostedHubOutcome outcome)
        => MessageHubGrain.HubConstructionFailureLevel(outcome)
            .Should().Be(LogLevel.Error,
                "only a KNOWN shutdown is benign; an unknown must never be quietly downgraded");

    // ── The SIBLING reporter (#3243, second half) ────────────────────────────────────────────────
    //
    // 🚨 Why the first half did not close the incident. The fingerprint identifies the FAULT, not
    // the reporter: with a frame present the reporting category is excluded from the identity, and
    // the discriminating text is the EXCEPTION's message rather than the reporter's prose
    // (Doc/Architecture/LogWatchTriage) — deliberately, so one fault printed by two catch sites
    // stays one ticket (#1170/#1171). The activation chain's error arm reported EVERY fault at
    // fail level, so the same teardown-race ObjectDisposedException kept landing on
    // e2028eb86d6a85a6 through the arm the classification had not reached. Measured: the
    // 2026-09-18T05:39:52Z sighting is that arm's line, `activation faulted for
    // Admin/_Notification/…`, on a pod rolling.

    /// <summary>The #3243 signature exactly: Autofac's scope is gone underneath the activation.</summary>
    private static Exception DisposedScope() =>
        new ObjectDisposedException("LifetimeScope",
            "Instances cannot be resolved and nested lifetimes cannot be created from this "
            + "LifetimeScope as it (or one of its parent scopes) has already been disposed.");

    /// <summary>
    /// 🚨 THE PIN. Pre-fix this arm was an unconditional <c>LogError</c>, so the expected teardown
    /// race was ticketed on every rollout — through the sibling of the line the first half fixed.
    /// </summary>
    [Fact]
    public void AnActivationAbandonedByTeardown_IsNotReportedAsAFault()
    {
        MessageHubGrain.ActivationFaultLevel(DisposedScope())
            .Should().Be(LogLevel.Debug,
                "a scope closed underneath an activation is the same expected race the missing-hub "
                + "line already excuses — and this arm is where it was still being ticketed");

        var reason = MessageHubGrain.ActivationFaultReason("Admin/_Notification/abc", DisposedScope());
        reason.Should().Contain("Admin/_Notification/abc");
        reason.Should().Contain("tearing", "the reader must be told WHICH condition fired");
        reason.Should().Contain("re-activates", "and that nothing is lost");
    }

    /// <summary>
    /// The hub announcing its own disposal is the other half of the same fact, and
    /// <c>IsHubDisposal</c> walks the CHAIN because this arm receives the fault wrapped.
    /// </summary>
    [Fact]
    public void AHubDisposalRace_CountsToo_EvenWrapped()
        => MessageHubGrain.ActivationFaultLevel(
                new InvalidOperationException("wrapped",
                    new HubDisposingException(new Address("test", "1"), "stream")))
            .Should().Be(LogLevel.Debug,
                "the fault reaches this arm wrapped, so the classifier must walk the chain");

    /// <summary>
    /// 🚨 THE CONTROL, and the thing that keeps this from being a mute button: a real activation
    /// failure — a node that never resolved, a configuration that threw — stays red and keeps the
    /// wording somebody has already learned to grep for.
    /// </summary>
    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    public void ARealActivationFailure_StaysAtFailLevel(Type faultType)
    {
        var ex = (Exception)Activator.CreateInstance(faultType)!;

        MessageHubGrain.ActivationFaultLevel(ex)
            .Should().Be(LogLevel.Error,
                "only a teardown race is benign; everything else is a defect someone must fix");
        MessageHubGrain.ActivationFaultReason("Admin/PlatformVersion", ex)
            .Should().Be("activation faulted for Admin/PlatformVersion",
                "the unclassified wording is unchanged, so an existing search still finds it");
    }

    /// <summary>
    /// A null fault is an UNKNOWN, not a shutdown — the same trap
    /// <see cref="EverythingElse_StaysAtFailLevel"/> guards on the other arm.
    /// </summary>
    [Fact]
    public void ANullFault_IsNotTreatedAsBenign()
        => MessageHubGrain.ActivationFaultLevel(null)
            .Should().Be(LogLevel.Error,
                "no evidence of a teardown is not evidence of one");
}
