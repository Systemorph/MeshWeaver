using System.Linq;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Why the Apps grid rendered icons "tiny", and the two rules that keep it fixed.
///
/// <para>A node icon is injected as a raw <c>MarkupString</c>, so Blazor's CSS isolation never
/// stamps its scope attribute onto that <c>&lt;svg&gt;</c>. An unscoped <c>.mesh-search-icon-box
/// svg</c> rule therefore matched NOTHING, and an icon authored with <c>width="24" height="24"</c>
/// on its root tag rendered at 24px inside a 64px tile. The row icon was never affected because its
/// rule always had <c>::deep</c> — which is exactly the correlation that made this look like an
/// authoring problem rather than a stylesheet one.</para>
///
/// <para>Two independent guards now: <c>::deep</c> in the stylesheet (the real fix) and
/// <c>SizeInlineSvg</c> forcing the size onto the element (so the next authored icon carrying a
/// fixed size cannot silently regress the grid). This fixture pins the ICON side of that contract —
/// what core ships must not carry root-tag dimensions in the first place.</para>
/// </summary>
public class AppIconRenderingTest
{
    private static string IconFor(string appId) =>
        UserActivityLayoutAreas
            .AppRecordSpecs(new HomeConfig { DefaultApps = [appId] }, "alice")
            .Single().Icon;

    [Fact]
    public void The_Threads_icon_carries_no_root_width_or_height()
    {
        // 🚨 The root cause, pinned. width/height on the root tag render at literal pixels inside
        // the tile; a viewBox alone lets the surface decide the size.
        var icon = UserActivityLayoutAreas.ThreadsIcon;

        var root = icon[..icon.IndexOf('>')];
        root.Should().NotContain("width=", "a fixed root width renders at that many pixels in a 64px tile");
        root.Should().NotContain("height=");
        root.Should().Contain("viewBox");
    }

    [Fact]
    public void The_Threads_icon_uses_attribute_styling_only()
    {
        // React Native renders neither <style> blocks nor class-driven fills, so an icon that
        // depends on them is invisible on the phone and fine on the web — the worst kind of bug,
        // because whoever authors it cannot see it.
        UserActivityLayoutAreas.ThreadsIcon.Should().NotContain("<style");
        UserActivityLayoutAreas.ThreadsIcon.Should().NotContain("class=");
    }

    [Fact]
    public void The_Threads_icon_namespaces_its_gradient_id()
    {
        // Several inline SVGs land in ONE document. A generic id like "grad" means the first
        // definition wins for every icon that references that name.
        UserActivityLayoutAreas.ThreadsIcon.Should().Contain("mw-threads-grad");
    }

    [Fact]
    public void The_Threads_app_is_full_bleed_artwork_not_a_grey_glyph()
    {
        // The complaint was that Threads looked unfinished beside the Store's tile. Inline artwork
        // rather than a static glyph URL is what that means concretely.
        var icon = IconFor("~/" + UserActivityLayoutAreas.ChatArea);

        icon.Should().StartWith("<svg", "an inline icon renders as artwork rather than a fetched glyph");
        icon.Should().NotContain("chat.svg");
    }

    [Fact]
    public void Sizing_an_authored_icon_overrides_its_own_dimensions()
    {
        // The defence in depth: whatever an icon author wrote, the surface's size wins. Duplicate
        // attributes resolve first-wins in HTML parsing, so the injected style must come FIRST.
        const string authored = "<svg width='24' height='24' viewBox='0 0 24 24'><rect/></svg>";

        var sized = MeshNodeImageHelper.SizeInlineSvg(authored, 36);

        sized.IndexOf("style=", StringComparison.Ordinal)
            .Should().BeLessThan(sized.IndexOf("width='24'", StringComparison.Ordinal),
                "the injected size has to precede the authored one to win");
        sized.Should().Contain("36px");
    }
}
