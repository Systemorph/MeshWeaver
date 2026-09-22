using System.Reactive.Linq;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A permission fold can emit off the HTTP/circuit flow. The authoritative SEO read must still
/// carry the viewer who began resolution, rather than dropping the body at the posting guard.
/// </summary>
public class SeoOwnerReadContextTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .ConfigureHub(configuration =>
        {
            var evaluate = configuration.Get<EffectivePermissionsDelegate>()
                ?? throw new InvalidOperationException("The fixture requires real row-level security.");
            return configuration.WithPermissionEvaluator((hub, path, userId) =>
                evaluate(hub, path, userId).Do(_ =>
                {
                    if (userId != WellKnownUsers.Anonymous)
                        return;
                    // Model the scheduler boundary AFTER the real permission fold has decided.
                    // No verdict is replaced. A production server has no test-host fallback.
                    var access = hub.ServiceProvider.GetRequiredService<AccessService>();
                    access.SetContext(null);
                    access.SetCircuitContext(null);
                }));
        })
        .AddMeshNodes(
            new MeshNode("SeoContext") { NodeType = "Space" },
            AssignmentNodeFactory.Policy("SeoContext", new PartitionAccessPolicy { PublicRead = true }),
            new MeshNode("Page", "SeoContext")
            {
                NodeType = "Markdown", Name = "Current title",
                Content = new MarkdownContent { Content = "Current **body**" },
                PreRenderedHtml = "Stale body",
            },
            new MeshNode("PrivateSeoContext") { NodeType = "Space" });

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(WellKnownUsers.Anonymous, false)]
    [InlineData("seo-viewer", false)]
    [InlineData("seo-viewer", true)]
    public async Task CurrentBodySurvivesTheGateEmissionWithoutAnAmbientIdentity(string userId, bool circuitOnly)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.ClearHostIdentity();
        var viewer = new AccessContext { ObjectId = userId, Name = userId };
        access.SetContext(circuitOnly ? null : viewer);
        access.SetCircuitContext(circuitOnly ? viewer : null);
        try
        {
            var result = await SeoResolver.Resolve(Mesh, "SeoContext/Page").Should().Emit();

            Assert.NotNull(result);
            Assert.Equal("Current title", result.Node.Name);
            Assert.Equal("<p>Current <strong>body</strong></p>\n", result.Body);
        }
        finally
        {
            access.SetContext(TestUsers.Admin);
            access.SetHostIdentity(TestUsers.Admin);
        }
    }

    [Fact]
    public async Task ASignedInViewerStillCannotPublishAPrivatePage()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.ClearHostIdentity();
        access.SetContext(TestUsers.Admin);
        try
        {
            Assert.Null(await SeoResolver.Resolve(Mesh, "PrivateSeoContext").Should().Emit());
        }
        finally
        {
            access.SetContext(TestUsers.Admin);
            access.SetHostIdentity(TestUsers.Admin);
        }
    }
}
