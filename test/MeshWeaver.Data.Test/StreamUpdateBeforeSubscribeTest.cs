using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
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
/// <para>The fix: <c>SynchronizationStream&lt;TStream&gt;.UpdateStreamRequest</c> is on the
/// SynchronizationGate's let-through predicate. The handler reads <c>Current</c>
/// at process time — if null (raced before SubscribeResponse), the transform
/// receives null and returns null (no-op); once Current populates, the transform
/// applies. No deferral ⇒ no drop.</para>
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
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        var firstBu = TestData.BusinessUnits.First();
        var newName = "renamed-" + System.Guid.NewGuid().ToString("N")[..6];
        var updatedBu = firstBu with { DisplayName = newName };

        // OPEN the remote stream and IMMEDIATELY call DataChangeRequest — this
        // is the path that exercises UpdateStreamRequest under the gate. Pre-fix
        // the request was deferred behind the SynchronizationGate and lost when
        // the gate opened (TPL Dataflow LinkTo doesn't re-flush queued items).
        // Post-fix UpdateStreamRequest passes the gate; the handler reads
        // Current at process time and applies the transform once Current is
        // populated by SetCurrent.
        client.Post(new DataChangeRequest { Updates = [updatedBu] });

        // Wait for the new name to propagate via the cross-hub data sync.
        var observed = await workspace
            .GetObservable<BusinessUnit>(firstBu.SystemName)
            .Should().Within(60.Seconds())
            .Match(bu => bu?.DisplayName == newName);

        observed!.DisplayName.Should().Be(newName,
            "DataChangeRequest fires UpdateStreamRequest under the SynchronizationGate; pre-fix " +
            "the request was deferred behind the gate and silently lost when the gate opened.");
    }
}
