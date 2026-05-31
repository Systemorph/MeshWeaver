using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Regression cover for the prod hang where MCP attach (and any other auth path)
/// timed out at exactly 30 000 ms. Root cause was twofold:
///
/// <list type="number">
///   <item><c>MessageHubGrain.OnActivateAsync</c> only handled <c>onNext</c> and
///   <c>onError</c> on the activation chain's <c>Subscribe</c> â€” when the source
///   completed without ever emitting a usable node (catalog couldn't find it,
///   no provider claimed the partition, â€¦), <c>_hubReady</c> stayed pending and
///   <c>DeliverMessage</c>'s <c>WaitAsync(30 s)</c> burned the budget.</item>
///   <item><c>MeshQuery.MergeProviderObservables</c> subscribed to every
///   provider regardless of <see cref="IMeshQueryProvider.Matches"/>, so a
///   single stalled provider held the merged Initial hostage. Fixed by gating
///   on <c>Matches(queryNamespaces)</c> at every fan-out point in
///   <c>MeshQuery</c>.</item>
/// </list>
///
/// <para>This test asserts the user-visible contract: a request to a
/// non-existent path must surface failure within seconds, never the 30 s
/// grain-timeout window. The old behaviour blocked at exactly 30 000 ms; the
/// fix surfaces an <c>InvalidOperationException</c> ("No MeshNode resolvable
/// for address â€¦") within ~1 s.</para>
/// </summary>
public class GrainActivationCompletesFastTest(ITestOutputHelper output)
    : OrleansTestBase<DynamicCompilationSiloConfigurator>(output)
{
    /// <summary>
    /// Caps the test budget well below the 30 s grain <c>WaitAsync</c>. If
    /// activation hangs (the prod symptom), the cancellation token fires inside
    /// this window and the test fails â€” distinguishing "activation hung"
    /// (CancellationException) from "activation failed cleanly"
    /// (DeliveryFailureException with the new "No MeshNode resolvable" text).
    /// </summary>
    private static readonly TimeSpan FastFailBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NonExistentPath_ActivationFailsFast_NotThirtySecondTimeout()
    {
        var ct = new CancellationTokenSource(FastFailBudget).Token;
        var client = GetClient($"nonexistent-{Guid.NewGuid():N}");

        // A path that no provider can claim. Persistence has no row, no static
        // node provider declares it. Pre-fix, the activation chain's source
        // observable completes empty, _hubReady stays pending, and DeliverMessage
        // burns the full 30 s WaitAsync. Post-fix, the Subscribe's onCompleted
        // handler sets _hubReady to InvalidOperationException, the grain returns
        // delivery.Failed immediately, and the client's Observe surfaces
        // DeliveryFailure within milliseconds of the source completing.
        var nonexistentPath = $"nonexistent-{Guid.NewGuid():N}/never/exists";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Func<Task> act = async () =>
        {
            await client
                .Observe(new GetDataRequest(new MeshNodeReference()),
                    o => o.WithTarget(new Address(nonexistentPath)))
                .FirstAsync()
                .ToTask(ct);
        };

        // Two acceptable outcomes â€” both prove the fix:
        //   * DeliveryFailureException surfaced from the framework's NotFound /
        //     "No MeshNode resolvable" path (the typical shape).
        //   * Plain TimeoutException at the framework's response budget (also
        //     short-circuit-friendly because the grain's _hubReady was failed
        //     before that budget elapsed).
        // What we MUST NOT see is the wall clock crossing the 30 s grain
        // boundary â€” that's the prod hang signature.
        await act.Should().ThrowAsync<Exception>();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(
            FastFailBudget,
            "activation against a non-existent path must fail fast (catalog completes empty â†’ " +
            "_hubReady.TrySetException) â€” never block on the 30 s MessageHubGrain.DeliverMessage " +
            $"WaitAsync. Actual: {sw.Elapsed.TotalSeconds:0.0}s.");

        Output.WriteLine($"PASSED â€” failed in {sw.Elapsed.TotalMilliseconds:0}ms (well under 30 000 ms grain timeout)");
    }
}
