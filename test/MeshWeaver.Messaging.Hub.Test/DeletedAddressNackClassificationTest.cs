using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the FORK in the abandoned-delivery NACK: a hub tearing down because its NODE WAS DELETED
/// must answer the authoritative <see cref="ErrorType.NotFound"/>; every other teardown keeps the
/// transient <see cref="ErrorType.ShuttingDown"/>.
///
/// <para><b>Why the fork exists (#1029).</b> <c>ShuttingDown</c> means "ask again — this address
/// may reactivate", and consumers with their own recovery machinery act on it:
/// <c>JsonSynchronizationStream</c>'s SubscribeRequest error arm RIDES IT OUT, keeping the stream
/// and its keep-alive alive so the change-feed resubscribe latch can rehydrate after a recycle.
/// That is correct for a recycle and catastrophic for a delete: the address never comes back, no
/// announce ever arrives, and the consumer is parked in silence. The reader then burns its entire
/// budget and reports "unavailable" for a node that is provably gone — measured end-to-end as a
/// 30&#160;s silent read in <c>PostDeleteReadVerdictTest</c>, whose CI face is
/// <c>MeshPluginTest.FullCrudWorkflow_CreateGetUpdateDelete</c> asserting "Not found" and getting
/// "Unavailable". The stranded keep-alive then heartbeats the nonexistent owner forever, which is
/// the zombie-heartbeat storm the terminal arm exists to prevent.</para>
///
/// <para>The deciding fact is one the dying hub cannot derive from its own run level, so it comes
/// from <see cref="IAddressTombstones"/> — the mesh's delete tombstone, written SYNCHRONOUSLY by
/// the delete handler before its response returns, hence already authoritative for any delivery
/// that raced the teardown.</para>
///
/// <para>The <c>ShuttingDown</c> half of the fork is pinned by
/// <see cref="DeferredDeliveryNackedOnDisposeTest"/> (no tombstones registered ⇒ transient) — so
/// between the two, neither branch can be collapsed into the other unnoticed.</para>
/// </summary>
public class DeletedAddressNackClassificationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record GatedRequest : IRequest<GatedResponse>;

    private record GatedResponse;

    private static readonly Address DeletedAddress = new("gated", "deleted-node");

    /// <summary>
    /// Stands in for the mesh's <c>RecentlyDeletedRegistry</c>: reports exactly one path as
    /// tombstoned. Using the real registry here would drag <c>MeshWeaver.Mesh.Contract</c> into a
    /// messaging-layer test for no added coverage — what is under test is how the PIPELINE reacts
    /// to the answer, not how the tombstone is kept.
    /// </summary>
    private sealed class SingleDeletedPath(string deletedPath) : IAddressTombstones
    {
        public bool IsDeleted(string? path) =>
            string.Equals(path, deletedPath, StringComparison.Ordinal);
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(GatedRequest), typeof(GatedResponse))
            .WithServices(services => services.AddSingleton<IAddressTombstones>(
                new SingleDeletedPath(DeletedAddress.Path)));

    [Fact(Timeout = 30_000)]
    public async Task RequestAbandonedByADeletedAddress_IsNackedAsAuthoritativeNotFound()
    {
        var host = GetHost();

        // Same construction as DeferredDeliveryNackedOnDisposeTest: a hosted hub whose gate never
        // opens parks the request deterministically, and DisposeRequest (gate-exempt) overtakes it
        // and tears the hub down with the request still inside. The handler that WOULD answer is
        // registered on purpose, so a pass can only come from the NACK.
        var gated = host.GetHostedHub(
            DeletedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("test-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(DeletedAddress))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);
        host.Post(new DisposeRequest(), o => o.WithTarget(DeletedAddress));

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        Output.WriteLine($"[nack] {failure.Failure!.ErrorType}: {failure.Failure.Message}");

        failure.Failure!.ErrorType.Should().Be(ErrorType.NotFound,
            "a deleted address is gone for good, so the NACK must be a VERDICT. Reported as the "
            + "transient ShuttingDown it is ridden out by JsonSynchronizationStream and the caller "
            + "never learns anything at all (#1029)");

        // The mesh classifies failures by MESSAGE, not just ErrorType — MeshNodeStreamCache's
        // IsMissingNodeFailure ("No node found") is what turns this into a definitive "Not found"
        // for the reader, and IsTransientOwnerFailure ("is shutting down", "invalid activation",
        // "Rejecting now", …) is what would send it back to retrying forever. Both are string
        // matches, so the wording is contract: assert it here rather than discovering a reworded
        // sentence as a re-opened #1029.
        failure.Failure.Message.Should().Contain("No node found",
            "MeshNodeStreamCache.IsMissingNodeFailure matches on this phrase — without it the "
            + "reader cannot tell a definitive absence from an availability failure");
        failure.Failure.Message.Should().NotContain("shutting down",
            "IsTransientOwnerFailure matches 'is shutting down' anywhere in the message, which "
            + "would re-classify this verdict as retryable and restore the silent park");
    }

    /// <summary>
    /// Polls the public disposal diagnostics until the request is DEMONSTRABLY parked in the gated
    /// hub's deferred queue. Without it the test could dispose before the delivery was accepted —
    /// the intake-gate case, a different code path that would pass either way.
    /// </summary>
    private static async Task WaitForDeferredBacklog(IMessageHub host)
    {
        for (var i = 0; i < 100; i++)
        {
            foreach (Match m in Regex.Matches(host.GetDisposalDiagnostics(), @"deferred=(\d+)"))
                if (int.Parse(m.Groups[1].Value) > 0)
                    return;
            await Task.Delay(50);
        }

        Assert.Fail(
            "The request never reached the gated hub's deferred queue, so this test never "
            + "exercised the disposal path it exists to pin.");
    }
}
