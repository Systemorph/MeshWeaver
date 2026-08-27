#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The "a newer build of this type is available — recycle to pick it up" banner
/// (<see cref="StaleBuildBanner"/>), end to end through a real mesh: an instance whose NodeType has
/// published a build different from the one the instance bound must render the banner in the
/// <see cref="LayoutAreaSlots.StaleBuildBanner"/> sidecar slot — ABOVE its own content, never
/// instead of it.
///
/// <para><b>The seam that makes this deterministic.</b> The state is a pure function of two
/// strings: the <see cref="NodeTypeDefinition.LatestAssemblyPath"/> the instance bound at
/// activation, versus the one the type currently publishes. So the test needs no second compile, no
/// recompile race and no sleep — it writes a DIFFERENT path onto the type node through the ordinary
/// mutation API and the watcher's predicate flips. Nothing ever loads that path (the test never
/// recycles), so a synthetic value is safe; only the COMPARISON is under test.</para>
///
/// <para><b>Both halves in one run, negative first.</b> Asserting only that the banner appears
/// would pass just as well for a banner that is ALWAYS on — which would put a "newer build
/// available" notice above every page in the portal. So the run first proves the slot is empty
/// while the instance is on the current build.</para>
///
/// <para>🚨 <b>This asserts the OFFER, and that nothing recycles by itself.</b> The watcher used to
/// post a self-<c>DisposeRequest</c> here, restarting every live instance of a type on every
/// publish. The instance must still be serving its own content after the publication — the banner
/// is an adornment on a working page, and a page that recycled itself is the regression.</para>
/// </summary>
public class StaleBuildBannerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    [Fact(Timeout = 180000)]
    public async Task NewerPublishedBuild_OffersTheBannerAboveTheInstanceContent()
    {
        var suffix = $"{Guid.NewGuid():N}"[..8];
        var nodeTypeId = $"BannerType{suffix}";
        var nodeTypePath = $"type/{nodeTypeId}";
        // The instance lives UNDER its NodeType path: the root namespace is reserved for
        // partition roots (only Space/User own a partition), so a top-level instance is refused.
        var instancePath = $"{nodeTypePath}/Inst";

        // ── A NodeType that genuinely compiles, so its instance BINDS a real assembly path. ──
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration =
                    "config => config.AddLayout(layout => layout.WithView(\"Overview\", "
                    + "(host, ctx) => Controls.Markdown(\"INSTANCE_CONTENT_MARKER\")))"
            },
            State = MeshNodeState.Active,
        };
        await MeshService.CreateNode(typeNode).Should().Within(60.Seconds()).Emit();

        var compiled = await RequestHub.Observe(
                (IRequest<GetCompilationPathResponse>)new GetCompilationPathRequest(),
                o => o.WithTarget(new Address(nodeTypePath)))
            .Select(d => d.Message)
            .Should().Within(120.Seconds()).Emit();
        compiled.Success.Should().BeTrue($"the type must compile; error: {compiled.Error}");

        // ── An instance of it, activated against that build. ──
        await MeshService.CreateNode(new MeshNode("Inst", nodeTypePath)
        {
            Name = "Banner Instance",
            NodeType = nodeTypePath,
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(instancePath), reference);

        // The instance's OWN content renders. Established first so the banner assertions below are
        // about an adornment on a working page, not about a page that is only a banner.
        var content = await stream.GetControlStream(reference.Area!)
            .Should().Within(60.Seconds()).Match(c => c is not null);
        content.Should().NotBeNull();

        // ── NEGATIVE CONTROL: on the current build there is no offer, so the slot stays empty. ──
        // Without this, an always-on banner passes the positive half.
        var beforeBanner = await stream.GetControlStream(LayoutAreaSlots.StaleBuildBanner)
            .Timeout(10.Seconds())
            .Catch<object?, Exception>(_ => Observable.Return<object?>(null))
            .FirstAsync()
            .ToTask();
        IsBanner(beforeBanner).Should().BeFalse(
            "an instance running the type's CURRENT build must show no banner — otherwise every "
            + "page in the portal carries a 'newer build available' notice");

        // ── THE SIGNAL: the type publishes a DIFFERENT assembly. ──
        // Only the path changes: CompilationStatus / framework version stay as the successful
        // compile left them, so the build is still USABLE and the watcher's guard passes. Nothing
        // loads this path — the test never recycles — so a synthetic value is safe here.
        await Mesh.GetWorkspace().GetMeshNodeStream(nodeTypePath)
            .Update(node =>
            {
                var def = node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
                return node with
                {
                    Content = def with
                    {
                        LatestAssemblyPath = def.LatestAssemblyPath + ".superseded.dll"
                    }
                };
            })
            .Should().Within(60.Seconds()).Emit();

        // ── POSITIVE: the banner appears in the sidecar slot. ──
        // The watcher throttles on the settle window before offering, so this waits on the
        // CONDITION rather than on a sleep.
        var banner = await stream.GetControlStream(LayoutAreaSlots.StaleBuildBanner)
            .Should().Within(120.Seconds()).Match(IsBanner);
        IsBanner(banner).Should().BeTrue(
            "a published build different from the bound one must offer a recycle");

        // …and it is an OFFER, not a restart: the instance is still serving its own content.
        var afterContent = await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is not null);
        afterContent.Should().NotBeNull(
            "the banner ADORNS a working page — an instance that recycled itself on publish is the "
            + "restart-storm regression this replaced");
    }

    /// <summary>
    /// The slot is written empty (a bare Stack) while there is no offer, so "has a banner" means a
    /// control carrying the offer text — not merely "the slot exists".
    /// </summary>
    private static bool IsBanner(object? control) =>
        control is MarkdownControl md
        && md.Markdown?.ToString()?.Contains("build", StringComparison.OrdinalIgnoreCase) == true;
}
