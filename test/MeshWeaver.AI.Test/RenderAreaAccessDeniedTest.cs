using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// Like <see cref="McpAccessDeniedSurfacingTest"/>, but for <see cref="MeshOperations.RenderArea"/>
/// (the core behind <c>POST /api/mesh/render-area</c>). RLS is real here
/// (<see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — NO <c>PublicAdminAccess</c>), so the
/// layout-area subscribe runs the AccessControlPipeline gate: an ungranted user's
/// <c>SubscribeRequest</c> is denied and the denial must surface to the caller as the
/// <c>"Error: …"</c> sentinel — never as a rendered frame (content leak) and never as a hang.
/// </summary>
public class RenderAreaAccessDeniedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Real RLS: no global PublicAdminAccess grant, so an ungranted user is denied.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            // Statically seeded fixture node (no runtime create needed under real RLS).
            .AddMeshNodes(new MeshNode("SecuredRenderNode")
            {
                Name = "Secured Render Node",
                NodeType = "Markdown",
                Content = new MarkdownContent { Content = "# secret" }
            });

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    [Fact(Timeout = 60000)]
    public async Task RenderArea_WithoutPermission_SurfacesDenialNotContent()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var noAccess = new AccessContext { ObjectId = "no-access-user", Name = "No Access" };
        access.SetContext(noAccess);
        access.SetCircuitContext(noAccess);
        try
        {
            var result = await new MeshOperations(Mesh)
                .RenderArea("@SecuredRenderNode", "Overview", timeoutSeconds: 30)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

            result.Should().StartWith("Error",
                "an RLS denial on the layout-area subscribe must surface to the caller as an error, " +
                "not a silent success or a hang");
            result.Should().NotContain("\"areas\"",
                "a denied caller must NEVER receive the rendered frame");
            result.Should().NotContain("secret",
                "a denied caller must NEVER see the node's content");
        }
        finally
        {
            access.SetCircuitContext(null);
        }
    }
}
