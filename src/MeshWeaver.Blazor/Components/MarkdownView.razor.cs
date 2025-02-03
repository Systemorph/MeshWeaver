using HtmlAgilityPack;
using Markdig;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using MarkdownExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Blazor.Components;

public partial class MarkdownView
{
    private string Html { get; set; }
    private string Markdown { get; set; }

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Markdown, x => x.Markdown);
        DataBind(ViewModel.Html, x => x.Html);
        if (Html == null)
        {
            // TODO V10: Collection would not be good here. (25.01.2025, Roland Bürgi)
            var pipeline = MarkdownExtensions.CreateMarkdownPipeline(Stream.Owner, Stream.Owner);
            var document = Markdig.Markdown.Parse(Markdown, pipeline);
            Html = document.ToHtml(pipeline);
        }
    }


    private IJSObjectReference highlight;
    private IJSObjectReference mermaid;
    private IJSObjectReference katex;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            highlight ??= await JsRuntime.Import("highlight.js");
            mermaid ??= await JsRuntime.Import("mermaid.js");
            katex ??= await JsRuntime.Import("katex.js");
        }
        if (highlight is not null)
            await highlight.InvokeVoidAsync("highlightCode", Element);
        if (mermaid is not null)
            await mermaid.InvokeVoidAsync("contentLoaded", Mode is DesignThemeModes.Dark);
        if (katex is not null)
            await katex.InvokeVoidAsync("renderElement", Element);
    }

    private void RenderHtml(RenderTreeBuilder builder)
    {
        if (Html is null)
            return;

        var doc = new HtmlDocument();
        doc.LoadHtml(Html);

        var sequence = 0;
        RenderNodes(builder, doc.DocumentNode.ChildNodes, ref sequence);
    }
    private void RenderNodes(RenderTreeBuilder builder, IEnumerable<HtmlNode> nodes, ref int sequence)
    {
        // Handle code blocks
        foreach (var node in nodes)
        {
            switch (node)
            {
                case HtmlTextNode text:
                    builder.AddMarkupContent(sequence++, text.Text);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains(LayoutAreaMarkdownRenderer.LayoutArea):
                    //var divId = node.GetAttributeValue("id", string.Empty);
                    var address = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Address}", null);
                    var area = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Area}", null);
                    var areaId = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.AreaId}", null);
                    RenderLayoutArea(builder, address, area, areaId, ref sequence);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("mermaid"):
                    builder.OpenElement(sequence++, node.Name);
                    foreach (var attribute in node.Attributes)
                        builder.AddAttribute(sequence++, attribute.Name, attribute.Value);
                    builder.AddContent(sequence++, string.Join('\n', node.ChildNodes.OfType<HtmlTextNode>().Select(t => t.Text)));
                    builder.CloseElement();
                    break;

                default:
                    builder.OpenElement(sequence++, node.Name);
                    foreach (var attribute in node.Attributes)
                        builder.AddAttribute(sequence++, attribute.Name, attribute.Value);

                    RenderNodes(builder, node.ChildNodes, ref sequence);
                    builder.CloseElement();
                    break;
            }
        }
    }
    private void RenderLayoutArea(
        RenderTreeBuilder builder,
        string address,
        string area,
        string areaId,
        ref int sequence)
    {
        builder.OpenComponent<LayoutAreaView>(sequence++);
        builder.AddAttribute(sequence++,
            nameof(LayoutAreaView.ViewModel),
            new LayoutAreaControl((Address)address, new LayoutAreaReference(area) { Id = areaId })
            {
                ShowProgress = true,
                ProgressMessage = $"Loading {area}"
            }
        );
        builder.CloseComponent();
    }


    private string ComponentContainerId(string id) => $"component-container-{id}";

}
