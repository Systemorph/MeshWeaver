using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Guards the fix for the errant-Admin-on-Auth bug. Attempting to create a node inside a
/// system-managed MIRROR partition (User / Auth) tripped the self-healing partition bootstrap, which
/// granted the caller Admin on the mirror partition (and emailed them) BEFORE the structural
/// write-guard rejected the actual write — leaving the grant behind, pinned as "the last admin of
/// Auth" and impossible to remove. The shared <see cref="WellKnownPartitions"/> predicate now gates
/// the bootstrap, the write guard, and the last-admin invariant so none of them treats a mirror
/// partition as a user Space.
/// </summary>
public class ReservedPartitionMirrorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Theory]
    [InlineData("Auth", true)]
    [InlineData("auth", true)]        // case-insensitive
    [InlineData("User", true)]
    [InlineData("MySpace", false)]
    [InlineData("rbuergi", false)]
    [InlineData("", false)]
    public void IsMirror_IdentifiesTheSystemManagedMirrorPartitions(string partition, bool expected)
        => Assert.Equal(expected, WellKnownPartitions.IsMirror(partition));

    [Fact]
    public async Task LastAdminInvariant_ExemptsMirrorPartitions_SoAnErrantAuthGrantIsRemovable()
    {
        var validator = new SpaceAdminInvariantValidator(Mesh);

        // The exact errant grant shape: an Admin AccessAssignment under Auth/_Access. Deleting it must
        // be ALLOWED — the mirror has no user admin to protect — so the cleanup can proceed. Without
        // the mirror exemption the invariant would block it as "the last administrator of Auth".
        var node = new MeshNode("rbuergi_Access", "Auth/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "rbuergi Access",
            Content = new AccessAssignment
            {
                AccessObject = "rbuergi",
                Roles = [new RoleAssignment { Role = "Admin" }],
            },
        };
        var ctx = new NodeValidationContext { Operation = NodeOperation.Delete, Node = node };

        var result = await validator.Validate(ctx).FirstAsync().ToTask();

        Assert.True(result.IsValid, "removing an errant admin grant on the Auth mirror must be allowed");
    }
}
