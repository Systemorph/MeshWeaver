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
}
