using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Deterministic repro + contract for the interactive-markdown <b>subscribe-before-create</b> storm.
///
/// <para><b>The defect.</b> A markdown view with executable code blocks embeds, for each block, a
/// <c>&lt;div class='layout-area' data-address='{owner}/_Activity/markdown-{id}' …&gt;</c>. The Blazor
/// renderer turns each into a <c>LayoutAreaView</c> that subscribes — via
/// <c>Workspace.GetRemoteStream&lt;JsonElement,LayoutAreaReference&gt;</c>, which BYPASSES the
/// <c>MeshNodeStreamCache</c> storm-breaker — to that activity address. The view used to embed the LIVE
/// address at the very first render, BEFORE its <c>OnAfterRenderAsync</c> created the activity (and the
/// create is itself async), so every block's subscription raced the create and hit the address before it
/// existed → a <c>[ROUTE] NotFound</c> burst (one subscriber per executable block; measured ~59× on
/// <c>Doc/DataMesh/InteractiveMarkdown</c>, which has 3 executable blocks, across the
/// prerender→interactive transition).</para>
///
/// <para><b>The fix.</b> <see cref="MarkdownViewLogic.RenderKernelResultAreas"/> gates the LIVE
/// (subscribing) embed on the activity being created + routable. Until then the kernel area renders as a
/// static, NON-subscribing placeholder, so the GUI never opens a subscription to a not-yet-created
/// address. The view flips its <c>kernelReady</c> flag from <c>CreateActivityAndSubmit</c>'s
/// <c>onReady</c> callback (which fires only once the activity is routable) and re-renders with the live
/// area embedded.</para>
///
/// <para>These tests assert the gate's three states directly on the produced HTML — the presence of a
/// live <c>data-address</c> is exactly what makes the GUI mount a <c>LayoutAreaView</c> and subscribe.</para>
/// </summary>
public class KernelSubscribeGateTest
{
    // One executable --render block ⇒ one kernel result-area placeholder div.
    private const string KernelMarkdown = """
        ```csharp --render gate-demo
        Controls.Markdown("hi")
        ```
        """;

    private static string RenderedKernelHtml()
    {
        var rendered = MarkdownViewLogic.Render(KernelMarkdown, null, null);
        rendered.CodeSubmissions.Should().NotBeNull("the markdown contains an executable --render block");
        rendered.Html.Should().Contain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "the renderer embeds the kernel-address placeholder for later runtime substitution");
        return rendered.Html;
    }

    /// <summary>
    /// THE REPRO. With a real owner but the per-view Activity NOT yet created/routable, the kernel
    /// result area must NOT embed a live, subscribable address — otherwise the GUI mounts a
    /// LayoutAreaView per block that subscribes to <c>{owner}/_Activity/markdown-{id}</c> before it
    /// exists and NotFound-storms the router. Pre-fix (the embed was unconditional once an owner was
    /// present) this carried the live <c>data-address</c>; post-fix it is a non-subscribing placeholder.
    /// </summary>
    [Fact]
    public void OwnedButKernelNotReady_RendersNonSubscribingPlaceholder_NoLiveAddress()
    {
        var addr = new Address("rbuergi/Doc/_Activity/markdown-abc");

        var html = MarkdownViewLogic.RenderKernelResultAreas(
            RenderedKernelHtml(), ownerPath: "rbuergi/Doc", kernelReady: false, kernelAddress: addr);

        html.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder);
        html.Should().NotContain("data-address=",
            "before the activity is created the kernel area must carry NO subscribable address — "
            + "no LayoutAreaView mount ⇒ no subscription ⇒ no NotFound storm (the bug)");
        html.Should().NotContain(addr.ToString());
        html.Should().Contain("markdown-kernel-pending",
            "the not-yet-ready case must SURFACE as a visible, non-subscribing placeholder, not blank");
    }

    /// <summary>
    /// Once the activity is created + routable (the view flips kernelReady), the LIVE area is embedded
    /// so the GUI finally subscribes — to an address that now EXISTS — and renders the kernel result.
    /// </summary>
    [Fact]
    public void OwnedAndKernelReady_EmbedsLiveSubscribableAddress()
    {
        var addr = new Address("rbuergi/Doc/_Activity/markdown-abc");

        var html = MarkdownViewLogic.RenderKernelResultAreas(
            RenderedKernelHtml(), ownerPath: "rbuergi/Doc", kernelReady: true, kernelAddress: addr);

        html.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder);
        html.Should().Contain("data-address=");
        html.Should().Contain(addr.ToString(),
            "after the activity is routable the live address is embedded so the GUI subscribes");
    }

    /// <summary>
    /// No owning node ⇒ no per-node hub to host the kernel. The area renders a static "unavailable"
    /// notice and never subscribes (an ownerless <c>_Activity/markdown-*</c> would NotFound-storm —
    /// the same shape <c>ActivityNodeGuard</c> blocks at create time). Unchanged by the gate.
    /// </summary>
    [Fact]
    public void NoOwner_RendersDisabledNotice_NeverSubscribes()
    {
        var addr = new Address("_Activity/markdown-abc");

        var html = MarkdownViewLogic.RenderKernelResultAreas(
            RenderedKernelHtml(), ownerPath: null, kernelReady: false, kernelAddress: addr);

        html.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder);
        html.Should().NotContain("data-address=");
        html.Should().Contain("markdown-kernel-disabled");
    }
}
