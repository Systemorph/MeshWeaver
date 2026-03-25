using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Verifies that users cannot see other users' partition data
/// through the full monolith stack with PostgreSQL persistence and RLS.
/// </summary>
[Collection("PostgreSql")]
public class UserPartitionVisibilityTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(90.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(fixture.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph();

    protected override async Task SetupAccessRightsAsync()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync(TestUsers.Admin.ObjectId, "Admin", null, "system", TestTimeout);
    }

    [Fact(Timeout = 120000)]
    public async Task OtherUser_CannotSee_UserPartitionData()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Give each user Admin on their own partition
        await securityService.AddUserRoleAsync("Alice", "Admin", "User/Alice", "system", TestTimeout);
        await securityService.AddUserRoleAsync("Bob", "Admin", "User/Bob", "system", TestTimeout);

        // Create data in Alice's partition (admin context is active from SetupAccessRightsAsync)
        await NodeFactory.CreateNodeAsync(
            new MeshNode("Alice", "User") { Name = "Alice", NodeType = "User" }, TestTimeout);
        await NodeFactory.CreateNodeAsync(
            new MeshNode("SecretProject", "User/Alice") { Name = "Alice's Secret Project", NodeType = "Markdown" }, TestTimeout);

        // Create data in Bob's partition
        await NodeFactory.CreateNodeAsync(
            new MeshNode("Bob", "User") { Name = "Bob", NodeType = "User" }, TestTimeout);
        await NodeFactory.CreateNodeAsync(
            new MeshNode("MyWork", "User/Bob") { Name = "Bob's Work", NodeType = "Markdown" }, TestTimeout);

        // Bob queries Alice's partition descendants
        var bobSeesAlice = await MeshQuery
            .QueryAsync<MeshNode>(new MeshQueryRequest
            {
                Query = "path:User/Alice scope:descendants",
                UserId = "Bob"
            }, ct: TestTimeout)
            .ToListAsync();

        // Bob should NOT see Alice's partition content
        bobSeesAlice
            .Where(n => n.NodeType != "User") // User node itself may be public-read
            .Should().BeEmpty("Bob must not see content inside Alice's partition");

        // Alice queries her own partition descendants
        var aliceSeesOwn = await MeshQuery
            .QueryAsync<MeshNode>(new MeshQueryRequest
            {
                Query = "path:User/Alice scope:descendants",
                UserId = "Alice"
            }, ct: TestTimeout)
            .ToListAsync();

        // Alice SHOULD see her own data
        aliceSeesOwn
            .Should().Contain(n => n.Name == "Alice's Secret Project",
                "Alice must see her own partition data");

        // Bob queries his own partition descendants
        var bobSeesOwn = await MeshQuery
            .QueryAsync<MeshNode>(new MeshQueryRequest
            {
                Query = "path:User/Bob scope:descendants",
                UserId = "Bob"
            }, ct: TestTimeout)
            .ToListAsync();

        // Bob SHOULD see his own data
        bobSeesOwn
            .Should().Contain(n => n.Name == "Bob's Work",
                "Bob must see his own partition data");

        // Alice queries Bob's partition descendants
        var aliceSeesBob = await MeshQuery
            .QueryAsync<MeshNode>(new MeshQueryRequest
            {
                Query = "path:User/Bob scope:descendants",
                UserId = "Alice"
            }, ct: TestTimeout)
            .ToListAsync();

        // Alice should NOT see Bob's partition content
        aliceSeesBob
            .Where(n => n.NodeType != "User")
            .Should().BeEmpty("Alice must not see content inside Bob's partition");
    }
}
