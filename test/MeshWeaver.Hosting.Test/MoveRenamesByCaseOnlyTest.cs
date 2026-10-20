using System;
using System.Collections.Generic;
using System.Linq;
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
/// 🚨 A move whose target differs from its source ONLY BY CASE is a rename — Systemorph/MeshWeaver#6951.
///
/// <para>Measured on a production portal: <c>VhvGruppe/BirgitSalzwedel → VHVGruppe/BirgitSalzwedel</c>
/// failed in its copy leg with <i>"Cannot write 'VHVGruppe/BirgitSalzwedel': the subtree
/// 'VhvGruppe/BirgitSalzwedel' is currently being deleted"</i>. The move holds the subtree-deletion
/// scope of its source, and the scope compares paths case-insensitively — as the mesh keys paths — so
/// the move's own target writes were refused. Loosening that comparison would not have helped: in a
/// case-insensitive store (in-memory, the file system on macOS/Windows) the target and the source ARE
/// one key, so copy-then-delete would create nothing new and the delete leg would remove the only
/// copy. A case-only rename therefore relocates through a staging sibling — source → staging →
/// target — two ordinary moves between keys that differ in every store.</para>
///
/// <para>The controls in the same suite: a move into the source's own subtree is still refused (with
/// or without a case difference), and so is a move onto itself.</para>
/// </summary>
public class MoveRenamesByCaseOnlyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    [Fact(Timeout = 90_000)]
    public async Task ACaseOnlyRename_RelocatesTheSubtree_AndNoNodeKeepsTheOldCasing()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/vhvgruppe-{id}";
        var target = $"{TestPartition}/VHVGruppe-{id}";
        await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);
        await Create($"{source}/birgit/notes", "Notes", ct);

        var moved = await Move(source, target, ct);

        moved.Success.Should().BeTrue($"a case-only rename is a legitimate move; it answered: {moved.Error}");
        moved.Node!.Path.Should().Be(target, "the response names the node at its new casing");

        (await StoredAt(target, ct))!.Path.Should().Be(target);
        (await StoredAt($"{target}/birgit", ct))!.Path.Should().Be($"{target}/birgit");
        (await StoredAt($"{target}/birgit/notes", ct))!.Path.Should().Be($"{target}/birgit/notes");

        var descendants = await Descendants(target, ct);
        Output.WriteLine($"descendants of {target}: {string.Join(", ", descendants)}");
        descendants.Should().HaveCount(2, "both descendants travelled, and nothing was left twice");
        descendants.Should().OnlyContain(p => p.StartsWith(target + "/", StringComparison.Ordinal),
            "every descendant carries the new casing");

        // The old casing is gone: on a case-sensitive store nothing answers it, on a case-insensitive
        // one it answers the SAME node — which now carries the new casing.
        var old = await StoredAt(source, ct);
        (old is null || string.Equals(old.Path, target, StringComparison.Ordinal)).Should().BeTrue(
            $"no node keeps the old casing '{source}'; read back: {old?.Path ?? "(none)"}");

        var siblings = await ChildrenOf(TestPartition, ct);
        siblings.Should().NotContain(p => p.Contains(id, StringComparison.Ordinal) && p != target,
            "the staging sibling the rename passed through is gone");
    }

    [Fact(Timeout = 60_000)]
    public async Task AMoveIntoItsOwnSubtree_IsRefused_WithOrWithoutACaseDifference()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/nest-{id}";
        await Create(source, "Nest", ct);
        await Create($"{source}/child", "Child", ct);

        foreach (var target in new[] { $"{source}/child/inner", $"{TestPartition}/NEST-{id}/inner" })
        {
            var refused = await Move(source, target, ct);
            refused.Success.Should().BeFalse($"'{target}' lies inside the subtree being moved");
            refused.RejectionReason.Should().Be(NodeMoveRejectionReason.ValidationFailed,
                "a move into its own subtree is a verdict, not a failure worth retrying");
            refused.Log!.Messages.Select(m => m.MessageKey).Should().Contain("activity.node.move.intoOwnSubtree",
                "the refusal is keyed, so a German viewer reads it in German");
        }

        var onto = await Move(source, source, ct);
        onto.Success.Should().BeFalse("a move onto itself moves nothing");
        onto.RejectionReason.Should().Be(NodeMoveRejectionReason.ValidationFailed);
        onto.Log!.Messages.Select(m => m.MessageKey).Should().Contain("activity.node.move.ontoItself",
            "the refusal is keyed, so a German viewer reads it in German");

        (await StoredAt(source, ct))!.Path.Should().Be(source, "every refusal left the source where it was");
        (await StoredAt($"{source}/child", ct)).Should().NotBeNull();
        (await Descendants(source, ct)).Should().ContainSingle("nothing was copied anywhere under it");
    }

    [Theory]
    [InlineData("Abc", "abc", MovePathRelation.CaseOnlyRename)]
    [InlineData("P/VhvGruppe/x", "P/VHVGruppe/x", MovePathRelation.CaseOnlyRename)]
    [InlineData("P/a", "P/a", MovePathRelation.Same)]
    [InlineData("P/a", "/P/a/", MovePathRelation.Same)]
    [InlineData("P/a", "P/a/b", MovePathRelation.IntoOwnSubtree)]
    [InlineData("P/a", "p/A/b", MovePathRelation.IntoOwnSubtree)]
    [InlineData("P/a", "P/ab", MovePathRelation.Distinct)]
    [InlineData("P/a/b", "P/a", MovePathRelation.Distinct)]
    [InlineData("P/a", "Q/a", MovePathRelation.Distinct)]
    public void TheRelationBetweenSourceAndTarget_IsClassifiedOnPathSegments(
        string source, string target, string expected)
        => MovePaths.Classify(source, target).Should().Be(expected);

    [Fact]
    public void TheStagingSibling_IsOutsideBothSubtrees_AndUnderTheTargetsParent()
    {
        var staging = MovePaths.CaseRenameStaging("P/VHVGruppe/BirgitSalzwedel");

        staging.Should().StartWith($"P/VHVGruppe/{MovePaths.StagingPrefix}",
            "it is a sibling of the target, so it needs the same Create grant the target does");
        MovePaths.Classify("P/VhvGruppe/BirgitSalzwedel", staging).Should().Be(MovePathRelation.Distinct);
        MovePaths.Classify(staging, "P/VHVGruppe/BirgitSalzwedel").Should().Be(MovePathRelation.Distinct);
        MovePaths.CaseRenameStaging("Abc").Should().StartWith(MovePaths.StagingPrefix,
            "a root-level node stages at the root");
        MovePaths.CaseRenameStaging("Abc").Should().NotContain("/", "a root-level node stages at the root");

        // The leaf is bounded independently of the target's: a long target leaf never lengthens it.
        var longStaging = MovePaths.CaseRenameStaging("P/" + new string('X', 240));
        longStaging.Should().HaveLength("P/".Length + MovePaths.StagingPrefix.Length + MovePaths.StagingTokenLength,
            "the staging leaf is the prefix and a fixed-length token, whatever the target's leaf is");
        MovePaths.CaseRenameStaging("P/A").Should().NotBe(MovePaths.CaseRenameStaging("P/A"),
            "each attempt mints its own staging path");
    }

    private Task<MoveNodeResponse> Move(string source, string target, CancellationToken ct) =>
        ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode?> StoredAt(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<IReadOnlyCollection<string>> Descendants(string path, CancellationToken ct) =>
        Storage.ListDescendantPaths(path)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private async Task<IReadOnlyCollection<string>> ChildrenOf(string path, CancellationToken ct)
    {
        var listed = await Storage.ListChildPaths(path)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        return listed.NodePaths.Concat(listed.DirectoryPaths).ToArray();
    }

    private Task<MeshNode> Create(string path, string name, CancellationToken ct) =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = name,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}
