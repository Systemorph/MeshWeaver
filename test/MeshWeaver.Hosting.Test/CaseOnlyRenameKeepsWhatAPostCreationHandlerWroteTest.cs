using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
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
/// 🚨 A failed case-only rename leg leaves what a post-creation handler wrote at the target alone
/// (#6951, review of #6976).
///
/// <para>A successful create runs the post-creation handlers that match the node, and they write
/// elsewhere — a Space's creator grant below its root, its partition record. None of that is in the
/// create's answer. The undo used to remove the rows the copy wrote, move the subtree back and answer
/// "nothing was renamed" with a generated child still stored under the new casing. A failed rename
/// now never deletes at its target: the copy wrote the root, so the answer is Unknown and the root and
/// everything under it are left for the operator.</para>
///
/// <para>Isolated fault: a best-effort post-creation handler that matches exactly the TARGET root
/// (compared ordinally — leg 1's staging root is untouched) and writes one child under it, and a
/// create validator that refuses exactly one descendant at the target's casing. The controls: the
/// generated child is still stored, and the staging path the answer names holds the whole subtree.</para>
/// </summary>
public class CaseOnlyRenameKeepsWhatAPostCreationHandlerWroteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string StagingMarker = "-recase-";

    private readonly RefuseExactPathCreationValidator refusing = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(refusing)
            .AddSingleton<ChildWritingHandler>()
            .AddSingleton<INodePostCreationHandler>(sp => sp.GetRequiredService<ChildWritingHandler>()));

    [Fact(Timeout = 90_000)]
    public async Task APostCreationChildWrite_ThenARefusedDescendant_IsUnknown_AndNothingIsUndone()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/vhvgruppe-{id}";
        var target = $"{TestPartition}/VHVGruppe-{id}";
        await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);
        await Create($"{source}/birgit/notes", "Notes", ct);
        var handler = Mesh.ServiceProvider.GetRequiredService<ChildWritingHandler>();
        handler.WriteUnder(target);
        refusing.Refuse($"{target}/birgit");

        var moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        // The control: the handler ran, and what it wrote is still there.
        (await StoredAt($"{target}/{ChildWritingHandler.ChildId}", ct)).Should().NotBeNull(
            "the post-creation handler wrote its child under the target root");

        moved.Success.Should().BeFalse("the second leg's descendant create was refused");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.Unknown,
            "the copy had already written the target root, and a failed rename never deletes at its target");
        moved.Error.Should().NotContain("The subtree was moved back", "nothing was moved back");
        var staging = StagingNamedIn(moved.Error!, target);
        moved.Log.Should().NotBeNull("the outcome travels as a keyed transcript");
        moved.Log!.Messages.Select(m => m.MessageKey)
            .Should().Contain("activity.node.move.caseRenameCopyWrote", "a German viewer reads it in German");

        (await StoredAt(target, ct)).Should().NotBeNull("the target root whose handlers ran was left as it is");
        (await StoredAt(staging, ct)).Should().NotBeNull("the complete copy is at the staging path the answer names");
        (await Descendants(staging, ct)).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            [$"{staging}/birgit", $"{staging}/birgit/notes"], "the staging copy is complete");
    }

    private static string StagingNamedIn(string error, string target)
    {
        var start = error.IndexOf($"'{target}{StagingMarker}", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the answer names the staging path; it said: {error}");
        var end = error.IndexOf('\'', start + 1);
        return error[(start + 1)..end];
    }

    private Task<MeshNode?> StoredAt(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<IReadOnlyCollection<string>> Descendants(string path, CancellationToken ct) =>
        Storage.ListDescendantPaths(path)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode> Create(string path, string name, CancellationToken ct) =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = name,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    /// <summary>
    /// A best-effort post-creation handler that, for exactly the paths it is told to (compared
    /// ORDINALLY), writes one child node under the created node — the shape of a Space's creator
    /// grant. Instance state owned by the test's mesh, never static.
    /// </summary>
    internal sealed class ChildWritingHandler(IServiceProvider services) : INodePostCreationHandler
    {
        public const string ChildId = "generated";

        private ImmutableHashSet<string> paths = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

        public void WriteUnder(string path) => ImmutableInterlocked.Update(ref paths, s => s.Add(path));

        /// <summary>Diagnostic label only — <see cref="Matches"/> decides.</summary>
        public string NodeType => "Markdown";

        public bool Matches(MeshNode createdNode) => paths.Contains(createdNode.Path);

        public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy)
        {
            var hub = services.GetRequiredService<MeshWeaver.Messaging.IMessageHub>();
            return services.GetRequiredService<IStorageAdapter>()
                .Write(MeshNode.FromPath($"{createdNode.Path}/{ChildId}") with
                    {
                        Name = "Generated",
                        NodeType = "Markdown",
                        State = MeshNodeState.Active,
                    }, hub.JsonSerializerOptions)
                .Take(1)
                .Select(_ => Unit.Default);
        }
    }
}
