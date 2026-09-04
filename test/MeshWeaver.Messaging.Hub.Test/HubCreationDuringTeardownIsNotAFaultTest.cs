using System;
using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the distinction hosted-hub creation used to throw away (Systemorph/MeshWeaver#3243): a
/// null hub can mean the HOST IS GOING DOWN or that the HUB CONFIGURATION THREW, and those belong
/// at opposite log levels because everything a portal reports as red becomes an incident and a
/// GitHub issue (<c>Doc/Architecture/LogWatchTriage</c>).
///
/// <para>The production shape (incident <c>e2028eb86d6a85a6</c> plus its sibling
/// <c>8bd7c9c44c12e40b</c>, recurring across four deployments since mid-August): a
/// <c>MessageHubGrain</c> activated while its pod was stopping, the creation passed the collection's
/// <c>IsDisposing</c> check, and the Autofac scope then died UNDER the build —
/// <c>LifetimeScope.ThrowDisposedException</c>. <c>CreateHub</c> logged that as
/// <c>Failed to create hosted hub</c> at fail level and returned null; the grain, unable to tell
/// which of the two conditions it was, logged its own fail-level line naming BOTH. Two tickets per
/// pod rollout for a race nothing failed in.</para>
///
/// <para>All three cases below run against a REAL Autofac container built by the framework's own
/// <c>SetupModules</c> — the same call <c>MessageHubConfiguration.CreateServiceProvider</c> makes —
/// so the disposed-container case reproduces the incident's exact frame rather than simulating it.
/// No mocks: <see cref="HostedHubsCollection"/> is exercised through its own public surface.</para>
/// </summary>
public class HubCreationDuringTeardownIsNotAFaultTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// A configuration that faults while the host is fully alive — the condition that MUST stay
    /// loud. Reported as a shutdown it would hide a broken NodeType behind a Debug line.
    /// </summary>
    [Fact]
    public void ConfigurationThatThrows_IsReportedAsAFault_WithTheRealException()
    {
        var host = GetClient().ServiceProvider.CreateMessageHub(
            new Address("outcome-host", "faulted"),
            c => c.WithPostingIdentity(PostingIdentity.System));

        var boom = new InvalidOperationException("the NodeType's configuration lambda blew up");

        var result = host.TryGetHostedHub(
            new Address("outcome-child", "faulted"),
            _ => throw boom,
            HostedHubCreation.Always);

        result.Hub.Should().BeNull("the configuration threw, so no hub was produced");
        result.Outcome.Should().Be(HostedHubOutcome.ConstructionFaulted,
            "a configuration that throws while the host is live is a defect someone must look at");
        result.IsShutdownRace.Should().BeFalse("nothing here is a teardown race");
        result.Error.Should().BeSameAs(boom, "the caller's log line must carry the REAL cause, not a guess");

        host.Dispose();
    }

    /// <summary>
    /// The freeze half of the shutdown case: the collection already knows it is disposing, so
    /// creation is refused before it starts. No exception exists, and none is needed.
    /// </summary>
    [Fact]
    public void CreationRefusedByTheDisposalFreeze_IsReportedAsAShutdown()
    {
        var host = GetClient().ServiceProvider.CreateMessageHub(
            new Address("outcome-host", "frozen"),
            c => c.WithPostingIdentity(PostingIdentity.System));

        // Dispose freezes hosted-hub creation SYNCHRONOUSLY across the subtree
        // (TeardownHubCreationFreezeTest pins that); this asserts what the refusal is CALLED.
        host.Dispose();

        var result = host.TryGetHostedHub(
            new Address("outcome-child", "frozen"),
            c => c,
            HostedHubCreation.Always);

        result.Hub.Should().BeNull("a disposing host refuses new hosted hubs");
        result.Outcome.Should().Be(HostedHubOutcome.HostShuttingDown,
            "a refusal by the teardown freeze is an expected shutdown, never a hub-construction fault");
        result.IsShutdownRace.Should().BeTrue();
    }

    /// <summary>
    /// 🚨 The incident itself: the container dies UNDER a creation that had already passed the
    /// freeze check. Before the fix this was indistinguishable from
    /// <see cref="ConfigurationThatThrows_IsReportedAsAFault_WithTheRealException"/> — same null,
    /// same fail-level line — which is exactly why an expected pod rollout opened a ticket.
    /// </summary>
    [Fact]
    public void ContainerDisposedUnderALiveCollection_IsReportedAsAShutdown_NotAFault()
    {
        // A real child lifetime scope of the fixture's container — the same construction
        // MessageHubConfiguration.CreateServiceProvider performs for every hosted hub.
        var scope = new ServiceCollection().SetupModules(GetClient().ServiceProvider);

        // Built while the scope is ALIVE (its ctor resolves this collection's logger from it), so
        // the collection's own freeze flags stay false — nothing told it a shutdown is happening.
        var hubs = new HostedHubsCollection(scope, new Address("outcome-host", "disposed-container"));

        // The pod stops: the container goes down without the collection ever being disposed.
        ((IDisposable)scope).Dispose();

        var result = hubs.GetHubWithOutcome(
            new Address("outcome-child", "disposed-container"),
            c => c,
            HostedHubCreation.Always);

        result.Hub.Should().BeNull("hub construction cannot run against a disposed container");
        result.Outcome.Should().Be(HostedHubOutcome.HostShuttingDown,
            "a container that answers every resolution with ObjectDisposedException IS a host going "
            + "down — reporting it as a construction fault tickets every pod rollout (#3243)");
        result.IsShutdownRace.Should().BeTrue();
        result.Error.Should().BeOfType<ObjectDisposedException>(
            "the evidence rides along: downgrading the level is not licence to swallow the outcome");
    }
}
