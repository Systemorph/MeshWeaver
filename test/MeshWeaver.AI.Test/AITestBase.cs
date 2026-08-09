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

    /// <summary>Per-class partition, so concurrently-running AI.Test classes do not contend on one
    /// partition hub — the root cause behind the parallel failures in #1040 (a partition has ONE
    /// owning hub that serialises every write routed through it). Overriding here covers all
    /// AI.Test classes in one place; other projects keep the "TestData" default and are unaffected.</summary>
    public override string TestPartition => $"TestData{GetType().Name}";
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();
}
