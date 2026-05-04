using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public record Item(string Id, int Value);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<Item>(t => t
                    .WithKey(i => i.Id)
                    .WithInitialData(_ => Task.FromResult<IEnumerable<Item>>([new Item("k", 1)])))));

    [HubFact]
    public async Task UpdateImmediatelyAfterGetRemoteStream_AppliesAfterCurrentPopulated()
    {
        // Activate the host hub once so the catalog/router knows about it.
        var host = GetHost();

        var client = GetClient(c => c.AddData(d => d));
        var workspace = client.GetWorkspace();

        // OPEN the remote stream and IMMEDIATELY call Update — this is the path
        // NodeTypeService.TryTriggerRecompile takes from a change-feed handler.
        // The remote stream's sync/* hub is in INIT state; SynchronizationGate
        // is closed. Without the let-through fix the Update is deferred and
        // silently dropped when the gate opens (LinkTo doesn't replay).
        var stream = workspace.GetRemoteStream<Item, EntityReference>(
            host.Address,
            new EntityReference(typeof(Item).FullName!, "k"));

        stream.Update(curr =>
        {
            // First call may see null (Current not yet populated) — return null
            // and let the gate-let-through fix apply our transform on a later
            // emission. After Current is populated, we mutate.
            if (curr is null) return null;
            var updated = curr with { Value = 42 };
            return new ChangeItem<Item>(updated, stream.StreamId, stream.StreamId,
                ChangeType.Patch, stream.Hub.Version,
                [new EntityUpdate(typeof(Item).FullName!, "k", updated) { OldValue = curr }]);
        }, ex => Logger.LogWarning(ex, "Update failed"));

        // Subscribe to the stream and wait for Value=42 to appear (the transform
        // landed). Pre-fix this never emits because the UpdateStreamRequest was
        // dropped from the deferred buffer.
        var observed = await stream
            .Where(c => c.Value?.Value == 42)
            .Take(1)
            .Timeout(60.Seconds())
            .Select(c => c.Value!)
            .ToTask(TestContext.Current.CancellationToken);

        observed.Value.Should().Be(42,
            "Update called before Current was populated must still apply once SetCurrent arrives — " +
            "the SynchronizationGate's let-through predicate keeps UpdateStreamRequest from being deferred.");
    }
}
