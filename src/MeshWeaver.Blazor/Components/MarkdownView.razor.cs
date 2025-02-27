using HtmlAgilityPack;
using Markdig;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
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
            var pipeline = MarkdownExtensions.CreateMarkdownPipeline(Stream.Owner);
            var document = Markdig.Markdown.Parse(Markdown, pipeline);
            Html = document.ToHtml(pipeline);
        }
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
        foreach (var node in nodes)
        {
            switch (node)
            {
                case HtmlTextNode text:
                    builder.AddMarkupContent(sequence++, text.Text);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains(LayoutAreaMarkdownRenderer.LayoutArea):
                    var address = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Address}", null);
                    var area = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Area}", null);
                    var areaId = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.AreaId}", null);
                    RenderLayoutArea(builder, address, area, areaId, ref sequence);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("mermaid"):
                    builder.OpenComponent<Mermaid>(sequence++);
                    builder.AddAttribute(sequence++, nameof(Mermaid.IsDark), Mode is DesignThemeModes.Dark);
                    builder.AddAttribute(sequence++, nameof(Mermaid.Diagram), node.InnerHtml);
                    builder.CloseComponent();
                    break;
                case { Name: "pre" } when node.ChildNodes.Any(n => n.Name == "code"):
                    builder.OpenComponent<CodeBlock>(sequence++);
                    builder.AddAttribute(sequence++, nameof(CodeBlock.Html), node.OuterHtml);
                    builder.CloseComponent();
                    break;
                case { Name: "span" } when node.GetAttributeValue("class", "").Contains("math"):
                    builder.OpenComponent<MathBlock>(sequence++);
                    builder.AddAttribute(sequence++, nameof(MathBlock.Html), node.OuterHtml);
                    builder.CloseComponent();
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


    private void RenderLayoutArea(RenderTreeBuilder builder, string address, string area, string areaId, ref int sequence)
    {
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "layout-area");

        builder.OpenComponent<LayoutAreaView>(sequence++);
        builder.AddAttribute(sequence++, nameof(LayoutAreaView.ViewModel), new LayoutAreaControl((Address)address, new LayoutAreaReference(area) { Id = areaId })
        {
            ShowProgress = true,
            ProgressMessage = $"Loading {area}"
        });
        builder.CloseComponent();

        builder.CloseElement();
    }

    private string ComponentContainerId(string id) => $"component-container-{id}";
}
