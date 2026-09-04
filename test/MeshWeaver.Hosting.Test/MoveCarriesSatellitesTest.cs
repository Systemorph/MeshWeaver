using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A MOVE relocates a node AND everything that belongs to it — its satellites included — or it
/// refuses. Issue #3272: it did neither. A move is copy-to-target + delete-the-source, and the two
/// legs enumerated the subtree through DIFFERENT surfaces:
///
/// <list type="bullet">
///   <item>the <b>copy</b> leg read the content query <c>path:{source} scope:subtree</c>, which is a
///     PRIMARY-table read on every backend and therefore returns no <c>_Comment</c>/<c>_Thread</c>/
///     <c>_Access</c> row at all (in-memory:
///     <c>StorageAdapterMeshQueryProvider.IsExcludedFromResults</c>, mirroring PG's table
///     separation; Postgres: <c>ResolveQueryTable</c> unions the CONTENT satellite tables
///     — <c>Source</c>/<c>Test</c> → <c>code</c> — and deliberately not the metadata ones);</item>
///   <item>the <b>delete</b> leg enumerated <c>IStorageAdapter.ListDescendantPaths</c>, "a native
///     prefix enumeration across every table of the partition". Satellites included.</item>
/// </list>
///
/// <para>Copy skipped them; delete removed them; the move reported success. The satellite prefixes
/// are where the durable, hard-to-recreate context lives — comments, threads, approvals, and
/// <c>_Access</c> grants — and there is no version history to recover from.</para>
///
/// <para>🚨 The first test's Output lines are the MEASUREMENT, not decoration: they print what the
/// copy leg's own query returns, so the record shows the satellite is still absent from it and that
/// the node nonetheless arrives at the target. Reading a green tick without them would not
/// distinguish "the sweep works" from "the query started returning satellites".</para>
/// </summary>
public class MoveCarriesSatellitesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>
    /// The satellites of the moved node, of a DESCENDANT of it, and a NESTED satellite
    /// (a reply filed under a comment) all ride along. The three shapes are separate
    /// assertions because each exercises a different part of the sweep: the owner set covers the
    /// root, the owner set covers descendants, and one query per CONTAINER carries whatever is
    /// nested beneath it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Move_CarriesTheSatellitesOfTheNodeAndOfItsDescendants()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sourceRoot = $"{TestPartition}/sat-{suffix}";
        var sourceChild = $"{sourceRoot}/Pricing";
        var targetRoot = $"{TestPartition}/satmoved-{suffix}";

        var rootComment = "_Comment/c1";
        var childComment = "Pricing/_Comment/c2";
        var nestedReply = "_Comment/c1/_Comment/reply";

        await NodeFactory.CreateNode(MeshNode.FromPath(sourceRoot) with
        {
            Name = "Doc",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath(sourceChild) with
        {
            Name = "Pricing",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath($"{sourceRoot}/{rootComment}") with
        {
            Name = "A comment on the doc",
            NodeType = "Comment",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath($"{sourceRoot}/{childComment}") with
        {
            Name = "A comment on the child",
            NodeType = "Comment",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath($"{sourceRoot}/{nestedReply}") with
        {
            Name = "A reply to the comment",
            NodeType = "Comment",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var before = await Storage.ListDescendantPaths(sourceRoot)
            .Should().Within(TestTimeouts.Convergence).Emit();
        Output.WriteLine($"BEFORE storage descendants of {sourceRoot}: [{string.Join(", ", before.OrderBy(p => p, StringComparer.Ordinal))}]");

        // The copy leg's own read. It returns the MAIN subtree and nothing else — which is the
        // whole defect: what it cannot see, the delete leg still destroys.
        var copyLegSees = await MeshQuery.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{sourceRoot} scope:subtree").Complete())
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => c.Items)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit();
        Output.WriteLine($"COPY-LEG QUERY 'path:{sourceRoot} scope:subtree' returns: "
            + $"[{string.Join(", ", copyLegSees.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal))}]");

        var moved = await ObserveNodeOperation(new MoveNodeRequest(sourceRoot, targetRoot))
            .Should().Within(TestTimeouts.Convergence).Emit();
        Output.WriteLine($"MOVE success={moved.Message.Success} error={moved.Message.Error}");
        moved.Message.Success.Should().BeTrue(moved.Message.Error ?? "the move must succeed");

        var after = await Storage.ListDescendantPaths(targetRoot)
            .Should().Within(TestTimeouts.Convergence).Emit();
        Output.WriteLine($"AFTER  storage descendants of {targetRoot}: [{string.Join(", ", after.OrderBy(p => p, StringComparer.Ordinal))}]");

        var leftBehind = await Storage.ListDescendantPaths(sourceRoot)
            .Should().Within(TestTimeouts.Convergence).Emit();
        Output.WriteLine($"AFTER  storage descendants of {sourceRoot}: [{string.Join(", ", leftBehind.OrderBy(p => p, StringComparer.Ordinal))}]");

        foreach (var tail in new[] { rootComment, childComment, nestedReply })
        {
            after.Should().Contain(
                p => string.Equals(p, $"{targetRoot}/{tail}", StringComparison.OrdinalIgnoreCase),
                $"the satellite at {tail} must ride along with the node it belongs to");
            leftBehind.Should().NotContain(
                p => string.Equals(p, $"{sourceRoot}/{tail}", StringComparison.OrdinalIgnoreCase),
                "the source subtree is gone after a move");
        }

        // The node, not merely a path: a satellite carried as an empty shell would satisfy the
        // enumeration above and still have lost the comment.
        var carriedComment = await ReadNode($"{targetRoot}/{rootComment}")
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n is not null, $"the comment must exist at {targetRoot}/{rootComment}");
        carriedComment!.Name.Should().Be("A comment on the doc", "the satellite's content travels with it");
        carriedComment.NodeType.Should().Be("Comment");
        carriedComment.MainNode.Should().Be(targetRoot,
            "a satellite's MainNode is retargeted with the node it hangs off — left pointing at the "
            + "old path it names a node that no longer exists, and its grants project nowhere");
    }

    /// <summary>
    /// The other half of "relocates everything, or refuses". <see cref="CopyNodeRequest.RequireComplete"/>
    /// is asserted BEFORE anything is created, so a copy that would leave part of the stored subtree
    /// behind fails having written nothing — which is what keeps the move's delete leg from running
    /// at all. Exercised here with <c>IncludeSatellites = false</c> because that is the one shape a
    /// test can construct deterministically: the sweep is switched off, so the satellite is
    /// genuinely uncarryable, exactly as an unreadable node would be in production.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RequireComplete_RefusesTheCopy_WhenSomethingWouldBeLeftBehind()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sourceRoot = $"{TestPartition}/incomplete-{suffix}";
        var targetRoot = $"{TestPartition}/incompletecopy-{suffix}";

        await NodeFactory.CreateNode(MeshNode.FromPath(sourceRoot) with
        {
            Name = "Doc",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath($"{sourceRoot}/_Comment/c1") with
        {
            Name = "A comment",
            NodeType = "Comment",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var refused = await ObserveNodeOperation(new CopyNodeRequest(sourceRoot, targetRoot)
            {
                IncludeDescendants = true,
                IncludeSatellites = false,
                RequireComplete = true,
            })
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"COPY success={refused.Message.Success} error={refused.Message.Error}");

        refused.Message.Success.Should().BeFalse(
            "a copy that cannot carry the whole stored subtree must refuse, not report success");
        refused.Message.Error.Should().StartWith(CopyNodeRequest.IncompleteCopyRefusal);
        refused.Message.Error.Should().Contain($"{sourceRoot}/_Comment/c1",
            "the refusal names what it could not carry — a refusal that does not is a mystery");
        refused.Message.RejectionReason.Should().Be(NodeCopyRejectionReason.ValidationFailed);

        // Nothing was written at the target: the check runs before the first create.
        var target = await ReadNode(targetRoot).Should().Within(TestTimeouts.Convergence)
            .Match(n => n is null, "the refusal happens before anything is created");
        target.Should().BeNull();

        // And the source is untouched.
        var stillThere = await Storage.ListDescendantPaths(sourceRoot)
            .Should().Within(TestTimeouts.Convergence).Emit();
        stillThere.Should().Contain(
            p => string.Equals(p, $"{sourceRoot}/_Comment/c1", StringComparison.OrdinalIgnoreCase),
            "a refused copy removes nothing");
    }
}
