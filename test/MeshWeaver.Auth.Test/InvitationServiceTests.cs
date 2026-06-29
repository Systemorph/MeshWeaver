using System;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Email;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Pins the invitation-only onboarding building blocks: <see cref="InvitationService"/> writes a
/// globally-queryable Invitation node in the Admin partition, finds the Pending invitation by
/// email (the verified-email allowlist the onboarding gate consumes), and flips status on
/// accept / revoke. Real mesh, in-memory partitioned persistence, no mocks.
/// </summary>
public class InvitationServiceTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services =>
            {
                services.AddScoped<InvitationService>();
                return services;
            });

    private InvitationService Service =>
        Mesh.ServiceProvider.GetRequiredService<InvitationService>();

    private AccessService Access =>
        Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private IMeshService MeshSvc =>
        Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// CreateInvitation writes a Pending Invitation node to the Admin partition, queryable
    /// globally by <c>nodeType:Invitation content.email:X</c> — the exact lookup the onboarding
    /// gate runs.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateInvitation_WritesQueryableAdminNode()
    {
        var email = $"invitee-{Guid.NewGuid():N}@example.com".ToLowerInvariant();

        MeshNode created;
        using (Access.ImpersonateAsSystem())
            created = await Service.CreateInvitation(email, invitedBy: "admin", note: "new teammate").Should().Emit();

        created.Should().NotBeNull();
        created.NodeType.Should().Be("Invitation");
        created.Path.Should().StartWith("Admin/Invitation/");
        InvitationService.TryGetInvitation(created, Mesh.JsonSerializerOptions)!
            .Status.Should().Be(InvitationStatus.Pending);

        // Same shape as the onboarding gate's lookup — routes nodeType:Invitation to Admin.
        var found = await MeshSvc
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"nodeType:Invitation content.email:{email} limit:1"))
            .Should().Within(10.Seconds())
            .Match(c => c.Items.Count > 0);
        found.Items.Should().ContainSingle("the onboarding gate must find the invitation by email");
        found.Items[0].Path.Should().Be(created.Path);
    }

    /// <summary>FindPendingInvitation returns the node for a Pending invite, null when none exists.</summary>
    [Fact(Timeout = 30000)]
    public async Task FindPendingInvitation_ReturnsPending_NullWhenAbsent()
    {
        var email = $"invitee-{Guid.NewGuid():N}@example.com".ToLowerInvariant();
        var workspace = Mesh.GetWorkspace();

        // Absent → null.
        (await Service.FindPendingInvitation(workspace, email).Should().Emit()).Should().BeNull();

        using (Access.ImpersonateAsSystem())
            await Service.CreateInvitation(email, "admin", null).Should().Emit();

        // Wait for visibility, then the pending invitation is found.
        await MeshSvc.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"nodeType:Invitation content.email:{email} limit:1"))
            .Should().Within(10.Seconds()).Match(c => c.Items.Count > 0);

        var pending = await Service.FindPendingInvitation(workspace, email).Should().Emit();
        pending.Should().NotBeNull();
        pending!.NodeType.Should().Be("Invitation");
    }

    /// <summary>Revoke flips the invitation to Revoked, so it no longer counts as Pending.</summary>
    [Fact(Timeout = 30000)]
    public async Task Revoke_FlipsStatus_NoLongerPending()
    {
        var email = $"invitee-{Guid.NewGuid():N}@example.com".ToLowerInvariant();

        MeshNode created;
        using (Access.ImpersonateAsSystem())
            created = await Service.CreateInvitation(email, "admin", null).Should().Emit();

        var inv = InvitationService.TryGetInvitation(created, Mesh.JsonSerializerOptions)!;
        using (Access.ImpersonateAsSystem())
            await Service.Revoke(created, inv).Should().Emit();

        var reread = await ReadNode(created.Path).Should().Emit();
        reread.Should().NotBeNull();
        InvitationService.TryGetInvitation(reread!, Mesh.JsonSerializerOptions)!
            .Status.Should().Be(InvitationStatus.Revoked);
    }

    /// <summary>MarkAccepted flips the invitation to Accepted and stamps AcceptedAt.</summary>
    [Fact(Timeout = 30000)]
    public async Task MarkAccepted_FlipsStatus_StampsAcceptedAt()
    {
        var email = $"invitee-{Guid.NewGuid():N}@example.com".ToLowerInvariant();

        MeshNode created;
        using (Access.ImpersonateAsSystem())
            created = await Service.CreateInvitation(email, "admin", null).Should().Emit();

        var inv = InvitationService.TryGetInvitation(created, Mesh.JsonSerializerOptions)!;
        using (Access.ImpersonateAsSystem())
            await Service.MarkAccepted(created, inv).Should().Emit();

        var reread = await ReadNode(created.Path).Should().Emit();
        var stored = InvitationService.TryGetInvitation(reread!, Mesh.JsonSerializerOptions)!;
        stored.Status.Should().Be(InvitationStatus.Accepted);
        stored.AcceptedAt.Should().NotBeNull();
    }

    /// <summary>The NoOp sender (Email:Enabled=false) reports success without sending.</summary>
    [Fact(Timeout = 10000)]
    public async Task NoOpEmailSender_ReturnsTrue_WithoutSending()
    {
        IEmailSender sender = new NoOpEmailSender();
        (await sender.SendEmail("x@example.com", "hi", "<p>body</p>").Should().Emit()).Should().BeTrue();
    }
}
