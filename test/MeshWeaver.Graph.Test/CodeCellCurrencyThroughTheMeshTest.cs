using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #3249, through a real mesh: the state a swallowed last-execution stamp leaves in STORAGE must
/// not read back as "up to date".
///
/// <para><see cref="CodeOutputCurrencyTest"/> pins the rule as a pure function. This class pins the
/// two things a pure function cannot: that the state actually survives the write/serialize/read
/// round trip as the rule expects (a hash absent on the wire is an ABSENT member, not an empty
/// string, and the run markers around it must still arrive), and that a genuine run through
/// <c>CodeNodeType</c>'s own dispatch does reach <see cref="CodeOutputCurrency.Current"/> — a
/// fail-closed rule that never says "current" would be no better than the fail-open one it
/// replaced.</para>
///
/// <para>Every read is <c>GetMeshNodeStream(path)</c> — the authoritative, live single-node read —
/// never a query, and every wait is on the condition itself.</para>
/// </summary>
public class CodeCellCurrencyThroughTheMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Source = "1 + 1";

    private static TimeSpan Bound => TestTimeouts.Convergence;

    /// <summary>
    /// The defect's own shape: the node records a run — timestamp, runner, activity pointer — and
    /// records nothing about WHAT it ran, because the write that would have carried the fingerprint
    /// did not land. Read back off the node stream, that cell must not claim to be current.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARunTheNodeDidNotRecord_IsNotReadBackAsUpToDate()
    {
        var path = await CreateCell(new CodeConfiguration
        {
            Code = Source,
            IsExecutable = true,
            // It ran …
            LastExecutedAt = DateTimeOffset.UtcNow,
            LastExecutedBy = "rbuergi",
            LastActivityPath = $"{TestPartition}/_Activity/{Guid.NewGuid():N}",
            // … and the stamp's proof of WHAT it ran never arrived.
            LastExecutedCodeHash = null,
        });

        var cell = await ReadCell(path, c => c is { Code: Source });

        cell.LastExecutedCodeHash.Should().BeNull(
            "the round trip must preserve the state under test — if storage had invented a hash "
            + "there would be nothing here to fail closed about");
        cell.ProvesOutputIsCurrent().Should().BeFalse(
            "the cell records that it ran but not what it ran, so it cannot substantiate 'up to "
            + "date' — and asserting it anyway is a wrong claim, not a missing one (#3249)");
        cell.OutputCurrency().Should().Be(CodeOutputCurrency.Unverified,
            "the honest verdict is 'unverified': neither Current (nothing proves it) nor Stale "
            + "(which would light up every node stamped before the fingerprint existed)");
    }

    /// <summary>
    /// The control arm, end to end: a run dispatched through <c>ExecuteScriptRequest</c> stamps the
    /// fingerprint, so the cell reads as <see cref="CodeOutputCurrency.Current"/> — and moving the
    /// code underneath that recorded run flips it to <see cref="CodeOutputCurrency.Stale"/>. Without
    /// this, "fails closed" could be satisfied by a rule that simply never says anything is current.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARunTheNodeDidRecord_ReadsAsCurrent_AndGoesStaleWhenTheCodeMoves()
    {
        var path = await CreateCell(new CodeConfiguration { Code = Source, IsExecutable = true });

        var dispatch = await RequestHub
            .Observe<ExecuteScriptResponse>(new ExecuteScriptRequest(), o => o.WithTarget(new Address(path)))
            .FirstAsync()
            .Timeout(Bound)
            .Await();
        dispatch.Message.Success.Should().BeTrue(
            "the dispatch must be accepted before its stamp can be asserted on");

        var ran = await ReadCell(path, c => c is { LastExecutedCodeHash: not null and not "" });
        ran.OutputCurrency().Should().Be(CodeOutputCurrency.Current,
            "the stamp recorded the fingerprint of what the run submitted and the code has not "
            + "moved since — the one state a cell may render as up to date");

        // Move the code under the recorded run — the ordinary way a cell goes stale.
        var options = Mesh.JsonSerializerOptions;
        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(path)
            .Update(current => current with
            {
                Content = (current.ContentAs<CodeConfiguration>(options) ?? new CodeConfiguration())
                    with { Code = "2 + 2" },
            })
            .FirstAsync()
            .Timeout(Bound)
            .Await();

        var edited = await ReadCell(path,
            c => c is { Code: "2 + 2", LastExecutedCodeHash: not null and not "" });
        edited.OutputCurrency().Should().Be(CodeOutputCurrency.Stale,
            "the visible output belongs to source the reader is no longer looking at");
    }

    /// <summary>
    /// #3301, the whole issue in one case: a run whose last-execution stamp NEVER LANDED is still
    /// found, because the Activity node the dispatcher created before dispatching names the cell on
    /// <c>ActivityLog.HubPath</c>.
    ///
    /// <para><b>The repro is deterministic, not simulated.</b> Two cells really run through
    /// <c>ExecuteScriptRequest</c>, so two real Activity nodes exist in the same <c>_Activity</c>
    /// namespace. Then one cell's stamp is WIPED — which is exactly the state #3249's failure paths
    /// leave behind (the write is one node write; it lands whole or not at all). From that point the
    /// cell is, to every reader, indistinguishable from one nobody ever ran.</para>
    ///
    /// <para><b>Three arms, because a lookup that always says "ran" would be worthless.</b> The
    /// wiped cell must be recovered as <see cref="CodeOutputCurrency.Unverified"/>; a cell that
    /// genuinely never ran must stay <see cref="CodeOutputCurrency.NeverRun"/>; and the second,
    /// still-stamped cell proves the <c>content.hubPath</c> predicate is doing the work rather than
    /// the namespace listing finding any activity at all.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARunWhoseStampNeverLandedIsStillFoundByTheActivityItWrote()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var options = Mesh.JsonSerializerOptions;

        // Two cells, both genuinely run: the second one's Activity shares the _Activity namespace,
        // so a lookup that merely listed the namespace would pass on the wrong evidence.
        var wiped = await CreateCell(new CodeConfiguration { Code = Source, IsExecutable = true });
        var neighbour = await CreateCell(new CodeConfiguration { Code = Source, IsExecutable = true });
        await Run(wiped);
        await Run(neighbour);

        // The stamp is one write to one node — it lands whole or not at all. Removing it reproduces
        // the state every #3249 failure path leaves behind.
        await Mesh.GetWorkspace().GetMeshNodeStream(wiped)
            .Update(current => current with
            {
                Content = (current.ContentAs<CodeConfiguration>(options) ?? new CodeConfiguration())
                    with
                    {
                        LastExecutedAt = null,
                        LastExecutedBy = null,
                        LastActivityPath = null,
                        LastExecutedCodeHash = null,
                    },
            })
            .FirstAsync()
            .Timeout(Bound)
            .Await();

        var stampless = await ReadCell(wiped, c => c is
        {
            Code: Source, LastExecutedAt: null, LastActivityPath: null, LastExecutedCodeHash: null,
        });

        // The defect, asserted rather than assumed: from the cell alone, the run is gone.
        stampless.OutputCurrency().Should().Be(CodeOutputCurrency.NeverRun,
            "with no stamp the cell carries no evidence of its own run — this is the state a reader "
            + "sees after a reload, and it is indistinguishable from a cell nobody ever ran (#3301)");

        // The fix: the run is found by the edge the DISPATCHER wrote.
        var recovered = await stampless
            .ResolveOutputCurrency(wiped, viewerHome: null, meshService)
            .Timeout(Bound)
            .Await();
        recovered.Should().Be(CodeOutputCurrency.Unverified,
            "the Activity node created before the dispatch still names this cell on HubPath, so the "
            + "run is not lost — only the cell's pointer to it is. A run with nothing recording WHAT "
            + "it ran is Unverified, the same fail-closed verdict as a stamp that arrived without "
            + "its fingerprint");

        // Non-vacuity: a cell that genuinely never ran must still say so, or the lookup is a rubber
        // stamp that would report every unrun cell in the mesh as having run.
        var untouched = await CreateCell(new CodeConfiguration { Code = Source, IsExecutable = true });
        var untouchedCell = await ReadCell(untouched, c => c is { Code: Source });
        var untouchedVerdict = await untouchedCell
            .ResolveOutputCurrency(untouched, viewerHome: null, meshService)
            .Timeout(Bound)
            .Await();
        untouchedVerdict.Should().Be(CodeOutputCurrency.NeverRun,
            "no Activity anywhere names this cell — and the neighbour's run, which lives in the very "
            + "same _Activity namespace, must not be mistaken for it. A verdict that could not come "
            + "out NeverRun here would prove nothing above");

        // The stamped neighbour never reaches the lookup at all — the stamp answers first, which is
        // what keeps a notebook of normal cells at zero queries.
        var neighbourCell = await ReadCell(neighbour, c => c is { LastExecutedCodeHash: not null and not "" });
        var neighbourVerdict = await neighbourCell
            .ResolveOutputCurrency(neighbour, viewerHome: null, meshService)
            .Timeout(Bound)
            .Await();
        neighbourVerdict.Should().Be(CodeOutputCurrency.Current,
            "a cell whose stamp landed is judged by the stamp, unchanged and without a query");
    }

    // ── helpers ──

    private async Task Run(string path)
    {
        var dispatch = await RequestHub
            .Observe<ExecuteScriptResponse>(new ExecuteScriptRequest(), o => o.WithTarget(new Address(path)))
            .FirstAsync()
            .Timeout(Bound)
            .Await();
        dispatch.Message.Success.Should().BeTrue(
            $"the run of '{path}' must be accepted before anything about it can be asserted");
        // The stamp is what makes the run observable from the cell; waiting on it also guarantees
        // the Activity node exists before the lookup goes looking for it.
        await ReadCell(path, c => c is { LastExecutedCodeHash: not null and not "" });
    }

    private async Task<string> CreateCell(CodeConfiguration content)
    {
        var id = $"cell{Guid.NewGuid():N}"[..12];
        var path = $"{TestPartition}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = CodeNodeType.NodeType,
                Name = id,
                State = MeshNodeState.Active,
                Content = content,
            }))
            .FirstAsync()
            .Timeout(Bound)
            .Await();
        return path;
    }

    /// <summary>
    /// Reads the cell's content off the authoritative single-node stream, waiting on the CONDITION
    /// rather than on the clock. 🚨 <c>ContentAs</c>, never <c>is CodeConfiguration</c>: content
    /// that crossed a hub boundary can arrive as untyped JSON, and the cast would yield a silent
    /// null that reads exactly like "the node has no code".
    /// </summary>
    private async Task<CodeConfiguration> ReadCell(string path, Func<CodeConfiguration?, bool> until)
    {
        var options = Mesh.JsonSerializerOptions;
        return (await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<CodeConfiguration>(options))
            .Where(until)
            .FirstAsync()
            .Timeout(Bound)
            .Await())!;
    }
}
