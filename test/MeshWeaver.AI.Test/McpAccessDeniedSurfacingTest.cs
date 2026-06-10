using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Like <see cref="McpFailureSurfacingTest"/>, but with RLS actually enforced
/// (<see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — NO <c>PublicAdminAccess</c>) so a
/// permission denial is a real one. Pins that an RLS rejection surfaces as an MCP
/// <c>"Error…"</c> string rather than a silent success or a swallowed exception.
/// </summary>
public class McpAccessDeniedSurfacingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Real RLS: no global PublicAdminAccess grant, so an ungranted user is denied.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    [Fact(Timeout = 60000)]
    public async Task Create_WithoutPermission_ReturnsError()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var noAccess = new AccessContext { ObjectId = "no-access-user", Name = "No Access" };
        access.SetContext(noAccess);
        access.SetCircuitContext(noAccess);
        try
        {
            // Nested path (namespace "Restricted") so this isolates the RLS denial from the
            // top-level partition rule. The ungranted user has no Create permission here.
            var node = new MeshNode("Denied" + Guid.NewGuid().ToString("N")[..8], "Restricted")
            {
                Name = "Denied Doc",
                NodeType = "Markdown",
                Content = new MarkdownContent { Content = "# nope" }
            };
            var json = JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions);

            var result = await new MeshOperations(Mesh).Create(json)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

            result.Should().StartWith("Error",
                "an RLS denial must surface to the MCP caller as an error, not a silent success");
        }
        finally
        {
            access.SetCircuitContext(null);
        }
    }
}
