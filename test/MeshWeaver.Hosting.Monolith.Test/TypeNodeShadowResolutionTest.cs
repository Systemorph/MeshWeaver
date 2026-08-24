using System;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the type-node UN-SHADOWING on navigation resolution
/// (<c>PathResolutionService.RewriteTypeNodeShadow</c>). A module's NodeType definition node may
/// legitimately live at <c>{parent}/{Name}</c> while the PARENT node renders an AREA of the same
/// name — the live case is the Store: its catalog type node sits at <c>Store/Catalog</c>, and the
/// Store node's <c>Catalog</c> area renders at <c>/Store/Catalog[/…]</c>. The longer prefix match
/// won, so <c>/Store/Catalog/Catalog?category=…</c> resolved onto the TYPE node's hub with
/// remainder <c>Catalog?…</c>, where the framework's type-catalog area answered
/// "Collection ?category=… is not mapped in Address Store/Catalog" — reproduced on PROD Blazor and
/// the RN client alike (they share this resolver via <c>ResolveNavigationPath</c> /
/// <c>POST /api/mesh/resolve</c>).
/// </summary>
public class TypeNodeShadowResolutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IPathResolver Resolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

    /// <summary>A parent content node plus a NodeType DEFINITION node named like the parent's area.</summary>
    private async Task SeedShadowPair(string parent, string typeName)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(MeshNode.FromPath(parent) with
            {
                Name = parent,
                NodeType = "Group",
                State = MeshNodeState.Active,
            }).Should().Within(30.Seconds()).Emit();
            await meshService.CreateNode(MeshNode.FromPath($"{parent}/{typeName}") with
            {
                Name = typeName,
                NodeType = "NodeType",
                State = MeshNodeState.Active,
            }).Should().Within(30.Seconds()).Emit();
        }
    }

    /// <summary>
    /// THE bug: an area URL under the parent must resolve to the PARENT (which owns the area),
    /// not to the same-named type node with the area's id as a bogus remainder.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AreaUrlShadowedByTypeNode_ResolvesToParentWithFullRemainder()
    {
        await SeedShadowPair("shadowstore", "Catalog");

        var resolution = await Resolver.ResolveNavigationPath("shadowstore/Catalog/Catalog")
            .Should().Within(20.Seconds()).Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("shadowstore",
            "the parent owns the Catalog AREA; the same-named type node must not steal the prefix");
        resolution.Remainder.Should().Be("Catalog/Catalog",
            "the full remainder (area + id) goes back to the parent's hub");
    }

    /// <summary>Navigating exactly TO the definition node (no remainder) still resolves to it.</summary>
    [Fact(Timeout = 30000)]
    public async Task TypeNodeItself_StillResolvesDirectly()
    {
        await SeedShadowPair("shadowstore2", "Catalog");

        var resolution = await Resolver.ResolveNavigationPath("shadowstore2/Catalog")
            .Should().Within(20.Seconds()).Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("shadowstore2/Catalog",
            "an exact navigation to the type node is not a shadowing case — its own pages keep working");
        resolution.Remainder.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// The shared read/route resolution stays LITERAL (same scope rule as the legacy-User rewrite):
    /// only navigation un-shadows.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SharedResolvePath_DoesNotUnshadow_PreservesReadRouteInvariant()
    {
        await SeedShadowPair("shadowstore3", "Catalog");

        var resolution = await Resolver.ResolvePath("shadowstore3/Catalog/Anything")
            .Should().Within(20.Seconds()).Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("shadowstore3/Catalog",
            "routing and reads must see the unmodified longest-prefix resolution");
        resolution.Remainder.Should().Be("Anything");
    }
}
