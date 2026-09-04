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

    // ── helpers ──

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
