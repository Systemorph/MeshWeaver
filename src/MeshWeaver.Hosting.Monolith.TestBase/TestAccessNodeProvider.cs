using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// Global, test-only <see cref="IStaticNodeProvider"/> that exposes
/// AccessAssignment / PartitionAccessPolicy / Role MeshNodes registered by
/// individual test classes. Used so security seeds live in the static
/// provider repo rather than being duplicated in each test's
/// <c>ConfigureMesh</c>.
///
/// <para>Usage from a test class:
/// <code>
/// protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
///     => ConfigureMeshBase(builder)
///         .ConfigureServices(services => {
///             TestAccessNodeProvider.Seed(
///                 AssignmentNodeFactory.UserRole("Alice", "Admin", "ACME"),
///                 AssignmentNodeFactory.UserRole("Bob",   "Viewer", "ACME/Project"));
///             return services;
///         });
/// </code>
/// </para>
///
/// <para>Tests that <em>mutate</em> security data at runtime must dispose the
/// affected hubs in their teardown via
/// <see cref="MonolithMeshTestBase.DisposeHubsAsync"/> so the next test sees
/// only the static seeds, not the mutated state.</para>
/// </summary>
public sealed class TestAccessNodeProvider : IStaticNodeProvider
{
    private static readonly ConcurrentBag<MeshNode> Nodes = new();

    /// <summary>Adds <paramref name="nodes"/> to the global static repo.</summary>
    public static void Seed(params MeshNode[] nodes)
    {
        foreach (var n in nodes) Nodes.Add(n);
    }

    /// <summary>Resets the static repo. Call from the fixture's
    /// <c>InitializeAsync</c> if test isolation requires a clean slate.</summary>
    public static void Clear() => Nodes.Clear();

    public IEnumerable<MeshNode> GetStaticNodes() => Nodes;
}
