using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Base class for AI integration tests.
/// Adds AddAI() on top of the standard MonolithMeshTestBase configuration.
/// </summary>
public abstract class AITestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();
}
