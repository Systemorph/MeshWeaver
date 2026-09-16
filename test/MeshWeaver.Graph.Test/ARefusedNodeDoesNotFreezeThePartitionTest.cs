using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Issues #4459 and #4456 — ONE refused node froze a whole partition out of the mesh, and
/// nothing named the node.</b>
///
/// <para><b>Measured on memex.systemorph.com, 2026-09-15.</b> A single file —
/// <c>Hosting/Deployment/Source/SelfUpdateRouting.cs</c> — carried a LITERAL NUL byte (0x00) inside a
/// char literal. PostgreSQL cannot store a NUL in a text column, so the Hosting GitSync import
/// refused that ONE row while forty others landed. The pass therefore earned
/// <c>ImportedWithContentErrors</c> — every failure in it was a verdict about the bytes — and the
/// content-addressed marker recorded that verdict FOR THE PARTITION. Every later import then
/// answered:</para>
/// <code>
/// Skipped — an earlier FULL import already recorded this exact content at fingerprint
/// bb9801101e859a21 (Hosting/_Activity/import-bb9801101e859a21), so the partition was not re-read
/// </code>
/// <para>which is not true: the import lost a node, and that sentence claims the content was
/// recorded. Five Hosting NodeTypes stayed parked on <c>CS0246 SelfUpdateRouting</c> — among them
/// <c>Hosting/InstanceAction</c>, so NO instance action could run on the control instance at all (no
/// Sample, no Logs, no HelmRelease, no Roll) — and the operator's one repair, a re-import, was
/// refused with that same sentence. The failure count was reported without a single path, so nobody
/// could tell WHICH node was missing; the visible symptom was a compile error on a file that is
/// plainly in git.</para>
///
/// <para>🚨 <b>The defect is GRANULARITY, not the #3146 rule it came from.</b> #3146's argument is
/// correct and is pinned in <see cref="FailedImportIsNotRetriedAtTheSameFingerprintTest"/>: the
/// marker is content-addressed, so re-issuing a write the owner refused re-derives the identical
/// refusal, and doing it anyway cost memex-cloud 19 full passes in 3 h. But a content verdict is
/// earned by a NODE — one file that Postgres, a validator or RLS refuses — and recording it as a
/// verdict about the PARTITION is what turns one unstorable byte into a frozen partition. The
/// memory now lives per node, in the partition's import manifest, so both hold at once: the failing
/// write is not re-issued, AND every other node is still evaluated.</para>
///
/// <para>The two tests below are the two halves. The first is the freeze (#4459/#4456); the second
/// is the storm the fix must not reintroduce, measured on WRITE REQUESTS rather than on the word
/// "Skipped" — which is the quantity #3146 was ever about.</para>
/// </summary>
public class ARefusedNodeDoesNotFreezeThePartitionTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    // 🚨 A DERIVED FIELD INITIALIZER RUNS BEFORE THE BASE CONSTRUCTOR, and the base constructor is
    // what calls ConfigureMesh — so both of these are already set when the validator is registered.
    private readonly string _partition = "Rf" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The one path the test validator refuses. A validator refusal is mapped to
    /// <c>NodeUpsertRejectionReason.ValidationFailed</c>, which <c>StaticRepoImporter.IsContentVerdict</c>
    /// classifies as a verdict about the bytes — the same classification the live Postgres NUL-byte
    /// refusal earned, reproduced without needing a store that refuses a byte.
    /// </summary>
    private string? _refusePath;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(
                new RefuseOnePathValidator(() => _refusePath)));

    /// <summary>
    /// 🚨 <b>THE PIN FOR #4459/#4456.</b> One node is refused; every other node in the partition must
    /// still be evaluated by the next import — including one that has since gone MISSING from the
    /// mesh, which is precisely the repair an operator reaches for and precisely the repair the
    /// whole-partition skip refused.
    ///
    /// <para>Deleting <c>Good</c> between the passes stands in for the ways a partition drifts from
    /// its marker (a cross-partition prune, a manual delete, a botched migration) — the same drift a
    /// GREEN marker has had a content sentinel for since the "a user didn't see the core app skills"
    /// report. A marker recording a content verdict had no such path at all: it short-circuited
    /// before reading anything.</para>
    ///
    /// <para>Pre-fix the second pass answers <c>Skipped</c> and writes nothing, so <c>Good</c> stays
    /// missing for ever at this fingerprint. Post-fix the refusal is remembered against
    /// <c>Bad</c> alone and <c>Good</c> is re-created.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AContentRefusalOnOneNode_DoesNotStopTheNextImportRepairingTheRest()
    {
        var ct = TestContext.Current.CancellationToken;
        _refusePath = $"{_partition}/Bad";
        var source = new FixtureSource(_partition)
        {
            Root = Space(_partition),
            Nodes = [Page(_partition, "Good"), Page(_partition, "Bad")],
        };

        var first = await Import(source, ct);
        first.Outcome.Should().Be("ImportedWithContentErrors",
            "the one failure in this pass is a validator refusing these bytes — a verdict about the "
            + "content, which is what the live NUL-byte refusal was too");
        first.WrittenPaths.Should().Contain($"{_partition}/Good",
            "the refusal is per file: every other node still lands");

        // 🚨 THE WAIT BELOW MUST BE ABLE TO FAIL. It waits for `Good` to LEAVE the parent listing, so
        // a listing that never contained `Good` in the first place would satisfy it instantly and the
        // test would pass having deleted nothing and repaired nothing. Assert the positive case first:
        // if the query shape ever stops seeing the node, this goes red here rather than passing
        // vacuously three lines later.
        (await ChildPaths(_partition, ct)).Should().Contain($"{_partition}/Good",
            "the listing this test waits on must actually see the node, or waiting for it to "
            + "disappear proves nothing");

        // The partition now drifts from what the marker claims — the state an operator re-imports to
        // repair. Waited on through the QUERY the importer itself reads, not merely posted: a stale
        // snapshot would let the incremental skip pass over the node and prove nothing.
        await Delete($"{_partition}/Good", ct);
        await WaitUntilAbsentFromTheIndex(_partition, "Good", ct);

        var second = await Import(source, ct);

        second.Outcome.Should().NotBe("Skipped",
            "the marker recorded a verdict earned by ONE node; answering Skipped for the PARTITION "
            + "claims 'an earlier FULL import already recorded this exact content' about content the "
            + "import demonstrably lost, and it refuses the operator's only repair. That sentence is "
            + "what memex.systemorph.com was given while Hosting/InstanceAction sat parked on a "
            + "symbol whose file was in git the whole time (#4459/#4456)");
        second.WrittenPaths.Should().Contain($"{_partition}/Good",
            "a node that has gone missing must be re-created by the next import — the refusal belongs "
            + "to Bad, and nothing about Bad's bytes says anything about Good");
        (await Body($"{_partition}/Good", ct)).Should().Contain("page",
            "and it is really back in the mesh, not merely reported as written");
    }

    /// <summary>
    /// 🚨 <b>THE CASE THAT COULD FALSIFY THE ONE ABOVE — #3146's storm must stay prevented.</b>
    ///
    /// <para>The freeze is removed by re-importing where the old code skipped, so the obvious way to
    /// get this wrong is to re-issue the refused write on every trigger: 19 full passes in 3 h, ≈425
    /// identical failing upserts plus a NodeType compile each, on a portal already at 8/8 replicas.
    /// So the second pass is measured on <see cref="StaticRepoImportResult.WriteRequests"/> — the
    /// requests actually issued, counted at subscribe — which is the quantity that storm was made of.
    /// Asserting an outcome word would stay green through a revert to re-attempting every node.</para>
    ///
    /// <para>And the pass must still SAY the partition is incomplete. Going quiet would be the other
    /// failure: a green import over a partition missing a node is exactly how the Hosting outage went
    /// unnoticed until five NodeTypes parked.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheRefusedWriteIsNotReIssued_AndTheIncompletenessIsStillReported()
    {
        var ct = TestContext.Current.CancellationToken;
        _refusePath = $"{_partition}/Bad";
        var source = new FixtureSource(_partition)
        {
            Root = Space(_partition),
            Nodes = [Page(_partition, "Good"), Page(_partition, "Bad")],
        };

        var first = await Import(source, ct);
        first.WriteRequests.Should().BeGreaterThan(0, "the first pass really did try to write");

        var second = await Import(source, ct);

        second.WriteRequests.Should().Be(0,
            "nothing in this pass can do anything: Good is unchanged and present, and Bad was already "
            + "refused at exactly this token. Re-issuing either is the #3146 storm — ≈425 identical "
            + "failing upserts plus a NodeType compile per pass, 19 passes in 3 h on memex-cloud");
        second.Failed.Should().Be(1,
            "and it must not go QUIET about it either: the partition is still missing a node the "
            + "source declares, and a green import over that is how one dropped file went unnoticed "
            + "until five NodeTypes parked on it");
        second.Outcome.Should().Be("ImportedWithContentErrors",
            "the verdict is unchanged — these bytes still break the same rule");
    }

    /// <summary>
    /// 🚨 The reporting half of #4459 (defect 2) and #4456 (suggestion 1): <i>"names a count (9),
    /// never the paths"</i>. Every other thing an import can leave behind names its paths —
    /// <see cref="StaticRepoImportResult.WrittenPaths"/>, <c>PrunedPaths</c>,
    /// <c>BlockedCreatePaths</c>, <c>HeldNodeTypePaths</c>, <c>RefusedContent</c>. A failure was the
    /// one reported as a bare number, and with it the operator had nothing to act on.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AFailedNode_IsNamedWithItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        _refusePath = $"{_partition}/Bad";
        var source = new FixtureSource(_partition)
        {
            Root = Space(_partition),
            Nodes = [Page(_partition, "Good"), Page(_partition, "Bad")],
        };

        var result = await Import(source, ct);

        result.FailedPaths.Should().HaveCount(1,
            "one node did not land, and the result must carry it — the count and the paths have to "
            + "agree, because Failed alone is what the sync baseline reads and the paths are what a "
            + "person reads");
        var failure = result.FailedPaths[0];
        failure.NodePath.Should().Be($"{_partition}/Bad");
        failure.Reason.Should().NotBeNullOrWhiteSpace(
            "'refused' alone is indistinguishable between a byte Postgres cannot store, a validator "
            + "rule and an RLS denial — three problems with three different fixes (#3101's argument, "
            + "for nodes this time)");
        failure.Deterministic.Should().BeTrue(
            "a validator refusal is a verdict about the bytes, and that flag is what the manifest "
            + "records so the next pass does not re-issue the write");
    }

    private async Task<StaticRepoImportResult> Import(FixtureSource source, CancellationToken ct)
    {
        // 🚨 .Await(), never a bare `await` on the observable: Rx's own awaiter resumes the
        // continuation INLINE on the signalling thread, inside the trampoline, and every later await
        // in the method inherits that scheduler.
        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds()).Await(ct);
        Output.WriteLine(
            $"outcome={result.Outcome} count={result.Count} failed={result.Failed} "
            + $"writeRequests={result.WriteRequests} written=[{string.Join(", ", result.WrittenPaths)}]");
        return result;
    }

    private async Task Delete(string path, CancellationToken ct) =>
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .DeleteNode(path)
            .FirstAsync().Timeout(60.Seconds()).Await(ct);

    /// <summary>
    /// Waits until the eventually-consistent index the importer's own snapshot reads no longer lists
    /// the node — the re-query shape the house rules prescribe for a request/response source, never a
    /// <c>Task.Delay</c>. Without it the next import could read a stale "present", skip the node on
    /// its token, and the test would pass having measured nothing.
    ///
    /// <para>🚨 A <c>scope:children</c> LISTING of the parent, never a <c>path:</c> point query.
    /// Existence of a specific path is not a valid query use — a point read of an absent node is a
    /// routing NotFound that terminates the stream and opens the storm-breaker on that path — so the
    /// existence half of the question is asked the way the CQRS rules say to ask it: list the parent
    /// and look for the child. (Copilot review.)</para>
    /// </summary>
    private async Task WaitUntilAbsentFromTheIndex(string partition, string id, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var path = $"{partition}/{id}";
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ChildPathsOnce(meshService, partition))
            .Where(paths => !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            .FirstAsync().Timeout(60.Seconds()).Await(ct);
    }

    /// <summary>One reading of the parent's child listing — the same listing the wait above polls, so
    /// the pre-delete assertion and the wait can never disagree about what they are looking at.</summary>
    private async Task<IReadOnlyList<string>> ChildPaths(string partition, CancellationToken ct) =>
        await ChildPathsOnce(Mesh.ServiceProvider.GetRequiredService<IMeshService>(), partition)
            .FirstAsync().Timeout(60.Seconds()).Await(ct);

    private static IObservable<IReadOnlyList<string>> ChildPathsOnce(
        IMeshService meshService, string partition) =>
        meshService
            // .Complete() — this is an ENUMERATION used as an existence gate, so it must never be
            // silently served as a page.
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{partition} scope:children").Complete())
            .Take(1)
            .Select(change => (IReadOnlyList<string>)change.Items.Select(n => n.Path).ToArray());

    private async Task<string> Body(string path, CancellationToken ct)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(60.Seconds()).Await(ct);
        return node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "";
    }

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = partition, NodeType = "Space", State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {partition}\n\nfixture." }
    };

    private static MeshNode Page(string partition, string id) => new(id, partition)
    {
        NodeType = "Markdown", Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}\n\npage" }
    };

    private sealed class FixtureSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;

        /// <summary>
        /// Immutable, per the repository's collections policy — the fixture only ever initializes it,
        /// so nothing is lost by refusing a mutable one here. (Copilot review.)
        /// </summary>
        public ImmutableList<MeshNode> Nodes { get; init; } = ImmutableList<MeshNode>.Empty;

        public MeshNode? Root { get; init; }

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
        public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => [];
    }

    /// <summary>
    /// Refuses ONE path. The bulk verb runs the same <c>INodeValidator</c> pass the singular create
    /// runs, so the refusal reaches the importer as
    /// <c>NodeUpsertRejectionReason.ValidationFailed</c> — a content verdict.
    /// </summary>
    private sealed class RefuseOnePathValidator(Func<string?> path) : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => Observable.Return(
                string.Equals(context.Node.Path, path(), StringComparison.Ordinal)
                    ? NodeValidationResult.Invalid(
                        $"'{context.Node.Path}' is refused by the test validator")
                    : NodeValidationResult.Valid());
    }
}
