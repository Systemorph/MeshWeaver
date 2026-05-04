using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Regression guard for the SynchronizationStream gate-deferred UpdateStreamRequest
/// drop. The scenario: caller opens a remote stream and IMMEDIATELY calls
/// <c>stream.Update(...)</c> before the SubscribeRequest round-trip has populated
/// <c>Current</c>. The local sync/* hub queues the UpdateStreamRequest behind its
/// SynchronizationGate; the gate opens when <c>SetCurrentRequest</c> arrives from
/// the owner. If the deferred message gets dropped (TPL Dataflow LinkTo edge case
/// in <c>MessageService.OpenGate</c> doesn't re-flush queued items), the transform
/// never fires and the caller's edit silently disappears.
///
/// <para>The fix: <see cref="UpdateStreamRequest"/> is on the SynchronizationGate's
/// let-through predicate. The handler reads <c>Current</c> at process time — if
/// null (raced before SubscribeResponse), the transform receives null and returns
/// null (no-op); once Current populates, the transform applies. No deferral ⇒ no
/// drop.</para>
/// </summary>
public class StreamUpdateBeforeSubscribeTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>()));

    [HubFact]
    public async Task UpdateImmediatelyAfterGetRemoteStream_AppliesAfterCurrentPopulated()
    {
        // Activate the host hub.
        var host = GetHost();
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var firstBu = TestData.BusinessUnits.First();
        var newName = "renamed-" + System.Guid.NewGuid().ToString("N")[..6];

        // OPEN the remote stream and IMMEDIATELY call Update — this is the path
        // NodeTypeService.TryTriggerRecompile takes from a change-feed handler.
        // The remote stream's sync/* hub is in INIT state; SynchronizationGate
        // is closed. Without the let-through fix the Update is deferred and
        // silently dropped when the gate opens (LinkTo doesn't replay).
        var stream = workspace.GetRemoteStream<BusinessUnit, EntityReference>(
            host.Address,
            new EntityReference(typeof(BusinessUnit).FullName!, firstBu.SystemName));

        stream.Update(curr =>
        {
            // First call may see null (Current not yet populated) — return null
            // and let the gate-let-through fix apply our transform on a later
            // emission. After Current is populated, we mutate.
            if (curr is null) return null;
            var updated = curr with { DisplayName = newName };
            return new ChangeItem<BusinessUnit>(updated, stream.StreamId, stream.StreamId,
                ChangeType.Patch, stream.Hub.Version,
                [new EntityUpdate(typeof(BusinessUnit).FullName!, firstBu.SystemName, updated) { OldValue = curr }]);
        }, ex => Output.WriteLine($"Update failed: {ex}"));

        // Subscribe to the stream and wait for the new name to appear (the
        // transform landed). Pre-fix this never emits because the
        // UpdateStreamRequest was dropped from the deferred buffer.
        var observed = await stream
            .Where(c => c.Value?.DisplayName == newName)
            .Take(1)
            .Timeout(60.Seconds())
            .Select(c => c.Value!)
            .ToTask(TestContext.Current.CancellationToken);

        observed.DisplayName.Should().Be(newName,
            "Update called before Current was populated must still apply once SetCurrent arrives — " +
            "the SynchronizationGate's let-through predicate keeps UpdateStreamRequest from being deferred.");
    }
}
