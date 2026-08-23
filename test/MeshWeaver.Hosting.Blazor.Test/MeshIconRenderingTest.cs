using System.Threading.Tasks;
using MeshWeaver.Blazor.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Blazor.Test;

/// <summary>
/// <see cref="MeshIcon"/> rendered to ACTUAL HTML, one case per icon form a
/// <c>MeshNode.Icon</c> legitimately holds (<see cref="MeshWeaver.Domain.Icon.Parse"/>).
///
/// <para>These exist because the previous nav components were only ever asserted at the
/// control-plan level, and the plan carried the icons faithfully while the WIDGET dropped them:
/// FluentNavGroup rendered Fluent names only, so a node's inline-SVG icon vanished from every
/// group heading with nothing red anywhere. A DOM-level assertion per form is what would have
/// caught it.</para>
/// </summary>
public class MeshIconRenderingTest
{
    private static async Task<string> RenderAsync(string? icon, int size = 20)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new System.Collections.Generic.Dictionary<string, object?>
            {
                [nameof(MeshIcon.Value)] = MeshWeaver.Domain.Icon.Parse(icon),
                [nameof(MeshIcon.Size)] = size,
            });
            var output = await renderer.RenderComponentAsync<MeshIcon>(parameters);
            return output.ToHtmlString();
        });
    }

    [Fact]
    public async Task InlineSvg_RendersVerbatim_AndSized()
    {
        // The exact shape node icons typically take: a stroke='currentColor' outline, so the glyph
        // inherits the link's text color and is theme-correct in light AND dark.
        var html = await RenderAsync(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor'>"
            + "<path d='M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z'/></svg>");

        Assert.Contains("stroke='currentColor'", html);
        Assert.Contains("width: 20px; height: 20px;", html);
        Assert.Contains("M12 22s8-4", html);
    }

    [Fact]
    public async Task UrlIcon_RendersAsImg()
    {
        var html = await RenderAsync("/static/NodeTypeIcons/document.svg");

        Assert.Contains("<img", html);
        Assert.Contains("src=\"/static/NodeTypeIcons/document.svg\"", html);
    }

    [Fact]
    public async Task Emoji_RendersAsText()
    {
        var html = await RenderAsync("🧩");

        // Blazor HTML-encodes text content, so the glyph arrives as a numeric character reference.
        Assert.True(html.Contains("🧩") || html.Contains("&#x1F9E9;"),
            $"the emoji must be in the markup (raw or encoded): {html}");
    }

    [Fact]
    public async Task FluentName_RendersAnSvgGlyph()
    {
        var html = await RenderAsync("Document");

        Assert.Contains("<svg", html);
    }

    [Fact]
    public async Task UnknownFluentName_AndNull_RenderNothing_NeverThrow()
    {
        Assert.Equal(string.Empty, (await RenderAsync("NotARealIconName")).Trim());
        Assert.Equal(string.Empty, (await RenderAsync(null)).Trim());
    }
}
