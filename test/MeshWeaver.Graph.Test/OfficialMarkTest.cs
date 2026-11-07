using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The official-mark oversteer</b> — the seam that lets a vendor's registered mark render
/// unaltered on a store card while the browser tab keeps the portal's own mark.
///
/// <para>Two rules, and each one is a bug in the other's direction. <b>Recoloring a vendor mark is a
/// brand violation</b> — the default policy repaints <c>currentColor</c> white on a hash-derived hue,
/// which is right for a house glyph and forbidden for a mark we are invoking nominatively (these
/// packages are API clients to those services). <b>Putting a vendor mark in the tab is a
/// misrepresentation</b> — a favicon identifies the site occupying the tab, not a service it talks
/// to.</para>
///
/// <para>Neither failure raises anything: a recolored mark still renders, and a wrong favicon still
/// loads. So both are pinned here or they drift back.</para>
/// </summary>
public class OfficialMarkTest
{
    /// <summary>A vendor mark, near-black like the real OpenAI one — the color that makes plateless
    /// pass-through invisible on the dark theme.</summary>
    private const string OfficialMark =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' data-mw-mark='official'>"
        + "<path d='M4 4h16v16H4z' fill='#111827'/></svg>";

    private const string HouseOutline =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' "
        + "stroke='currentColor'><path d='M4 4h16v16H4z'/></svg>";

    // ---- the mark itself is never altered -------------------------------------------------

    [Fact]
    public void AnOfficialMark_KeepsItsOwnColors()
    {
        var rendered = IconBackplate.Ensure(OfficialMark);

        // The brand color survives verbatim. This is the whole point of the oversteer.
        Assert.Contains("#111827", rendered);
    }

    [Fact]
    public void AnOfficialMark_IsNotRecoloredWhite()
    {
        // currentColor recoloring is what the default policy does; on a vendor mark it repaints the
        // logo. A mark authored with currentColor must keep it.
        var withCurrentColor =
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' data-mw-mark='official'>"
            + "<path d='M4 4h16v16H4z' fill='currentColor'/></svg>";

        Assert.Contains("currentColor", IconBackplate.Ensure(withCurrentColor));
    }

    [Fact]
    public void AnOfficialMark_SitsOnAWhitePlate_NotAHashedHue()
    {
        var rendered = IconBackplate.Ensure(OfficialMark);

        // 🚨 NOT bare pass-through: a near-black mark with no plate vanishes on the dark theme —
        // the exact defect IconBackplate exists to prevent. White is also what the guidelines
        // prescribe, so the mark stays unaltered AND legible on both grounds.
        Assert.Contains(IconBackplate.OfficialPlate, rendered);
        Assert.DoesNotContain(IconBackplate.HueFor(OfficialMark), rendered);
    }

    [Fact]
    public void AHouseIcon_IsUnaffectedByTheOversteer()
    {
        // The control. Without it, "official marks work" could be satisfied by disabling the policy
        // for everything, which would silently return every currentColor outline to invisibility.
        var rendered = IconBackplate.Ensure(HouseOutline);

        Assert.DoesNotContain("currentColor", rendered);
        Assert.Contains(IconBackplate.HueFor(HouseOutline), rendered);
        Assert.DoesNotContain(IconBackplate.OfficialMarkValue, rendered);
    }

    // ---- claiming the treatment -----------------------------------------------------------

    [Fact]
    public void TheClaimSurvivesPlating()
    {
        // A caller downstream of Ensure must read the same answer as one upstream of it, or the
        // favicon rule silently stops applying to an already-rendered mark.
        Assert.True(IconBackplate.IsOfficialMark(OfficialMark));
        Assert.True(IconBackplate.IsOfficialMark(IconBackplate.Ensure(OfficialMark)));
    }

    [Theory]
    [InlineData("<svg viewBox='0 0 24 24'><path d='M0 0h1v1H0z' data-mw-mark='official'/></svg>")]
    [InlineData("<svg viewBox='0 0 24 24'><!-- data-mw-mark='official' --><path d='M0 0h1v1H0z'/></svg>")]
    public void OnlyTheROOTMayClaimIt(string svg)
    {
        // A nested element (or a comment) must not be able to smuggle the claim in from arbitrary
        // authored content — otherwise any Store package could opt its icon out of the legibility
        // policy without declaring it.
        Assert.False(IconBackplate.IsOfficialMark(svg));
    }

    [Fact]
    public void NonSvgFormsCannotClaimIt()
    {
        Assert.False(MeshNodeImageHelper.IsOfficialMark(null));
        Assert.False(MeshNodeImageHelper.IsOfficialMark(""));
        Assert.False(MeshNodeImageHelper.IsOfficialMark("/static/NodeTypeIcons/box.svg"));
        Assert.False(MeshNodeImageHelper.IsOfficialMark("🤖"));
    }

    // ---- the favicon diverges -------------------------------------------------------------

    [Fact]
    public void TheTabShowsTheMESHWEAVERMark_NotTheVendors()
    {
        var node = new MeshNode("Providers/OpenAI") { Icon = OfficialMark };

        var link = MeshNodeImageHelper.ResolveIconLink(node);

        // The vendor's mark must not reach the tab in any form — the data: URI would carry it
        // percent-encoded, so assert on the resolved target instead of scanning for the color.
        Assert.Contains(MeshNodeImageHelper.MeshWeaverMarkUrl, link.Href);
        Assert.DoesNotContain("111827", Uri.UnescapeDataString(link.Href));
    }

    [Fact]
    public void AnOrdinaryNodeIconStillReachesTheTab()
    {
        // The control for the favicon half: the substitution must apply ONLY to official marks, or
        // every node page silently loses its own tab icon.
        var node = new MeshNode("Docs/Page") { Icon = HouseOutline };

        var link = MeshNodeImageHelper.ResolveIconLink(node);

        Assert.DoesNotContain(MeshNodeImageHelper.MeshWeaverMarkUrl, link.Href);
        Assert.Equal(MeshNodeImageHelper.SvgMediaType, link.Type);
    }

    [Fact]
    public void TheCardKeepsTheVendorMarkWhileTheTabDoesNot()
    {
        // The two seams in one assertion — the divergence IS the feature, and a change that
        // collapses them back together (in either direction) has to fail something.
        var card = MeshNodeImageHelper.ResolveRenderable(OfficialMark);
        var tab = MeshNodeImageHelper.ResolveIconLink(new MeshNode("Providers/OpenAI") { Icon = OfficialMark });

        Assert.Contains("#111827", card.Value);
        Assert.Contains(MeshNodeImageHelper.MeshWeaverMarkUrl, tab.Href);
    }

    // ---- the plate follows the theme where the mark reads on dark (2026-11-07) ---------------

    /// <summary>
    /// The inbox's channel marks (Outlook, Gmail, iMessage, WhatsApp, Teams) read on a dark ground, so
    /// their plate follows the theme: white in light mode, <see cref="IconBackplate.OfficialDarkPlate"/>
    /// in dark mode — the white tile glared in a dark inbox. The brand colours stay byte-identical.
    /// </summary>
    [Theory]
    [InlineData("Outlook")]
    [InlineData("Gmail")]
    [InlineData("iMessage")]
    [InlineData("WhatsApp")]
    [InlineData("Teams")]
    public void AChannelMark_GetsTheThemeFollowingPlate(string source)
    {
        var mark = InboxSourceIcons.Resolve(source, null);
        Assert.True(IconBackplate.ReadsOnDark(mark), $"{source} reads on the dark plate");

        var rendered = IconBackplate.Ensure(mark);

        Assert.Contains($"class='{IconBackplate.ThemePlateClass}'", rendered);
        Assert.Contains("prefers-color-scheme: dark", rendered);
        Assert.Contains(IconBackplate.OfficialDarkPlate, rendered);
        // Light mode (and a renderer without media queries) keeps the white plate.
        Assert.Contains($"fill='{IconBackplate.OfficialPlate}'", rendered);
        // The mark itself is untouched.
        Assert.Contains(mark.Trim()[^120..], rendered);
        // Still recognised as plated, so no renderer plates it a second time.
        Assert.True(IconBackplate.HasBackplate(rendered));
    }

    /// <summary>A near-black vendor mark would vanish on the dark plate: it keeps the white plate on both themes.</summary>
    [Fact]
    public void ANearBlackMark_KeepsTheWhitePlate()
    {
        Assert.False(IconBackplate.ReadsOnDark(OfficialMark));
        var rendered = IconBackplate.Ensure(OfficialMark);
        Assert.DoesNotContain(IconBackplate.ThemePlateClass, rendered);
        Assert.DoesNotContain(IconBackplate.OfficialDarkPlate, rendered);
    }

    /// <summary>currentColor, a default (unset) fill and black all count as dark; gradients are not judged.</summary>
    [Theory]
    [InlineData("<svg viewBox='0 0 24 24'><path d='M0 0h1v1z' fill='currentColor'/></svg>", false)]
    [InlineData("<svg viewBox='0 0 24 24'><path d='M0 0h1v1z'/></svg>", false)]
    [InlineData("<svg viewBox='0 0 24 24'><path d='M0 0h1v1z' fill='black'/></svg>", false)]
    [InlineData("<svg viewBox='0 0 24 24'><path d='M0 0h1v1z' style='fill:#000'/></svg>", false)]
    [InlineData("<svg viewBox='0 0 24 24'><path d='M0 0h1v1z' fill='url(#g)'/><path d='M0 0h1v1z' fill='#fff'/></svg>", true)]
    [InlineData("<svg viewBox='0 0 24 24'><g fill='#25D366'><path d='M0 0h1v1z'/></g></svg>", true)]
    [InlineData("<svg viewBox='0 0 24 24'><g fill='#111111'><path d='M0 0h1v1z'/></g></svg>", false)]
    [InlineData("<svg viewBox='0 0 24 24'></svg>", false)]
    public void ReadsOnDark_JudgesSolidColoursOnly(string svg, bool expected)
        => Assert.Equal(expected, IconBackplate.ReadsOnDark(svg));

    /// <summary>The dark plate is dark enough that the light theme's white text detail would never be expected on it, and each channel's colours clear the bar.</summary>
    [Fact]
    public void TheDarkPlate_ContrastsWithTheChannelColours()
    {
        var plate = MeshWeaver.Layout.Palettes.OklchColor.FromHex(IconBackplate.OfficialDarkPlate);
        foreach (var brand in new[] { "#4285F4", "#34A853", "#FBBC04", "#EA4335", "#C5221F", "#34DA50", "#25D366", "#ffffff" })
            Assert.True(MeshWeaver.Layout.Palettes.OklchColor.FromHex(brand).ContrastRatio(plate) >= IconBackplate.MinDarkPlateContrast, brand);
    }
}
