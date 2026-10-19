using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A failed copy names as WRITTEN only what THIS attempt created — never a target it RESUMED onto
/// (#6951, review of #6976).
///
/// <para>A relocating copy resumes a half-landed relocation (#6310): a target that already holds the
/// same node relocated, with the source unchanged since, stands in for the create. That target was
/// not written by this attempt and may carry edits made AFTER the earlier attempt landed it. A caller
/// undoing a failed copy — the case-only rename of a move removes exactly
/// <see cref="CopyNodeResponse.Written"/> before it moves its subtree back — would otherwise delete
/// that newer target. Resumed targets are reported apart, in <see cref="CopyNodeResponse.Resumed"/>.</para>
///
/// <para>Isolated fault: one create validator refuses exactly ONE descendant path, so the first
/// attempt lands the root and fails; the second attempt, issued identically, resumes onto that root
/// and fails at the same descendant. The control is the first attempt, whose root IS its own write.</para>
/// </summary>
public class AFailedCopyDoesNotClaimAResumedTargetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly RefuseExactPathCreationValidator refusing = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(refusing));

    [Fact(Timeout = 90_000)]
    public async Task ASecondAttempt_ReportsTheRootItResumedOnto_AsResumed_NotAsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/group-{id}";
        var target = $"{TestPartition}/relocated-{id}";
        await Create(source, "Group", ct);
        await Create($"{source}/member", "Member", ct);
        refusing.Refuse($"{target}/member");

        // The control: the first attempt CREATED the root, so it is its own write.
        var first = await Copy(source, target, ct);
        first.Success.Should().BeFalse("the descendant create was refused");
        first.Written.Should().Equal([target], "the first attempt created the root before the refused descendant");
        first.Resumed.Should().BeEmpty("nothing was at the target before the first attempt");

        var second = await Copy(source, target, ct);
        second.Success.Should().BeFalse("the descendant create is still refused");
        second.Written.Should().NotContain(target,
            "the second attempt resumed onto the root the first one landed — it did not write it, and a caller "
            + "undoing this copy must never delete it");
        second.Resumed.Should().Equal([target], "the root this attempt resumed onto is reported as resumed");
        second.WrittenUnsettled.Should().BeFalse("the refusal came before the descendant was written");
    }

    private Task<CopyNodeResponse> Copy(string source, string target, CancellationToken ct) =>
        ObserveNodeOperation(new CopyNodeRequest(source, target)
            {
                IncludeDescendants = true,
                IncludeSatellites = true,
                PreserveAuthorship = true,
                RequireComplete = true,
            })
            .Select(d => d.Message)
            .Do(r => Output.WriteLine(
                $"COPY {source} -> {target}: success={r.Success} error={r.Error} written=[{string.Join(", ", r.Written)}] "
                + $"resumed=[{string.Join(", ", r.Resumed)}] unsettled={r.WrittenUnsettled}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode> Create(string path, string name, CancellationToken ct) =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = name,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}
