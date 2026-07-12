using System.Threading.Tasks;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Feedback is PRIVATE by construction: the <c>/feedback</c> skill files each entry under the
/// submitter's OWN partition (<c>{userId}/Feedback/{id}</c>). The self-scope-owner rule grants a user
/// Admin at the scope equal to their own id, so the submitter has full access to their own feedback
/// with NO explicit grant — and no other regular user has any access to it. (Platform admins read
/// across users only through the System-scoped review tab, not a data grant.)
/// </summary>
public class FeedbackPrivacyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder).AddRowLevelSecurity();

    // Granular permissions — skip the blanket PublicAdminAccess the base seeds.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 20000)]
    public async Task User_HasFullAccess_ToTheirOwnFeedback_WithoutAnyGrant()
    {
        // Self-scope: "alice" is Admin at scope "alice" → read + create on alice/Feedback/{id}.
        await Mesh.GetEffectivePermissions("alice/Feedback/f1", "alice")
            .Should().Match(p => (p & Permission.Read) == Permission.Read
                                 && (p & Permission.Create) == Permission.Create);
    }

    [Fact(Timeout = 20000)]
    public async Task OtherUsers_HaveNoAccess_ToSomeoneElsesFeedback()
    {
        // "bob" has no grant on alice's partition → cannot read alice's feedback. Privacy by construction.
        await Mesh.GetEffectivePermissions("alice/Feedback/f1", "bob")
            .Should().Match(p => p == Permission.None);
    }
}
