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
/// 🚨 A case-only rename whose SECOND leg (staging → target) fails NEVER deletes at the target
/// (#6951, review of #6976). It moves the subtree back only when the leg's copy provably wrote
/// nothing there; otherwise it answers Unknown naming source, staging and target, and leaves both
/// as they are for the operator.
///
/// <para>Why it never deletes: nothing on the platform proves that a row at the target is still the
/// one this copy inserted — storage writes are not insert-only, the deletion scope is in-process,
/// and no compare-and-delete checks identity across silos — so a compensating delete could remove
/// somebody else's node. Four review rounds each found another way it could; the undo now fails
/// closed instead.</para>
///
/// <para>Isolated fault: one create validator refuses exactly the paths it is told to, compared
/// ORDINALLY, so leg 1 (to staging) and the restore (to the source's casing) are untouched. Refusing
/// a target DESCENDANT leaves the root the copy already wrote — Unknown, nothing deleted. Refusing the
/// target ROOT means the copy wrote nothing — the subtree is moved back.</para>
/// </summary>
public class CaseOnlyRenameNeverDeletesAtAFailedSecondLegsTargetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string StagingMarker = "-recase-";

    private readonly RefuseExactPathCreationValidator refusing = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(refusing));

    [Fact(Timeout = 90_000)]
    public async Task ARefusedTargetDescendant_LeavesTargetAndStagingAsTheyAre_AndIsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, target) = await CreateTree(ct);
        refusing.Refuse($"{target}/birgit");

        var moved = await Move(source, target, ct);

        moved.Success.Should().BeFalse("the second leg's descendant create was refused");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.Unknown,
            "the copy had already written the target root, and a failed rename never deletes at its target");
        moved.Error.Should().NotContain("The subtree was moved back", "nothing was moved back");
        moved.Error.Should().Contain($"'{source}'").And.Contain($"'{target}'");
        var staging = StagingNamedIn(moved.Error!, target);
        moved.Log.Should().NotBeNull("the outcome travels as a keyed transcript");
        moved.Log!.Messages.Select(m => m.MessageKey)
            .Should().Contain("activity.node.move.caseRenameCopyWrote", "a German viewer reads it in German");

        // The target is left exactly as the copy left it: its root, at the new casing.
        (await StoredAt(target, ct))!.Path.Should().Be(target, "nothing was deleted at the target");
        // And staging keeps the complete subtree — the recovery data the answer names.
        (await StoredAt(staging, ct)).Should().NotBeNull("staging holds the complete copy");
        (await Descendants(staging, ct)).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            [$"{staging}/birgit", $"{staging}/birgit/notes"], "the staging copy is complete");
    }

    [Fact(Timeout = 90_000)]
    public async Task ARefusedTargetRoot_WroteNothing_SoTheSubtreeIsMovedBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, target) = await CreateTree(ct);
        var id = source[(source.LastIndexOf('-') + 1)..];
        refusing.Refuse(target);

        var moved = await Move(source, target, ct);

        moved.Success.Should().BeFalse("the second leg's root create was refused");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.ValidationFailed,
            "the copy provably wrote nothing, the subtree is back, so the copy's own verdict is the answer");
        moved.Error.Should().Contain("moved back", "the answer says the subtree was restored");
        moved.Log!.Messages.Select(m => m.MessageKey)
            .Should().Contain("activity.node.move.caseRenameRestored", "a German viewer reads it in German");

        (await StoredAt(source, ct))!.Path.Should().Be(source, "the source key carries the source's casing again");
        (await StoredAt($"{source}/birgit", ct))!.Path.Should().Be($"{source}/birgit");
        (await StoredAt($"{source}/birgit/notes", ct))!.Path.Should().Be($"{source}/birgit/notes");
        var siblings = await ChildrenOf(TestPartition, ct);
        siblings.Where(p => p.Contains(id, StringComparison.Ordinal))
            .Should().Equal([source], "no staging sibling and no second casing is left behind");
    }

    private async Task<(string Source, string Target)> CreateTree(CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/vhvgruppe-{id}";
        var target = $"{TestPartition}/VHVGruppe-{id}";
        await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);
        await Create($"{source}/birgit/notes", "Notes", ct);
        return (source, target);
    }

    private static string StagingNamedIn(string error, string target)
    {
        var start = error.IndexOf($"'{target}{StagingMarker}", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the answer names the staging path; it said: {error}");
        var end = error.IndexOf('\'', start + 1);
        return error[(start + 1)..end];
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
