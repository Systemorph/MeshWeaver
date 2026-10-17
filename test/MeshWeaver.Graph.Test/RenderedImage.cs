using System.Linq;
using MeshWeaver.Layout;
using Xunit;
using DomainIcon = MeshWeaver.Domain.Icon;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Reads an icon SURFACE — a tile, a preview, a title icon — the way the page receives it now that
/// each is an <see cref="ImageControl"/> rather than hand-built markup.
///
/// <para><b>What core owns, and what this reads.</b> A surface hands the icon VALUE to an
/// <see cref="ImageControl"/>; every renderer then decides what to draw through
/// <see cref="ImageSources.Resolve"/>, and draws <see cref="ImageSourceKind.Svg"/> ONLY as
/// <c>&lt;img src="ImageSources.SvgDataUri(IconBackplate.Ensure(…))"&gt;</c> (the Blazor
/// <c>ImageView</c>; the web and React Native leaves mirror it). <see cref="SvgMarkup"/> composes
/// exactly those core primitives, so a test reads the image the renderer contract produces — and a
/// surface that went back to emitting markup fails <see cref="TheImage"/> before anything else.</para>
/// </summary>
internal static class RenderedImage
{
    /// <summary>
    /// The one <see cref="ImageControl"/> of <paramref name="surface"/>, after asserting the surface
    /// carries no <see cref="HtmlControl"/> at all — the icon is a value, never page markup.
    /// </summary>
    public static ImageControl TheImage(UiControl surface, string because)
    {
        var tree = MarkdownEditIsATemplateTest.Descendants(surface).ToList();
        tree.OfType<HtmlControl>().Should().BeEmpty(because + " — an icon surface is platform controls, never markup");
        return tree.OfType<ImageControl>().Should().ContainSingle(because).Subject;
    }

    /// <summary>
    /// What a renderer draws for <paramref name="surface"/>'s inline-svg icon, by the shared
    /// contract: resolved as <see cref="ImageSourceKind.Svg"/>, plated, and loaded from a
    /// <c>data:</c> URI.
    /// </summary>
    public static string SvgMarkup(UiControl surface, string because)
    {
        var image = TheImage(surface, because);
        var source = image.Source.Should().BeOfType<string>(because + " — a literal surface carries the value").Subject;
        var resolved = ImageSources.Resolve(DomainIcon.Parse(source));
        resolved.Kind.Should().Be(ImageSourceKind.Svg, because + " — inline svg is drawn only as an image");
        return $"<img src=\"{ImageSources.SvgDataUri(IconBackplate.Ensure(resolved.Value))}\" alt=\"\" />";
    }
}
