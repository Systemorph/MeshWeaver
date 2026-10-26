using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Issue #7226 — a NodeType authored DEFINITION-FIRST, SOURCE-SECOND.</b>
///
/// <para><b>Observed.</b> A developer created a NodeType whose configuration lambda is
/// <c>config =&gt; config.WithContentType&lt;CoreInfo&gt;()</c> in their own dev space. The
/// first-build kickoff compiled it seven seconds after creation, its default source queries
/// (<c>Source</c>, <c>Test</c>) matched nothing, and the type settled at <c>Error</c> with
/// <c>CS0246 'CoreInfo'</c>, the empty source fold (<c>src=0</c>) and the MISSING SOURCES
/// diagnosis. No <c>Code</c> node existed under that subtree when the incident was read.</para>
///
/// <para><b>What this pins.</b> That the first compile's verdict over an established-but-empty
/// source set is a <i>provisional</i> one: the moment the declared source lands it is re-formed
/// with no Compile request from anyone — by the sources watcher's parked auto-retry in the process
/// that parked it, and by the owner-side failed-verdict re-drive (<c>HasStaleFailureVerdict</c>,
/// keyed on <c>FailedBuildInputs</c>) anywhere else. Either path alone passes the first case; with
/// both disabled it times out (measured by mutation when this was written) — and that a type
/// whose source is written in the same breath as its definition reaches <c>Ok</c> whichever
/// side of the first compile the source lands on (the in-flight half is #4280's
/// <c>SourcesMovedSince</c> re-drive at the terminal stamp). The negative control is the
/// incident itself: with no source ever written the type stays at <c>Error</c> and says WHY
/// (the declared <c>Source</c> query matched nothing), never a bare CS0246.</para>
///
/// <para>Each case uses its own partition, so no case can be satisfied by another's source.</para>
/// </summary>
public class ANodeTypeAuthoredBeforeItsSourcesConvergesTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private IMeshService MeshSvc => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string NewPartition() => "Ft7226" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The definition exactly as the incident's: no <c>sources</c> list (the defaults
    /// apply) and a configuration that names a content type declared in a source node.</summary>
    private static MeshNode TypeNode(string typePath, string contentType) =>
        MeshNode.FromPath(typePath) with
        {
            Name = typePath[(typePath.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new NodeTypeDefinition
            {
                Description = "authored before its sources (#7226)",
                Configuration = $"config => config.WithContentType<{contentType}>()",
            },
        };

    private static MeshNode SourceNode(string typePath, string contentType) =>
        new(contentType, $"{typePath}/{CodeConventions.SourceSubNamespace}")
        {
            NodeType = CodeConventions.CodeNodeType,
            Name = contentType,
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = $$"""
                    /// <summary>The content type the configuration lambda names.</summary>
                    public record {{contentType}}
                    {
                        /// <summary>A field, so the record is not empty.</summary>
                        public string? Title { get; init; }
                    }
                    """,
            },
        };

    private IObservable<NodeTypeDefinition> Definitions(string typePath) =>
        Mesh.GetMeshNodeStream(typePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null)
            .Select(d => d!);

    private Task<NodeTypeDefinition> Settled(string typePath, Func<NodeTypeDefinition, bool> at, CancellationToken ct) =>
        Definitions(typePath).Where(at).FirstAsync().Timeout(300.Seconds()).Await(ct);

    /// <summary>
    /// 🚨 THE INCIDENT, then the author's next step. The definition is created alone and its first
    /// compile is allowed to settle — deterministically at <c>Error</c>, because the declared
    /// source does not exist yet. Then the source is written, and NOTHING else: no Compile button,
    /// no release request, no recycle. The type must reach <c>Ok</c> on its own.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task DefinitionFirst_ThenItsSource_ReachesOkWithoutACompileRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = $"{NewPartition()}/Module/Core";
        const string contentType = "Ft7226CoreInfo";

        await MeshSvc.CreateNode(TypeNode(typePath, contentType))
            .FirstAsync().Timeout(120.Seconds()).Await(ct);

        var first = await Settled(typePath,
            d => d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error, ct);
        Output.WriteLine($"first verdict: {first.CompilationStatus}\n{first.CompilationError}");
        first.CompilationStatus.Should().Be(CompilationStatus.Error,
            "the content type is declared in a source node nobody has written yet");
        first.CurrentSourceVersions.Should().NotBeNull(
            "the first compile ran over an ESTABLISHED source set — an unestablished one is refused "
            + "as Unavailable, never compiled");
        first.CurrentSourceVersions!.Should().BeEmpty("no source exists yet");

        await MeshSvc.CreateNode(SourceNode(typePath, contentType))
            .FirstAsync().Timeout(120.Seconds()).Await(ct);

        var healed = await Settled(typePath, d => d.CompilationStatus is CompilationStatus.Ok, ct);
        Output.WriteLine($"after the source landed: {healed.CompilationStatus}, sources: "
            + string.Join(", ", healed.CurrentSourceVersions?.Keys ?? []));
        healed.CurrentSourceVersions!.Keys.Should().Contain(
            $"{typePath}/{CodeConventions.SourceSubNamespace}/{contentType}",
            "the build that reached Ok is the one over the source that just landed");
        healed.RequestedReleaseAt.Should().BeNull(
            "nobody pressed Compile — the re-drive is owner-side, keyed on the source set moving "
            + "past the one the failure was formed under");
        healed.FailedBuildInputs.Should().BeNull("a success clears the failure verdict");
    }

    /// <summary>
    /// The authoring tool's shape: the definition and its source written back to back, with the
    /// first compile free to start on either side of the source write. Whichever side it lands
    /// on, the type must converge to <c>Ok</c>.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task DefinitionAndSourceWrittenTogether_ReachOk()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = $"{NewPartition()}/Module/Core";
        const string contentType = "Ft7226TogetherInfo";

        await MeshSvc.CreateNode(TypeNode(typePath, contentType))
            .SelectMany(_ => MeshSvc.CreateNode(SourceNode(typePath, contentType)))
            .FirstAsync().Timeout(120.Seconds()).Await(ct);

        var ok = await Settled(typePath, d => d.CompilationStatus is CompilationStatus.Ok, ct);
        ok.CurrentSourceVersions!.Keys.Should().Contain(
            $"{typePath}/{CodeConventions.SourceSubNamespace}/{contentType}");
        ok.RequestedReleaseAt.Should().BeNull("no compile was requested by hand");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — the incident with no next step. Without the source the type must NOT
    /// read Ok (which would mean the two cases above prove nothing about the source arriving), and
    /// its Error must name the cause: the declared <c>Source</c> query matched nothing.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task DefinitionWithoutItsSource_SettlesAtErrorNamingTheMissingSourceQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = $"{NewPartition()}/Module/Core";

        await MeshSvc.CreateNode(TypeNode(typePath, "Ft7226NeverWritten"))
            .FirstAsync().Timeout(120.Seconds()).Await(ct);

        var settled = await Settled(typePath,
            d => d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error, ct);
        Output.WriteLine($"{settled.CompilationStatus}\n{settled.CompilationError}");

        settled.CompilationStatus.Should().Be(CompilationStatus.Error);
        settled.CompilationError.Should().Contain("CS0246",
            "the configuration names a type nothing declares — the incident's exact failure");
        settled.CurrentSourceVersions.Should().NotBeNull("an established set, not an unestablished one");
        settled.CurrentSourceVersions!.Should().BeEmpty("the incident's empty fold, src=0");
        settled.FailedSourceQueries.Should().NotBeNull("the coverage was measured")
            .And.Contain(q => q.Contains(CodeConventions.SourceSubNamespace),
                "the operator-visible record must say the declared Source query matched nothing — "
                + "the diagnosis that separates 'author has not written it yet' from broken code");
        settled.CompilationError.Should().Contain("matched NO nodes");
    }
}
