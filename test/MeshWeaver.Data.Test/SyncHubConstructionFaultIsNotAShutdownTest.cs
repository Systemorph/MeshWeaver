using System;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Systemorph/MeshWeaver#5592. A <see cref="SynchronizationStream{TStream}"/> that cannot get its
/// <c>sync/{id}</c> sub-hub refuses to exist. It used to refuse with
/// <see cref="HubDisposingException"/> ("Hub X is shutting down — retry") for EVERY null hub,
/// including a sub-hub whose construction RAN and FAULTED on a host that was fully alive.
///
/// <para><b>The production reading.</b> <c>mkleiner/_Install/SocialMedia</c> logged
/// <c>initialization failed — BuildupAction 1 of 2 (DataExtensions.StartDataSourcesAndOpenGate)
/// faulted (HubDisposingException: Hub mkleiner/_Install/SocialMedia is shutting down …)</c>.
/// That line comes from the arm of <c>MessageHub.HandleInitialize</c> that runs only when
/// <c>IsShuttingDown</c> is FALSE, so the host was not shutting down. <c>IsShuttingDown</c> reads
/// the same monotonic flags the creation freeze reads, so the only null that can arrive with it
/// false is <see cref="HostedHubOutcome.ConstructionFaulted"/>. The issue was filed as "an
/// expected shutdown race marked FAILED", and the fix it proposed (absorb the exception during
/// init) would have hidden a real construction fault behind a teardown label.</para>
///
/// <para>The fault is injected where production's comes from, inside the sub-hub's
/// construction: the host serves <see cref="AccessService"/> transiently and the factory throws
/// while the test holds it armed. The child's configuration resolves that service from its
/// parent, so <c>HostedHubsCollection.CreateHub</c> reports <c>ConstructionFaulted</c> with the
/// real exception on a live host. No mocks.</para>
/// </summary>
public class SyncHubConstructionFaultIsNotAShutdownTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Empty;

    private volatile bool faultArmed;

    private sealed class SubHubConstructionFault(string message) : Exception(message);

    private IMessageHub FaultableHost(string id)
        => GetHost().GetHostedHub(
            new Address("faultable-host", id),
            c => c.WithServices(s => s.AddTransient(_ => faultArmed
                ? throw new SubHubConstructionFault("the sub-hub's construction blew up")
                : new AccessService())))!;

    [HubFact]
    public void ASubHubWhoseConstructionFaults_IsReportedAsThatFault_NotAsAShutdown()
    {
        var host = FaultableHost("faulted");
        var reduceManager = new ReduceManager<Empty>(host);

        faultArmed = true;
        Exception? thrown;
        try
        {
            thrown = Record.Exception(() => _ = new SynchronizationStream<Empty>(
                new StreamIdentity(host.Address, null),
                host,
                new EntityReference("X", "Y"),
                reduceManager,
                null));
        }
        finally
        {
            faultArmed = false;
        }

        host.IsShuttingDown.Should().BeFalse(
            "the premise: nothing is tearing this host down. A refusal that claims otherwise is false");
        thrown.Should().NotBeNull("a stream that cannot own its sub-hub still refuses to exist");
        (thrown is ObjectDisposedException).Should().BeFalse(
            "HubDisposingException is an ObjectDisposedException, and every teardown classifier "
            + "reads that family as 'the host is going away, retry'. For a construction fault on a "
            + "live host that sends the reader after a race that never happened (#5592)");
        // The container wraps a factory's throw in its own resolution exception, so the injected
        // fault is found in the chain, not necessarily one level down.
        ExceptionChain.Contains<SubHubConstructionFault>(thrown).Should().BeTrue(
            "the refusal must carry the fault that actually happened");
        thrown!.Message.Should().Contain("the sub-hub's construction blew up");
        thrown.Message.Should().Contain(host.Address.ToString());
    }

    /// <summary>
    /// Control: the freeze is still a shutdown. If the change had turned EVERY null into a
    /// construction fault, a disposing host's refusal would stop reading as transient, and the
    /// callers that retry on it (Blazor's BindStream catch, the ShuttingDown NACK) would stop
    /// retrying.
    /// </summary>
    [HubFact]
    public void ASubHubRefusedByTheCreationFreeze_IsStillAShutdown()
    {
        var host = FaultableHost("frozen");
        var reduceManager = new ReduceManager<Empty>(host);

        // Dispose() freezes hosted-hub creation synchronously, at its first statement.
        host.Dispose();

        Action act = () => _ = new SynchronizationStream<Empty>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            reduceManager,
            null);

        act.Should().Throw<HubDisposingException>(
            "a host whose hosted-hub creation is frozen IS going down, and that refusal is transient")
            .Which.HubAddress.Should().Be(host.Address);
    }
}
