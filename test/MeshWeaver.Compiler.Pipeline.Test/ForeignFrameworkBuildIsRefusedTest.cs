using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🔴 <b>The outage shape, end to end on a real mesh — Systemorph/MeshWeaver#3472.</b>
///
/// <para>An assembly published under framework identity X, a process running identity Y: on
/// 2026-09-06 that pairing killed every deal page and every offer page of a client portal for two
/// and a half hours, while <c>compilationStatus</c> read <c>Ok</c> on both types throughout.</para>
///
/// <para><b>Why the cell surface is the site under test.</b> It is the most directly armed load
/// path in the platform — it loads straight through <c>NodeAssemblyLoadContext</c> with no
/// enrichment, takes a lifetime LEASE that keeps the assembly mapped for the whole session, and
/// from that moment every script submission can call the pack's functions by bare name. It is also
/// one of the six load paths that gated on <c>CompilationStatus == Ok</c> alone and never consulted
/// <c>NodeTypeCompilationHelpers.HasUsableBuild</c>, which is where the framework comparison lived.
/// <c>CellSurfaceRefusedBuildTest</c> is its twin on the orthogonal axis (bytes vs SOURCE, #2820);
/// this one is bytes vs FRAMEWORK.</para>
///
/// <para>🚨 <b>A controlled experiment, not one observation.</b> The pack is compiled FOR REAL by
/// this mesh and proved to be on the cell surface first; the refusal arm then differs from it in
/// EXACTLY one field of the NodeType node — <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>
/// — with the bytes, the assembly coordinates and <c>CompilationStatus.Ok</c> untouched. Asserting
/// only the refusal would pass just as well against a provider that had stopped joining anything at
/// all, which would silently empty every kernel session's reference set.</para>
/// </summary>
public class ForeignFrameworkBuildIsRefusedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/ForeignFrameworkPack";

    /// <summary>The identity the incident's records named. Any value that is not this process's own
    /// would do; using the real one keeps the test readable against the issue.</summary>
    private const string AnotherProcessesIdentity = "sc273ee39fdccbfc088f9aaf1fc548a9a";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private ICellSurfaceAssemblyProvider Provider =>
        Mesh.ServiceProvider.GetRequiredService<ICellSurfaceAssemblyProvider>();

    private async Task<bool> CellSurfaceContainsThePack()
    {
        var resolved = await Provider.ResolveCellSurfaceAssemblies()
            .Take(1)
            .Should().Within(60.Seconds()).Emit("resolution always emits — worst case an empty set");
        try
        {
            return resolved.Any(a =>
                string.Equals(a.NodeTypePath, TypePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var entry in resolved)
                entry.Lease.Dispose();
        }
    }

    private async Task<NodeTypeDefinition> ReadDefinition()
    {
        var node = await Mesh.GetMeshNodeStream(TypePath)
            .Take(1)
            .Should().Within(60.Seconds()).Emit("the NodeType node is readable");
        return node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }

    [Fact(Timeout = 180_000)]
    public async Task AnAssemblyBuiltForAnotherFrameworkIdentityIsNotServed_AndDoesNotReadOk()
    {
        await MeshService.CreateNode(MeshNode.FromPath(TypePath) with
            {
                Name = "ForeignFrameworkPack",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    CellSurface = true,
                    Configuration = "config => config.WithContentType<ForeignFrameworkPackContent>()"
                },
            })
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration
                {
                    Language = "csharp",
                    Code = """
                        public record ForeignFrameworkPackContent { public string Title { get; init; } = ""; }
                        public static class ForeignFrameworkPackApi { public static int TheAnswer() => 7; }
                        """,
                },
            }))
            .Should().Within(60.Seconds()).Emit();

        await Mesh.GetMeshNodeStream(TypePath)
            .Should().Within(120.Seconds())
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error });

        var honest = await ReadDefinition();
        honest.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the pack must compile; error: {honest.CompilationError}");

        // ── THE CONTROL ARM ─────────────────────────────────────────────────────────────────────
        // A record THIS mesh really produced, read by the process that produced it.
        NodeTypeBuildIdentity.RefusalReason(honest).Should().BeNull(
            "the gate must not refuse this process's own build — without this half every "
            + "assertion below would pass against a gate that refused everything, which would "
            + "take every dynamic NodeType in the mesh off its own bytes");
        NodeTypeBuildIdentity.ReportedStatus(honest).Should().Be(CompilationStatus.Ok,
            "and it must still report Ok for it");
        (await CellSurfaceContainsThePack()).Should().BeTrue(
            "a compiled cellSurface pack joins every session's reference set");

        // ── CRITERION 2, on data the pipeline really wrote, with nothing mutated ─────────────────
        // The SAME record, read by a process running another framework build — which is exactly
        // what the two surviving replicas were doing on 2026-09-06.
        NodeTypeBuildIdentity.ReportedStatus(honest, AnotherProcessesIdentity)
            .Should().Be(CompilationStatus.Foreign,
                "🚨 compilationStatus must not read Ok for a type whose hubs cannot activate. `Ok` "
                + "is a claim SCOPED to CompiledFrameworkVersion, and the record carries both "
                + "halves — the verdict and its scope — in two separate fields. Every instrument "
                + "read the first one, and read green for two and a half hours");
        honest.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "and the RECORD is not rewritten to say so. A reader-relative verdict written into a "
            + "shared record is how #3395's ping-pong was made: two replicas, two identities, each "
            + "correctly overwriting the other's answer forever. The record keeps saying what the "
            + "compiler did; only the report is scoped");

        // ── CRITERION 1, at a real load site ────────────────────────────────────────────────────
        // Stage the incident verbatim: ONE field moves. Same bytes on the shelf, same assembly
        // coordinates, same CompilationStatus.Ok.
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { CompiledFrameworkVersion = AnotherProcessesIdentity })
            .Should().Within(60.Seconds()).Emit();
        await Mesh.GetMeshNodeStream(TypePath).Should().Within(60.Seconds())
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                is { CompiledFrameworkVersion: AnotherProcessesIdentity });

        var onCellSurface = await CellSurfaceContainsThePack();

        // 🚨 Read the record back and require it to STILL be foreign before judging the resolution
        // above. The NodeType's own framework-stale kickoff is watching this field, so a healed
        // record would make the resolution a measurement of the wrong state — and a test that
        // cannot tell those apart is the flake it would later be blamed for. This is the guard,
        // not a retry: if the premise did not hold, the assertion says so instead of passing.
        var stillForeign = await ReadDefinition();
        stillForeign.CompiledFrameworkVersion.Should().Be(AnotherProcessesIdentity,
            "the premise of the assertion below: the record was foreign while the cell surface "
            + "was resolved. The framework-stale kickoff's store probe finds bytes under the LIVE "
            + "tag and answers Skip, which is exactly why this state is durable — and why the "
            + "record can sit at Ok with a foreign identity indefinitely");

        onCellSurface.Should().BeFalse(
            "🚨 THE REGRESSION. An assembly compiled for one framework identity must never be "
            + "served by a process running another: loading it fails as a TypeLoadException inside "
            + "a collectible ALC, which CompileResultFromAssembly records as an EMPTY configuration "
            + "list — and a hub resolves its configuration exactly once, so one refused load pins "
            + "'Area not found' for the grain's whole lifetime. The bytes here are still perfectly "
            + "loadable BY THEIR OWN PROCESS, and that is the point: the only thing that changed is "
            + "which framework identity the record names");

        NodeTypeBuildIdentity.ReportedStatus(stillForeign).Should().Be(CompilationStatus.Foreign,
            "and this process must not repeat the record's Ok while refusing to load what it names");
    }
}
