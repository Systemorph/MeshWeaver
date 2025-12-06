using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace MeshWeaver.Markdown;

/// <summary>
/// Renders DataContentBlock as a div with data attributes for client-side resolution.
/// The client fetches the data and displays it as JSON.
/// </summary>
public class DataContentBlockRenderer : HtmlObjectRenderer<DataContentBlock>
{
    public const string DataContent = "data-content";
    public const string AddressAttr = "data-address";
    public const string PathAttr = "data-path";

    protected override void Write(HtmlRenderer renderer, DataContentBlock obj)
    {
        renderer.EnsureLine();
        renderer.Write($"<div class='{DataContent}'");
        renderer.Write($" {AddressAttr}='{obj.Address}'");
        renderer.Write($" {PathAttr}='{obj.Path}'");
        renderer.Write("></div>");
        renderer.EnsureLine();
    }
}
