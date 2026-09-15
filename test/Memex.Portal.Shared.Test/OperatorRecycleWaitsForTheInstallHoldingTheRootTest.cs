#pragma warning disable CS1591

using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>#3510 — the OPERATOR recycle (<c>MeshOperations.Recycle</c>, the operations/MCP verb)
/// waits while an install holds the root, and is a straight pass-through when none does.</b>
///
/// <para><b>The hole this closes.</b> Every automatic recycler has consulted
/// <see cref="PackageRootInstallLeases"/> since #4009, and <c>HubRecycleExtensions</c> said so in
/// its own remarks — while naming the surface that did NOT: <i>"MeshOperations.Recycle — the
/// operations/MCP recycle. It posts directly, so an operator recycling a package root mid-install
/// can still strand that install."</i> That is the measured harm of CD 7950 reachable by hand: the
/// root's per-node children go down with it, the writes they owed acks for are never answered
/// (<c>[UpdateQueue] ADVANCE_WITHOUT_HANDOFF … the owner never acknowledged this write</c>), the
/// <c>nodeops</c> handler that owed its reply to one of those acks never replies, and the install
/// sits until its own ten-minute bound reports <c>[FAIL] … install: TimeoutException</c>.</para>
///
/// <para><b>Why the gate sits where it does, and why both halves are pinned.</b> It runs AFTER the
/// permission fold — a caller who may not recycle is refused at once and never waits on somebody
/// else's install — and BEFORE <c>RecycleCore</c>, because the release-request stamp
/// <c>RecycleCore</c> issues is itself a WRITE into the root the install is writing. Gating only
/// the <c>DisposeRequest</c> would leave that write racing the install, so the assertion below is
/// on the operation emitting NOTHING, not merely on the hub surviving.</para>
///
/// <para><b>Negative control — measured, not asserted.</b> Neutralising the production gate
/// (dropping the <c>WhenNoInstallHoldsRoot</c> hop in <c>MeshOperations.Recycle</c> so it calls
/// <c>RecycleCore</c> directly, as it did before this change) and rebuilding both projects makes
/// <see cref="WhileAnInstallHoldsTheRoot_TheOperatorRecycleWaitsAndThenRuns"/> fail on its FIRST
/// assertion, verbatim:
/// <code>
/// Expected the observable not to emit within 12s because an install is writing under this
/// root … but it emitted {"status":"Recycled","path":"TestData/op-recycle-held-root",
/// "message":"DisposeRequest posted + cache invalidation broadcast via MeshChangeFeed. …"}
/// </code>
/// — i.e. the teardown went out in the middle of the install, which is the defect. The
/// pass-through case stays green throughout, so the pair discriminates rather than one of them
/// merely being hard to satisfy. Restored ⇒ 2/2.</para>
///
/// <para>🚨 Twin of <c>RecycleNodeWaitsForTheInstallHoldingTheRootTest</c> in
/// <c>MeshWeaver.Graph.Test</c>, which pins the same contract on the framework surface. The two
/// are deliberately separate tests rather than one parameterised one: they are different entry
/// points in different assemblies, and #3510's whole lesson is that a gate reaches only what is
/// routed through it — a shared harness would let one of them stand in for the other.</para>
/// </summary>
public class OperatorRecycleWaitsForTheInstallHoldingTheRootTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Holder =
        "PackageInstaller: the install of package 'Hosting' is writing under this root";

    /// <summary>
    /// The verb's REAL origin — the same <see cref="SessionHubFactory"/> hub MCP, REST, gRPC and
    /// the CLI all drive <see cref="MeshOperations"/> from. Not the bare client hub
    /// <c>RequestHub</c> gives: <c>RecycleCore</c> resolves a workspace off its hub, which a
    /// handler-less client does not carry, so a test on that hub answers <c>status=Error</c> from
    /// the catch-all and never reaches the code under test.
    /// </summary>
    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh,
        "mcp",
        "recycle-lease",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(OperatorRecycleWaitsForTheInstallHoldingTheRootTest)));

    private async Task<string> SeedNode(string id)
    {
        var path = $"{TestPartition}/{id}";
        await NodeFactory
            .CreateNode(new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Emit("the node the recycle targets has to exist first",
                cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>
    /// 🚨 THE REGRESSION CASE. While an install holds the root, the operator recycle produces no
    /// answer at all — it has not stamped, and it has not posted its <c>DisposeRequest</c>, so the
    /// hub stays up and the writes beneath it keep their owner. The second half is what makes this
    /// a DEFERRAL rather than a silent drop: the moment the install releases the root, the recycle
    /// runs and the operator gets the envelope they asked for.
    /// </summary>
    [Fact]
    public async Task WhileAnInstallHoldsTheRoot_TheOperatorRecycleWaitsAndThenRuns()
    {
        var leases = Mesh.ServiceProvider.GetRequiredService<PackageRootInstallLeases>();
        var path = await SeedNode("op-recycle-held-root");

        var lease = leases.Hold(path, Holder);
        leases.HeldBy(path).Should().Be(Holder, "the holder is named, never anonymous");

        var recycled = new MeshOperations(SessionHub()).Recycle(path).Replay(1);
        using var connection = recycled.Connect();

        await recycled.Should().NotEmit(
            TestTimeouts.Quick,
            "an install is writing under this root — an operator recycling it now would strand "
            + "that install's writes, which is #3510's whole measured harm, and the verb must not "
            + "even stamp the release request because that stamp is itself a write into the root");

        lease.Dispose();

        var answer = await recycled.Should().Within(TestTimeouts.Convergence).Emit(
            "the recycle is DEFERRED, never dropped: the install released the root, so the verb "
            + "must now run and answer the operator");

        using var envelope = JsonDocument.Parse(answer);
        envelope.RootElement.GetProperty("status").GetString().Should().Be("Recycled",
            "the deferral changes WHEN the operation runs, never WHAT it answers — an operator "
            + "who waited must get the same envelope as one who did not");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. #3965 measured that a root recycling with no install of its own is
    /// the COMMON case, so a gate that blocked every operator recycle would pass the regression
    /// case above and break the verb for everybody. With nothing held this is the pre-#3510
    /// composition exactly.
    /// </summary>
    [Fact]
    public async Task WithNoInstallHoldingTheRoot_TheOperatorRecycleProceedsAtOnce()
    {
        var leases = Mesh.ServiceProvider.GetRequiredService<PackageRootInstallLeases>();
        var path = await SeedNode("op-recycle-free-root");

        leases.IsHeld(path).Should().BeFalse("no install is running");

        var answer = await new MeshOperations(SessionHub()).Recycle(path)
            .Should().Within(TestTimeouts.Convergence).Emit(
                "an ordinary operator recycle must still proceed promptly — the deferral is scoped "
                + "to a root an install actually holds");

        using var envelope = JsonDocument.Parse(answer);
        envelope.RootElement.GetProperty("status").GetString().Should().Be("Recycled",
            "nothing held the root, so the verb ran end to end");
    }
}
