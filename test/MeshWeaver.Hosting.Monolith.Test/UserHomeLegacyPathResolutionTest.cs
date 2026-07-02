using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the user-home path resolution. Post-v10 each user OWNS a ROOT partition at
/// <c>{id}</c> (the canonical home URL is <c>/{id}</c>); the built-in <c>User</c>
/// NodeType node still lives at path <c>"User"</c>. The portal still navigates the
/// home to the LEGACY <c>/User/{id}</c> shape, which prefix-matched the bare <c>User</c>
/// catalog node — resolving to <c>Prefix="User", Remainder="{id}"</c>, i.e.
/// hub=<c>User</c> / area=<c>{id}</c>. There is no renderer for an area literally named
/// after the user, so the page showed the LayoutDefinition placeholder
/// <c>"No renderer is registered for area '{id}' on hub 'User'"</c>.
///
/// <para>The fix (<c>PathResolutionService.RewriteLegacyUserHome</c>) strips the
/// legacy <c>User/</c> prefix and re-resolves against the user's own partition, so
/// <c>/User/{id}</c> resolves the SAME as the canonical <c>/{id}</c> — the user root
/// node, whose default area (<c>Activity</c>) renders the home.</para>
///
/// <para><b>Scope: NAVIGATION ONLY.</b> The rewrite lives on
/// <see cref="IPathResolver.ResolveNavigationPath"/> (what the GUI URL→area path calls),
/// NOT on the shared <see cref="IPathResolver.ResolvePath"/> that message routing and
/// node reads use — so a read/route of <c>User/{id}</c> stays literal (see
/// <see cref="SharedResolvePath_DoesNotRewriteLegacyUserHome_PreservesReadRouteInvariant"/>).
/// These tests therefore assert on the NAVIGATION resolution.</para>
/// </summary>
public class UserHomeLegacyPathResolutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private IPathResolver Resolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

    /// <summary>Seeds a post-v10 user: a <c>NodeType=User</c> row at the ROOT partition path <c>{id}</c>.</summary>
    private async Task SeedUserRoot(string id)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(MeshNode.FromPath(id) with
            {
                Name = "Roland",
                NodeType = "User",
                State = MeshNodeState.Active,
                Content = new User { Email = "roland@test.com" },
            }).Should().Within(30.Seconds()).Emit();
        }
    }

    /// <summary>
    /// THE bug: <c>/User/{id}</c> must resolve to the user's ROOT partition
    /// (Prefix="{id}", Remainder=null) — NOT to hub=<c>User</c> / area=<c>{id}</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task LegacyUserPrefix_ResolvesToUserRootPartition_NotAreaOnUserHub()
    {
        const string id = "roland_legacy_home";
        await SeedUserRoot(id);

        var resolution = await Resolver.ResolveNavigationPath($"User/{id}").Should().Within(20.Seconds()).Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(id,
            "the legacy /User/{id} URL must resolve to the user's OWN root partition, not the User catalog node");
        resolution.Remainder.Should().BeNull(
            "there is no leftover area — the home is the resolved node's DEFAULT area, never an area literally named '{0}'", id);
        resolution.Node.Should().NotBeNull("the matched user node must ride back on AddressResolution.Node");
        resolution.Node!.NodeType.Should().Be("User");

        // The exact defect shape must never surface: hub 'User' + area '{id}'.
        resolution.Prefix.Should().NotBe("User");
    }

    /// <summary>The canonical <c>/{id}</c> form resolves the same way (baseline — must not regress).</summary>
    [Fact(Timeout = 30000)]
    public async Task CanonicalBareId_ResolvesToUserRootPartition()
    {
        const string id = "roland_bare_home";
        await SeedUserRoot(id);

        var resolution = await Resolver.ResolveNavigationPath(id).Should().Within(20.Seconds()).Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(id);
        resolution.Remainder.Should().BeNull();
        resolution.Node!.NodeType.Should().Be("User");
    }

    /// <summary>
    /// A legacy <c>/User/{id}/{area}</c> URL keeps the trailing area as the Remainder,
    /// resolved against the user root (so e.g. <c>/User/{id}/Activity</c> → hub=<c>{id}</c>,
    /// area=<c>Activity</c> — the same as the canonical <c>/{id}/Activity</c>).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task LegacyUserPrefix_WithTrailingArea_KeepsAreaOnUserRoot()
    {
        const string id = "roland_area_home";
        await SeedUserRoot(id);

        var resolution = await Resolver.ResolveNavigationPath($"User/{id}/{UserActivityLayoutAreas.ActivityArea}")
            .Should().Within(20.Seconds()).Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(id, "the /User/ prefix is stripped; the user root is the hub");
        resolution.Remainder.Should().Be(UserActivityLayoutAreas.ActivityArea,
            "the trailing segment is the AREA on the user root, not part of the hub address");
    }

    /// <summary>
    /// The bare <c>User</c> NodeType catalog node itself still resolves to <c>Prefix="User"</c>
    /// (no remainder) — the rewrite only fires when the SOLE match is that catalog node AND a
    /// remainder is present, so <c>/User</c> is untouched.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task BareUserNodeType_StillResolvesToCatalogNode()
    {
        var resolution = await Resolver.ResolveNavigationPath("User").Should().Within(20.Seconds()).Emit();

        resolution.Should().NotBeNull("the built-in User NodeType node lives at path 'User'");
        resolution!.Prefix.Should().Be("User");
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// 🚨 The regression guard: the legacy-home rewrite is NAVIGATION-ONLY. The shared
    /// <see cref="IPathResolver.ResolvePath"/> — which message routing
    /// (<c>RoutingServiceBase.RouteMessage</c>) and node reads (<c>GetMeshNodeStream</c>)
    /// go through — must NOT rewrite <c>User/{id}</c>. It resolves LITERALLY to the bare
    /// <c>User</c> catalog node with the id as the (non-empty) remainder → NotFound for a
    /// route. This is exactly what preserves the "no legacy User mirror" onboarding
    /// invariant (<c>UserOnboardingServiceTests.CreateUser_WritesPartitionRootOnly_NoUserMirror</c>):
    /// a read of <c>User/{id}</c> must find nothing, never get redirected to the user's
    /// root partition (which DOES exist).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SharedResolvePath_DoesNotRewriteLegacyUserHome_PreservesReadRouteInvariant()
    {
        const string id = "roland_route_literal";
        await SeedUserRoot(id);

        // The un-rewritten ResolvePath must see User/{id} as the bare User catalog node
        // (Prefix="User") plus a leftover remainder — NEVER the user's own root partition.
        var resolution = await Resolver.ResolvePath($"User/{id}").Should().Within(20.Seconds()).Emit();

        resolution.Should().NotBeNull("the built-in User NodeType node still prefix-matches 'User/…'");
        resolution!.Prefix.Should().Be("User",
            "the SHARED ResolvePath must NOT rewrite the legacy home — routing/reads see the literal resolution");
        resolution.Remainder.Should().Be(id,
            "the id is the leftover remainder off the bare User catalog node; a route with a non-empty remainder is NotFound");
        resolution.Prefix.Should().NotBe(id,
            "reads/routes of User/{id} must never resolve to the user's root partition (that would re-break the no-mirror invariant)");
    }

    /// <summary>
    /// End-to-end: after the legacy URL resolves to the user root, that hub's default home
    /// area renders REAL content — never the "No renderer is registered for area …" placeholder
    /// the buggy hub=<c>User</c>/area=<c>{id}</c> shape produced.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task LegacyUserPrefix_RendersHomeArea_NotAreaNotFoundPlaceholder()
    {
        const string id = "roland_render_home";
        await SeedUserRoot(id);

        var resolution = await Resolver.ResolveNavigationPath($"User/{id}").Should().Within(20.Seconds()).Emit();
        resolution!.Prefix.Should().Be(id);

        var client = GetClient();
        var userAddress = new Address(resolution.Prefix);
        await client.Observe(new PingRequest(), o => o.WithTarget(userAddress)).Should().Within(15.Seconds()).Emit();

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            userAddress, new LayoutAreaReference(UserActivityLayoutAreas.ActivityArea));

        var value = await stream.Should().Within(15.Seconds()).Emit();

        value.Should().NotBeNull("the user root's home area must render on the resolved hub");
        var rawText = value!.Value.GetRawText();
        rawText.Should().NotBeNullOrWhiteSpace("the home area must produce real content");
        rawText.Should().NotContain("No renderer is registered",
            "the fix must never route the home to a hub with no matching area (the area-not-found placeholder)");
    }
}
