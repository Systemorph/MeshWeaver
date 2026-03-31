using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests GetEffectivePermissionsAsync with PostgreSQL persistence.
/// Protocol:
/// 1) Install OrganizationNodeType
/// 2) Create Organization "Systemorph"
/// 3) Ask ISecurityService.HasPermission → should NOT return None
/// </summary>
[Collection("PostgreSql")]
public class EffectivePermissionPostgresTest(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(90.Seconds()).Token;

    /// <summary>
    /// Wire up PostgreSQL partitioned persistence instead of in-memory.
    /// No PublicAdminAccess — permissions must come from persisted AccessAssignment nodes.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(fixture.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            .AddOrganizationType();

    protected override async Task SetupAccessRightsAsync()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync(TestUsers.Admin.ObjectId, "Admin", null, "system", TestTimeout);
    }

    [Fact(Timeout = 120000)]
    public async Task CreateOrganization_HasPermission_ReturnsAdmin()
    {
        // 1) OrganizationNodeType is installed via ConfigureMesh above

        // 2) Create Organization "Systemorph"
        var orgNode = MeshNode.FromPath("Systemorph") with
        {
            Name = "Systemorph",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Systemorph" }
        };
        await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // 3) Ask ISecurityService.HasPermission for this node
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var permissions = await securityService.GetEffectivePermissionsAsync(
            "Systemorph", TestUsers.Admin.ObjectId, TestTimeout);

        // The post-creation handler granted Admin to Roland via persisted AccessAssignment.
        // Without fix: returns Permission.None (GetChildrenAsync filters satellite nodes)
        // With fix: returns Permission.All (GetAllChildrenAsync includes satellites)
        permissions.Should().NotBe(Permission.None,
            "Creator should have permissions from persisted AccessAssignment on the Organization");
        permissions.Should().Be(Permission.All,
            "Admin role grants all permissions");
    }
}
