using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver#1198 — a commit-stage delete timeout named how many paths it had REMOVED
/// and never which ones it was STUCK ON, while printing an <c>unanswered=</c> field that said there
/// were none.
///
/// <para><b>The production line</b>, memex-cloud 2026-09-14T08:45:38Z, verbatim:</para>
/// <code>
/// [DeleteNode] timeout path=Hosting/TriageStatus stage=commit partial-deleted=3 unanswered=-
/// System.TimeoutException: [DeleteNode:commit] the bottom-up delete of 'Hosting/TriageStatus'
///     made no progress for 30s — 3 path(s) removed from storage so far
/// </code>
///
/// <para><b>Why that is worse than saying nothing.</b> The shared <c>[DeleteNode] timeout</c> line
/// carries an <c>unanswered=</c> field precisely so an operator learns WHICH node stopped the
/// operation — the sibling stage, <c>pre-validate-descendants</c>, fills it and its occurrences read
/// <c>unanswered=sglauser/AgenticBusiness/01-MeetYourCoworker/AskAdvisor, …</c>. The commit stage
/// never set it, so it rendered <c>-</c> — and <c>-</c> is the SAME rendering the line uses for
/// "there is nothing outstanding". The field did not abstain, it asserted the opposite of the
/// truth: the drain was stuck on the rest of the subtree and the report said it owed nothing.
/// The count of what SUCCEEDED does not identify what failed.</para>
///
/// <para><b>Both halves are in hand at the timeout.</b> The plan (<c>collected.ToDelete</c>) and
/// the progress (<c>SnapshotProgress()</c>) are both live in that closure; the difference is the
/// answer, and it costs one set operation on a path that has already failed.</para>
///
/// <para><b>The repro.</b> A storage adapter that serves the first two deletes and then goes silent
/// — the store took the call and never answered, which is exactly the state the no-progress
/// watchdog exists for. Nothing here widens or races a bound: the watchdog's own budget ends the
/// wait, and the assertion is about what it SAYS when it does.</para>
/// </summary>
public class DeleteCommitTimeoutNamesWhatIsStuckTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RootId = "commit-stuck";

    /// <summary>
    /// Small enough to keep the test quick, large enough that the watchdog has something to fire
    /// on. Every nested rung derives from it, so nothing else has to be configured.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A chain <c>root/l0/l1/l2/l3/l4</c>. The drain is bottom-up, so with
    /// <see cref="ServedBeforeStall"/> served the two deepest are gone and the walk stalls on
    /// <c>l2</c> — which makes "the owed set is a real difference, not the plan echoed back"
    /// something the test can assert in both directions.
    /// </summary>
    private const int Depth = 5;

    /// <summary>
    /// Two, not zero: the production occurrence had <c>partial-deleted=3</c>, so the drain must
    /// really have made progress before it stopped. A drain that removed NOTHING would let the
    /// assertions below pass on a plan that was never touched.
    /// </summary>
    private const int ServedBeforeStall = 2;

    private readonly LatentDeleteStorageAdapter storage = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStorageAdapter>(storage);
            services.AddSingleton(new MeshOperationOptions { Timeout = Budget });
            return services;
        }));

    [Fact]
    public async Task ACommitThatStalls_NamesThePathsTheDrainStillOwes()
    {
        var rootPath = $"{TestPartition}/{RootId}";
        await NodeFactory.CreateNode(
                new MeshNode(RootId, TestPartition) { Name = "Commit root", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit();

        var parent = rootPath;
        for (var i = 0; i < Depth; i++)
        {
            var id = $"l{i}";
            await NodeFactory.CreateNode(
                    new MeshNode(id, parent) { Name = id, NodeType = "Markdown" })
                .Should().Within(TestTimeouts.Convergence).Emit();
            parent = $"{parent}/{id}";
        }

        storage.LatencyRoot = rootPath;
        storage.StallAfterDeletes = ServedBeforeStall;

        // Producer -> test signal: the delete's ERROR arm completes an AsyncSubject the assertion
        // helpers await. A success emission leaves it empty and times the wait out, which is
        // itself the failure this test must report.
        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });

        var reported = await failure.Should().Within(TestTimeouts.WriteConvergence).Emit(
            "the store went silent mid-commit, so the no-progress watchdog must end the operation");

        Output.WriteLine(reported.Message);

        // POSITIVE CONTROL #1 — this really is the commit stage's no-progress watchdog, and not
        // some earlier stage timing out. Without this the assertions below could be satisfied by a
        // different failure that happens to mention paths.
        reported.Message.Should().Contain("[DeleteNode:commit]",
            "the subject is the COMMIT stage — a pre-flight timeout is the sibling report that "
            + "already named its outstanding set");
        reported.Message.Should().Contain("made no progress for",
            "the bound is a no-progress watchdog, not a total-duration cap (#3392) — if this read "
            + "'did not drain within' the test would be measuring the wrong mechanism");

        // POSITIVE CONTROL #2 — the COUNT is still reported. The names are ADDED to the report,
        // never traded against the progress figure #1198 already fought to get onto this line.
        reported.Message.Should().Contain("planned path(s) removed from storage so far",
            "reporting the real progress is the half of #1198 that already landed — a rewrite that "
            + "swapped one fact for another would read as a fix");

        // THE SUBJECT. The paths the plan still owes, by name.
        reported.Message.Should().Contain("still owed by the plan:",
            "the count of what SUCCEEDED does not identify what failed — and the shared "
            + "[DeleteNode] timeout line renders an unset outstanding set as '-', which is the same "
            + "rendering it uses for 'there is nothing outstanding'");

        // The walk is bottom-up, so the two deletes the store DID serve removed the two deepest
        // nodes and the stall landed on their parent. Naming it is the answer an operator needs.
        var stalledOn = $"{rootPath}/l0/l1/l2";
        reported.Message.Should().Contain(stalledOn,
            "the report must name the path the drain is stuck on — that is the node whose hub or "
            + "store an operator has to go and look at");

        // NEGATIVE CONTROL — the owed set is a real DIFFERENCE, not the plan echoed back. These two
        // were removed before the store went silent, so a report that listed them would be telling
        // the operator to investigate work that succeeded.
        reported.Message.Should().NotContain($"{rootPath}/l0/l1/l2/l3",
            "the two deepest nodes were removed before the stall — an 'owed' set that still names "
            + "them is the whole plan under a new label, which identifies nothing");

        // 🚨 And the report is checked against the STORE OF RECORD, not only against itself. Both
        // assertions above read the message; a message can be internally consistent and still
        // describe a state that never happened. These two read the backing store: the path the
        // report names is really still there, and the ones it does not name are really gone.
        (await storage.Inner.Exists(stalledOn).Should().Within(TestTimeouts.Convergence).Emit())
            .Should().BeTrue(
                "the report claims this path is still owed — if the store says it is gone, the "
                + "report is naming the wrong node, which is worse than naming none");
        (await storage.Inner.Exists($"{rootPath}/l0/l1/l2/l3/l4")
                .Should().Within(TestTimeouts.Convergence).Emit())
            .Should().BeFalse(
                "the deepest node was one of the two the store served before it went silent — so "
                + "progress really was PARTIAL, which is the production shape (partial-deleted=3) "
                + "and the only shape in which a count without names identifies nothing");
    }
}
