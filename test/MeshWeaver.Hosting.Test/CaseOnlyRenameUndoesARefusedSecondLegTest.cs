using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A case-only rename whose SECOND leg (staging → target) fails part-way must not leave the
/// leg's writes behind when it moves the subtree back (#6951, review of #6976).
///
/// <para>The copy creates the root before its descendants, so a refused DESCENDANT create leaves the
/// root (and any sibling that landed) at the final target. Moving staging back to the source does not
/// remove those: on a case-sensitive store both casings survive, and on a case-insensitive one — this
/// in-memory store — the restore's create of the source root meets the new-cased root at the SAME
/// key, recognises it as the same node relocated (#6310), and RESUMES onto it, so the answer said
/// "nothing was renamed" over a root that had been renamed. The rename now removes exactly the paths
/// the failed leg wrote before it restores.</para>
///
/// <para>Isolated fault: one create validator refuses exactly ONE path, compared ORDINALLY — the
/// descendant at the target's casing — so leg 1 (to staging) and the restore (to the source's
/// casing) are untouched, and the control in the same test is that the restored subtree reads back
/// at the source's own casing, byte for byte.</para>
/// </summary>
public class CaseOnlyRenameUndoesARefusedSecondLegTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly RefuseExactPathCreationValidator refusing = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(refusing));

    [Fact(Timeout = 90_000)]
    public async Task ARefusedTargetDescendant_LeavesNoRootAtTheTarget_AndTheSourceKeepsItsCasing()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/vhvgruppe-{id}";
        var target = $"{TestPartition}/VHVGruppe-{id}";
        await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);
        await Create($"{source}/birgit/notes", "Notes", ct);
        refusing.Refuse($"{target}/birgit");

        var moved = await Move(source, target, ct);

        moved.Success.Should().BeFalse("the second leg's descendant create was refused");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.ValidationFailed,
            "the subtree is back where it was, so the copy leg's own verdict is the answer");
        moved.Error.Should().Contain("moved back", "the answer says the subtree was restored");

        // The control: what is stored under the source key carries the SOURCE's casing. Before the
        // fix the restore resumed onto the leg's new-cased root, so this read back the TARGET casing.
        (await StoredAt(source, ct))!.Path.Should().Be(source,
            "nothing at the source key keeps the casing the failed leg wrote");
        (await StoredAt($"{source}/birgit", ct))!.Path.Should().Be($"{source}/birgit");
        (await StoredAt($"{source}/birgit/notes", ct))!.Path.Should().Be($"{source}/birgit/notes");
        var descendants = await Descendants(source, ct);
        Output.WriteLine($"descendants of {source}: {string.Join(", ", descendants)}");
        descendants.Should().HaveCount(2, "the whole subtree is back, once");
        descendants.Should().OnlyContain(p => p.StartsWith(source + "/", StringComparison.Ordinal),
            "every restored descendant carries the source's casing");

        var siblings = await ChildrenOf(TestPartition, ct);
        Output.WriteLine($"siblings: {string.Join(", ", siblings.Where(p => p.Contains(id, StringComparison.Ordinal)))}");
        siblings.Where(p => p.Contains(id, StringComparison.Ordinal))
            .Should().Equal([source], "no staging sibling and no second casing is left behind");

        moved.Log.Should().NotBeNull("the restored outcome travels as a keyed transcript");
        moved.Log!.Messages.Select(m => m.MessageKey)
            .Should().Contain("activity.node.move.caseRenameRestored", "a German viewer reads it in German");
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
        return listed.NodePaths.Concat(listed.DirectoryPaths).Distinct(StringComparer.Ordinal).ToArray();
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

/// <summary>
/// Refuses the create of exactly the paths it is told to, compared ORDINALLY — so the same node at
/// another casing is created normally. Instance state owned by the test's mesh, never static.
/// </summary>
internal sealed class RefuseExactPathCreationValidator : INodeValidator
{
    private ImmutableHashSet<string> refused = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>Refuse creating <paramref name="path"/> (this exact casing) from now on.</summary>
    public void Refuse(string path) => ImmutableInterlocked.Update(ref refused, s => s.Add(path));

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => Observable.Return(refused.Contains(context.Node.Path)
            ? NodeValidationResult.Invalid($"creating '{context.Node.Path}' is refused by this test")
            : NodeValidationResult.Valid());
}
