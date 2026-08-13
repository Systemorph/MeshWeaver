using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The DI half of issue #890's fix: the Roslyn reference set every dynamic-NodeType compile
/// binds against is resolved from the MESH's container — one per mesh, and the very instance the
/// compiler holds. <see cref="CompilationReferenceSetIsolationTest"/> pins that two sets share
/// no <c>MetadataReference</c> and that nothing static roots one; this pins that a real mesh
/// actually routes its compiler to its own set rather than to a process-wide field.
/// </summary>
public class CompilationReferenceSetWiringTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public void Mesh_OwnsExactlyOneReferenceSet_AndTheCompilerBindsIt()
    {
        var fromMesh = Mesh.ServiceProvider.GetRequiredService<CompilationReferenceSet>();

        // A transient would re-materialize ~15 MiB of PE metadata on every resolve.
        Assert.Same(fromMesh, Mesh.ServiceProvider.GetRequiredService<CompilationReferenceSet>());

        // The compiler must bind THIS mesh's references; binding anything else is the
        // process-wide state issue #890's canary implicated.
        Assert.Same(
            fromMesh,
            Mesh.ServiceProvider.GetRequiredService<MeshNodeCompilationService>().ReferenceSet);

        // An empty set would make every NodeType compile fail to bind, so this also guards the
        // lazy materialization actually producing the TPA list.
        Assert.NotEmpty(fromMesh.References);
    }
}
