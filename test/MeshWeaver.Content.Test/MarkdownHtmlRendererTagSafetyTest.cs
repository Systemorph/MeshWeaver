using MeshWeaver.Blazor.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Regression for the 2026-06-26 demo crashes: agent/markdown HTML is UNTRUSTED, and a
/// malformed tag name (e.g. <c>"summary\n"</c> — agents end every response with a
/// <c>&lt;summary&gt;</c> block, so a stray newline inside the tag is common) used to flow
/// straight from <see cref="MarkdownHtmlRenderer.RenderHtml"/> into the Blazor render tree.
/// On the client that became <c>document.createElement("summary\n")</c> → InvalidCharacterError
/// → "error applying batch N" → the SignalR circuit disconnected and the user's session died.
///
/// <para>The renderer now validates every tag (and attribute) name; an invalid one is dropped
/// and its children render inline. This test pins the invariant: no matter the HTML,
/// <c>RenderHtml</c> NEVER emits an element frame whose tag name isn't a legal
/// <c>createElement()</c> name.</para>
/// </summary>
public class MarkdownHtmlRendererTagSafetyTest
{
    [Theory]
    [InlineData("<details>\n<summary>\nTitle\n</summary>\nbody\n</details>")]   // the agent <summary> shape
    [InlineData("<summary\n>oops</summary>")]                                     // newline INSIDE the tag
    [InlineData("<summary\nclass=\"x\">oops</summary>")]
    [InlineData("<p>normal</p><summary>tail summary block</summary>")]
    [InlineData("<div\n>x</div>")]
    [InlineData("<ul><li>a</li><li>b</li></ul>")]                                 // well-formed control
    public void RenderHtml_NeverEmitsAnInvalidElementTagName(string html)
    {
        var renderer = new MarkdownHtmlRenderer(DesignThemeModes.Light, stream: null);
        var builder = new RenderTreeBuilder();

        renderer.RenderHtml(builder, html);

        // BL0006: the RenderTree frame types are "not for use outside Blazor", but inspecting the
        // emitted frames is exactly how this test pins the tag-name invariant — there is no public
        // API to read the produced tag names. Intentional, test-only.
#pragma warning disable BL0006
        var frames = builder.GetFrames();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            if (frame.FrameType != RenderTreeFrameType.Element)
                continue;

            var name = frame.ElementName;
            name.Should().NotBeNullOrEmpty(
                "an empty tag name throws InvalidCharacterError on createElement");
            name.Should().MatchRegex("^[A-Za-z][A-Za-z0-9-]*$",
                $"every element tag reaching the Blazor client must be a legal createElement() name " +
                $"(got '{name}' from html '{html}') — a malformed name crashes the render batch and kills the circuit");
        }
#pragma warning restore BL0006
    }
}
