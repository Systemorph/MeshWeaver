using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
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
/// 🚨 A MOVE validates its source as a delete of the source (#5970), and the validated set must be
/// the set it removes. The pre-flight enumerates the source subtree once; the delete leg
/// re-enumerates after the copy. A node written under the source BETWEEN the two used to be carried
/// by the copy and removed by the delete leg without any delete validator having been asked.
///
/// <para>The recursive delete closes that planning window with
/// <see cref="RecentlyDeletedRegistry.BeginSubtreeDeletion"/>, held from planning to commit, which
/// the storage write guard enforces. The move now holds the same scope. The test lands the late
/// write at the one deterministic point inside the window: from the delete validator of the source
/// ROOT, which runs only after the pre-flight's enumeration has been taken. Whatever happens to that
/// write, it must not end up removed unvalidated: it is either refused, or the validator saw it.</para>
///
/// <para>The control in the same test: once the move has ANSWERED, a write under the old source path
/// succeeds again, so the scope is released before the response, never after it.</para>
/// </summary>
public class MoveHoldsTheSubtreeDeletionScopeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly LateWriteTrapValidator trap = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(trap));

    [Fact(Timeout = 60000)]
    public async Task ANodeWrittenUnderTheSourceMidMove_IsRefusedOrValidated_NeverRemovedUnasked()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = NewPath("src");
        await Create(source, "src", ct);
        await Create($"{source}/Child", "Child", ct);
        var target = NewPath("dst");
        var late = $"{source}/Late";

        trap.Arm(source, Storage, NodeAt(late, "Late"), Mesh.JsonSerializerOptions);

        var moved = await Move(source, target, ct);
        moved.Success.Should().BeTrue(moved.Error ?? "nothing objects to deleting this source");

        // The switch FIRED: the late write was attempted inside the window, after the enumeration.
        var outcome = trap.Outcome;
        // The switch not firing means the test proved nothing: the source root's delete validator
        // never ran, so no late write was attempted inside the window.
        Assert.NotNull(outcome);
        Output.WriteLine($"late write: {(outcome.Refusal is null ? "LANDED" : $"refused — {outcome.Refusal}")}");
        Output.WriteLine($"validated: {string.Join(", ", trap.Validated)}");

        var lateWasValidated = trap.Validated.Contains(late);
        (outcome.Refusal is not null || lateWasValidated).Should().BeTrue(
            $"a node written under '{source}' during its move must be refused or validated before the "
            + "delete leg removes it — it was written, and no delete validator was asked about it");

        if (outcome.Refusal is not null)
        {
            (await StoredAt(late, ct)).Should().BeNull("the refused write left nothing under the source");
            (await StoredAt($"{target}/Late", ct)).Should().BeNull("and the copy carried nothing for it");
        }

        (await StoredAt(target, ct)).Should().NotBeNull("the move itself went through");
        (await StoredAt($"{target}/Child", ct)).Should().NotBeNull("with the validated descendant");
        (await StoredAt(source, ct)).Should().BeNull();

        // The control: the scope is released BEFORE the answer, so a write issued on the answer lands.
        var after = await Storage.Write(NodeAt($"{source}/AfterMove", "AfterMove"), Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        after.Should().NotBeNull("once the move has answered, the source path is writable again");
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

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static MeshNode NodeAt(string path, string name) =>
        MeshNode.FromPath(path) with { Name = name, NodeType = "Markdown", State = MeshNodeState.Active };

    private Task<MeshNode> Create(string path, string name, CancellationToken ct) =>
        NodeFactory.CreateNode(NodeAt(path, name))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}

/// <summary>The outcome of the trap's one late write: <see cref="Refusal"/> is null when it landed.</summary>
internal sealed record LateWriteOutcome(string? Refusal);

/// <summary>What the trap writes, and under which root it fires.</summary>
internal sealed record ArmedLateWrite(string Root, IStorageAdapter Storage, MeshNode Late, JsonSerializerOptions Options);

/// <summary>
/// A delete validator that records every path it is asked about and, the FIRST time it is asked
/// about the armed root, writes one extra node under it through the outermost storage adapter (the
/// in-process writer the subtree write guard governs). Its answer waits for that write to settle, so
/// the outcome is recorded before the move can proceed. Instance state, owned by the test's mesh.
/// </summary>
internal sealed class LateWriteTrapValidator : INodeValidator
{
    private ImmutableHashSet<string> validated = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    private ArmedLateWrite? armed;
    private volatile LateWriteOutcome? outcome;

    /// <summary>Every path a delete validation was asked about.</summary>
    public ImmutableHashSet<string> Validated => validated;

    /// <summary>The late write's outcome; null until the trap has fired.</summary>
    public LateWriteOutcome? Outcome => outcome;

    /// <summary>Fire once, on the next delete validation of <paramref name="root"/>.</summary>
    public void Arm(string root, IStorageAdapter storage, MeshNode late, JsonSerializerOptions options) =>
        armed = new ArmedLateWrite(root, storage, late, options);

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        ImmutableInterlocked.Update(ref validated, s => s.Add(context.Node.Path));
        var fire = armed is { } a && string.Equals(a.Root, context.Node.Path, StringComparison.OrdinalIgnoreCase)
            ? Interlocked.Exchange(ref armed, null)
            : null;
        if (fire is not { } f)
            return Observable.Return(NodeValidationResult.Valid());

        return f.Storage.Write(f.Late, f.Options)
            .Take(1)
            .Select(_ => new LateWriteOutcome(null))
            .Catch((Exception ex) => Observable.Return(new LateWriteOutcome(ex.Message)))
            .DefaultIfEmpty(new LateWriteOutcome("the write completed without an answer"))
            .Do(o => outcome = o)
            .Select(_ => NodeValidationResult.Valid());
    }
}
