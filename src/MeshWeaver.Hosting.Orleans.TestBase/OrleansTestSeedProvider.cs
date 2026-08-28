using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The cluster's AI-FREE seeds: the test user and its access grant, contributed as an
/// <see cref="IStaticNodeProvider"/> rather than via <c>builder.AddMeshNodes(...)</c> so they are an
/// immutable activation fallback rather than an initial snapshot tests could mutate or rewrite
/// through persistence.
///
/// <para>🚨 This used to also seed a default <c>Agent</c> node and a pre-populated chat thread, and
/// that is why the whole Orleans test rig lived in an AI-named assembly: ~51 tests that never touch
/// an agent still inherited from it. The AI seeds now come from a SECOND provider contributed by
/// MeshWeaver.Plugins (<c>IStaticNodeProvider</c> is enumerable, so both are read), which is what
/// let the engine leave this repo (#2276). Keep this one free of AI types — a single
/// <c>using MeshWeaver.AI</c> here puts the dependency back on every Orleans test.</para>
/// </summary>
public sealed class OrleansTestSeedProvider : IStaticNodeProvider
{
    /// <inheritdoc />
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // TestUser user node — owner of the per-user partition. Post-v10 the user node lives at
        // the ROOT namespace (path={userId}); the legacy "User/" wrapper has been retired.
        yield return new MeshNode("TestUser") { Name = "TestUser", NodeType = "User" };

        // TestUser Admin access — namespace="TestUser/_Access" so the
        // SecurityService.ComputeScopeRoles pattern (".../{scope}/_Access") resolves to
        // scope="TestUser". The Admin grant covers the user's own partition; matches the post-v10
        // root-level partition layout.
        yield return new MeshNode("TestUser_Access", "TestUser/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "TestUser Access",
            MainNode = "TestUser",
            Content = new AccessAssignment
            {
                AccessObject = "TestUser",
                DisplayName = "Test User",
                Roles = [new RoleAssignment { Role = "Admin" }]
            }
        };
    }
}
