using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the framework-level guarantee that <see cref="InitializeHubRequest"/>
/// (and the other system messages — see <c>MessageService.cs</c>) always pass
/// every <c>WithInitializationGate(...)</c> regardless of the user's predicate.
///
/// <para><b>Why this exists.</b> Prod regression: the per-thread hub used
/// <c>WithInitializationGate(MeshNodeInitGateName, d =&gt; d.Message is CreateNodeRequest)</c>.
/// On Orleans + Postgres, persistence load took long enough that the framework's
/// <c>InitializeHubRequest</c> arrived during the gate window. The narrow predicate
/// queued it → <c>BuildupActions</c> never ran → the gate that opens on
/// <c>MeshNodeTypeSource.Initialize</c> emission never opened → SubscribeRequest
/// timed out at 30s. Fix moved the bypass for InitializeHubRequest +
/// HeartBeatEvent into the framework alongside ShutdownRequest / DisposeRequest /
/// DeliveryFailure so no per-gate predicate can accidentally re-introduce the
/// deadlock.</para>
///
/// <para><b>The test.</b> Configure a host with a gate whose predicate is
/// <c>_ =&gt; false</c> (never lets anything through, never opens). If
/// <c>InitializeHubRequest</c> bypasses the gate, BuildupActions will run and
/// our <see cref="WithInitialization"/> hook flips the flag. If it doesn't
/// bypass, the flag stays false and the hub deadlocks on init.</para>
/// </summary>
public class InitializationGateBypassTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly TaskCompletionSource _buildupRan = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            // The framework seeds InitializationGates with
            //   { "Initialize": d => d.Message is InitializeHubRequest }
            // — a permissive predicate that already lets InitializeHubRequest through
            // every check. Override that predicate to `_ => false` so the per-gate
            // path can NO LONGER rescue init. With both this gate and the
            // framework's InitializeGateName predicate rejecting, the only thing
            // that can let InitializeHubRequest pass is the hardcoded
            // `delivery.Message is … or InitializeHubRequest …` short-circuit in
            // MessageService.cs. If that short-circuit is removed/regressed, the
            // hub deadlocks during boot — exactly the prod failure mode this
            // pin guards against.
            .WithInitializationGate(MessageHubConfiguration.InitializeGateName, _ => false)
            .WithInitializationGate("test-never-opens", _ => false)
            // BuildupAction runs only after InitializeHubRequest is processed.
            // If InitializeHubRequest were queued behind the gates, this never fires.
            .WithInitialization(_ =>
            {
                _buildupRan.TrySetResult();
            });

    [Fact]
    public async Task InitializeHubRequest_BypassesNeverOpeningGate()
    {
        // GetHost triggers hub construction → posts InitializeHubRequest → runs BuildupActions.
        var host = GetHost();
        host.Should().NotBeNull();

        // 5s is generous; with the bypass this completes synchronously after construction.
        // Without the bypass the gate stays closed forever and this hangs until the timeout.
        var ct = new CancellationTokenSource(5.Seconds()).Token;
        var completed = await Task.WhenAny(_buildupRan.Task, Task.Delay(Timeout.Infinite, ct));

        completed.Should().Be(_buildupRan.Task,
            "InitializeHubRequest must bypass every WithInitializationGate predicate so " +
            "BuildupActions can run and open user-defined readiness gates. If this hangs, " +
            "the framework-level bypass list in MessageService.cs has regressed — see " +
            "Doc/Architecture/InitializationGates.md → 'Framework-bypassed messages'.");
    }
}
