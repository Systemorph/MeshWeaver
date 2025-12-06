using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace MeshWeaver.Markdown;

/// <summary>
/// Renders FileContentBlock as a div with data attributes for client-side resolution.
/// Client determines rendering based on mime type with fallback to download link.
/// </summary>
public class FileContentBlockRenderer : HtmlObjectRenderer<FileContentBlock>
{
    public const string FileContent = "file-content";
    public const string AddressAttr = "data-address";
    public const string PathAttr = "data-path";

    protected override void Write(HtmlRenderer renderer, FileContentBlock obj)
    {
        renderer.EnsureLine();
        renderer.Write($"<div class='{FileContent}'");
        renderer.Write($" {AddressAttr}='{obj.Address}'");
        renderer.Write($" {PathAttr}='{obj.Path}'");
        renderer.Write("></div>");
        renderer.EnsureLine();
    }
}
