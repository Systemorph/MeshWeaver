using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Redaction of the PII email on world-readable <see cref="User"/> nodes (GitHub issue #471, RC1).
/// The subject and global admins see the real email; every other authenticated reader (and
/// anonymous) sees it redacted. The display name is never redacted, and the STORED node keeps the
/// real email so the System-identity email→userId login lookup keeps resolving.
/// </summary>
public class UserEmailPiiRedactionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string OwnerId = "Alice";
    private const string OwnerEmail = "alice@example.com";
    private const string AdminUserId = "AdminUser";

    private CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    // No PublicAdminAccess here — otherwise every user would resolve as a global admin and the
    // "non-admin → redacted" case could never be exercised. Grant exactly one global admin
    // (Admin role at the Admin scope) so IsGlobalAdmin is deterministic per identity.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(AssignmentNodeFactory.UserRole(AdminUserId, "Admin", "Admin"));

    private static MeshNode UserNode() => new(OwnerId)
    {
        Name = "Alice Smith",
        NodeType = "User",
        State = MeshNodeState.Active,
        Content = new User { FullName = "Alice Smith", Email = OwnerEmail, Role = "Developer", Bio = "Hi there" },
    };

    private User? ReadUser(MeshNode node) => node.ContentAs<User>(Mesh.JsonSerializerOptions);

    // ── (a) + (e): a non-owner non-admin reader gets the email redacted; the NAME is preserved. ──
    [Fact(Timeout = 30000)]
    public async Task NonOwnerNonAdmin_EmailRedacted_NameKept()
    {
        var bob = new AccessContext { ObjectId = "Bob", Name = "Bob" };
        var projected = await UserPiiRedaction.RedactEmailForReader(Mesh, UserNode(), bob)
            .FirstAsync().ToTask(Ct);

        ReadUser(projected)!.Email.Should().BeNull("a non-owner non-admin reader must not see the PII email");
        // Name / display identity is NOT redacted.
        projected.Name.Should().Be("Alice Smith");
        ReadUser(projected)!.FullName.Should().Be("Alice Smith");
        ReadUser(projected)!.Role.Should().Be("Developer");
    }

    // ── (b): the subject (owner) sees their own real email. ──
    [Fact(Timeout = 30000)]
    public async Task Owner_SeesOwnEmail()
    {
        var owner = new AccessContext { ObjectId = OwnerId, Name = OwnerId };
        var projected = await UserPiiRedaction.RedactEmailForReader(Mesh, UserNode(), owner)
            .FirstAsync().ToTask(Ct);

        ReadUser(projected)!.Email.Should().Be(OwnerEmail);
    }

    // ── (c): a global admin sees the real email. ──
    [Fact(Timeout = 30000)]
    public async Task GlobalAdmin_SeesEmail()
    {
        var admin = new AccessContext { ObjectId = AdminUserId, Name = AdminUserId };
        var projected = await UserPiiRedaction.RedactEmailForReader(Mesh, UserNode(), admin)
            .FirstAsync().ToTask(Ct);

        ReadUser(projected)!.Email.Should().Be(OwnerEmail);
    }

    // ── Anonymous readers never see it. ──
    [Fact(Timeout = 30000)]
    public async Task Anonymous_EmailRedacted()
    {
        var projected = await UserPiiRedaction.RedactEmailForReader(Mesh, UserNode(), viewer: null)
            .FirstAsync().ToTask(Ct);

        ReadUser(projected)!.Email.Should().BeNull();
    }

    // ── (d): redaction is a READ PROJECTION, never a mutation — the input/stored node is untouched,
    //         so the System-identity email→userId login lookup (which reads the stored value) still
    //         resolves. Non-User nodes pass through unchanged. ──
    [Fact(Timeout = 30000)]
    public async Task Redaction_DoesNotMutateStoredNode_And_PassesNonUserNodesThrough()
    {
        var stored = UserNode();
        var bob = new AccessContext { ObjectId = "Bob", Name = "Bob" };

        var projected = await UserPiiRedaction.RedactEmailForReader(Mesh, stored, bob)
            .FirstAsync().ToTask(Ct);

        // The projection returned a redacted COPY; the original (stored) node keeps the real email,
        // so the content.email login lookup under System identity is unaffected.
        ReadUser(projected)!.Email.Should().BeNull();
        ReadUser(stored)!.Email.Should().Be(OwnerEmail, "redaction must not mutate the stored node");

        // A non-User node is never touched (short-circuits on NodeType, returning the same instance).
        var markdown = new MeshNode("Page") { Name = "A Page", NodeType = "Markdown" };
        var passthrough = await UserPiiRedaction.RedactEmailForReader(Mesh, markdown, bob)
            .FirstAsync().ToTask(Ct);
        passthrough.Should().BeSameAs(markdown);
    }
}
