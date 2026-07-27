using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the timeout contract of the one-shot read <c>hub.GetMeshNode(path)</c>: a read that
/// gives up must SURFACE, never masquerade as "node not found".
///
/// <para>Why: the read used to map its own timeout to <c>null</c> — the identical value the
/// not-found path emits. Every caller therefore substituted "missing" for "the mesh stalled":
/// layout areas rendered empty, the compilation service resolved a null NodeType definition,
/// and in CI (ThreadAgentIntegrationTest, 2026-07-26) a test burned the full 60 s read budget,
/// asserted against a null context it never expected, PASSED, and then failed in DisposeAsync
/// with a watchdog message blaming the CancellationToken. The stall itself left nothing behind
/// but a Debug log.</para>
///
/// <para>The black hole below is deterministic, not timing-based: the target hub ACCEPTS the
/// <see cref="GetDataRequest"/> and never answers it, so the reply does not exist — no amount of
/// machine speed can make this test flaky in either direction.</para>
/// </summary>
public class GetMeshNodeTimeoutSurfacingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A hub that swallows every <see cref="GetDataRequest"/> — marks it processed and never
    /// posts a response. Reading its own address through <c>GetMeshNode</c> therefore always
    /// exhausts the budget.
    /// </summary>
    private IMessageHub CreateSilentHub()
        => GetClient(c => ConfigureClient(c)
            .WithHandler<GetDataRequest>((_, request) => request.Processed()));

    [Fact(Timeout = 30000)]
    public async Task ReadTimesOut_SurfacesTimeoutException_NotSilentNull()
    {
        var silent = CreateSilentHub();
        var path = silent.Address.ToString();

        Func<Task> act = () => silent.GetMeshNode(path, ReadBudget).FirstAsync().ToTask();

        var ex = (await act.Should().ThrowAsync<TimeoutException>(
            "a read that never got its reply must surface, not resolve to the same null "
            + "that means 'node not found'")).Which;

        Output.WriteLine($"Surfaced: {ex.Message}");

        ex.Message.Should().Contain(path,
            "the failure must name the path that stalled, or it is unactionable");
        ex.Message.Should().Contain("timed out",
            "the failure must say what happened");
        ex.Message.Should().Contain("NOT 'node not found'",
            "the message must stop the reader concluding the node is missing");
    }

    /// <summary>
    /// The timeout carries the reading hub's in-flight snapshot, so the next occurrence says
    /// WHY: our own GetDataRequest still listed as pending = the reply never came (dead owner
    /// hub / dropped response), versus a hub that is executing something else / has a backed-up
    /// queue = congestion or ThreadPool starvation.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ReadTimesOut_MessageCarriesPendingRequestDiagnostics()
    {
        var silent = CreateSilentHub();
        var path = silent.Address.ToString();

        Func<Task> act = () => silent.GetMeshNode(path, ReadBudget).FirstAsync().ToTask();
        var ex = (await act.Should().ThrowAsync<TimeoutException>()).Which;

        Output.WriteLine($"Surfaced: {ex.Message}");

        ex.Message.Should().Contain("PendingCallbacks",
            "the timeout must carry the hub's pending-callback snapshot");
        ex.Message.Should().Contain(nameof(GetDataRequest),
            "the snapshot must name the outstanding request — that is what identifies a reply "
            + "that never arrived, as opposed to a hub busy with something else");
        ex.Message.Should().Contain("RunLevel",
            "the snapshot must carry the hub's run level and queue state");
    }

    /// <summary>
    /// The documented opt-out still works for callers whose contract really is
    /// "indeterminate ⇒ treat as absent" (a cosmetic fallback, an idempotent-upsert existence
    /// probe). Those callers pass <see cref="ReadTimeoutBehavior.EmitNull"/> explicitly — the
    /// stall is still logged at Warning with the same diagnostics, so opting in suppresses the
    /// exception, never the evidence.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ReadTimesOut_WithEmitNullOptIn_StillEmitsNull()
    {
        var silent = CreateSilentHub();
        var path = silent.Address.ToString();

        var node = await silent
            .GetMeshNode(path, ReadBudget, ReadTimeoutBehavior.EmitNull)
            .FirstAsync()
            .ToTask();

        node.Should().BeNull(
            "the explicit opt-in keeps the legacy lenient behaviour for callers that documented it");
    }

    /// <summary>
    /// The other half of the contract: a genuine miss is NOT a timeout. Reading a path that
    /// does not exist still resolves to <c>null</c> — promptly, via the routing NotFound
    /// delivery failure — so the strict default costs "absent" callers nothing.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task NodeThatDoesNotExist_StillEmitsNull_Promptly()
    {
        var node = await ReadNode($"{TestPartition}/no-such-node-{Guid.NewGuid():N}")
            .Should().Within(TimeSpan.FromSeconds(20)).Emit();

        node.Should().BeNull("a genuine not-found must stay a null emission, not become an error");
    }
}
