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
/// The execute-time interlock's SECOND enforcement site (Systemorph/MeshWeaver#2820):
/// <c>CellSurfaceAssemblyProvider</c>, the kernel cell-surface join.
///
/// <para><b>Why it needs its own regression.</b> This site is unreachable from the per-instance-hub
/// gate in <c>NodeTypeEnrichmentHelpers</c>: it never enriches and never builds a
/// <c>HubConfiguration</c> — it loads the assembly straight through
/// <c>NodeAssemblyLoadContext</c>, and from that moment every script submission in the session can
/// call the pack's functions by bare name, with full write access through the ordinary mesh APIs
/// available to scripts. It is the most directly ARMED surface in the platform.</para>
///
/// <para>🚨 <b>A controlled experiment, not one observation.</b> The first assertion proves the pack
/// really is on the cell surface with an honest build; the second differs from it in EXACTLY one
/// field of the NodeType node — <see cref="NodeTypeDefinition.BuildProvenance"/> — with the bytes,
/// the assembly coordinates and the compile status untouched. Asserting only the refusal would pass
/// just as well against a provider that had stopped joining anything at all, which is the failure
/// mode that would silently empty every kernel session's reference set.</para>
/// </summary>
public class CellSurfaceRefusedBuildTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/RefusedCellPack";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private ICellSurfaceAssemblyProvider Provider =>
        Mesh.ServiceProvider.GetRequiredService<ICellSurfaceAssemblyProvider>();

    /// <summary>Resolves the cell surface and reports whether this pack is in it, disposing every
    /// lease the resolution handed out (the kernel would tie them to a session; this test does
    /// not need them alive).</summary>
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

    [Fact(Timeout = 180_000)]
    public async Task ACellSurfacePackWhoseBuildIsProvenStale_IsNotJoined()
    {
        var typeNode = MeshNode.FromPath(TypePath) with
        {
            Name = "RefusedCellPack",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                CellSurface = true,
                Configuration = "config => config.WithContentType<RefusedCellPackContent>()"
            },
        };

        await MeshService.CreateNode(typeNode)
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration
                {
                    Language = "csharp",
                    Code = """
                        public record RefusedCellPackContent { public string Title { get; init; } = ""; }
                        public static class RefusedCellPackApi { public static int TheAnswer() => 7; }
                        """,
                },
            }))
            .Should().Within(60.Seconds()).Emit();

        var compiled = await Mesh.GetMeshNodeStream(TypePath)
            .Should().Within(120.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition
            {
                CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error
            });
        ((NodeTypeDefinition)compiled.Content!).CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the pack must compile; error: {((NodeTypeDefinition)compiled.Content!).CompilationError}");

        // THE CONTROL ARM — with an honest build the pack IS on the cell surface.
        (await CellSurfaceContainsThePack()).Should().BeTrue(
            "a compiled cellSurface pack joins every session's reference set — without this half "
            + "the refusal below would prove nothing");

        // Stage the refusal exactly as ApplyAdoptedSourceStamp records it: the bundle names the
        // sources it was built from and they are not this mesh's. Nothing else changes — same
        // bytes, same coordinates, same CompilationStatus.Ok.
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with
            {
                AdoptedSourceFingerprint = "bundlefingerprintA",
                CurrentSourceFingerprint = "livefingerprintB",
                BuildProvenance = BuildProvenance.AdoptionRefused,
            })
            .Should().Within(60.Seconds()).Emit();
        await Mesh.GetMeshNodeStream(TypePath).Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition
            {
                BuildProvenance: BuildProvenance.AdoptionRefused
            });

        (await CellSurfaceContainsThePack()).Should().BeFalse(
            "a build PROVEN to come from other source must not be joined into a kernel session — "
            + "the bytes are still perfectly loadable, and that is exactly the point: the ONLY "
            + "thing that changed is the verdict about where they came from");
    }
}
