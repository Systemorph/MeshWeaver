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
            var pipeline = MarkdownExtensions.CreateMarkdownPipeline(Stream?.Owner);
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

        RenderNodes(builder, doc.DocumentNode.ChildNodes);
    }

    private void RenderNodes(RenderTreeBuilder builder, IEnumerable<HtmlNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case HtmlTextNode text:
                    builder.AddMarkupContent(1, text.Text);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains(LayoutAreaMarkdownRenderer.LayoutArea):
                    var address = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Address}", null);
                    var area = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Area}", null);
                    var areaId = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.AreaId}", null);
                    RenderLayoutArea(builder, address, area, areaId);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("mermaid"):
                    builder.OpenComponent<Mermaid>(1);
                    builder.AddAttribute(2, nameof(Mermaid.IsDark), Mode is DesignThemeModes.Dark);
                    builder.AddAttribute(3, nameof(Mermaid.Diagram), node.InnerHtml);
                    builder.CloseComponent();
                    break;
                case { Name: "pre" } when node.ChildNodes.Any(n => n.Name == "code"):
                    builder.OpenComponent<CodeBlock>(1);
                    builder.AddAttribute(2, nameof(CodeBlock.Html), node.OuterHtml);
                    builder.CloseComponent();
                    break;
                case { Name: "span" } when node.GetAttributeValue("class", "").Contains("math"):
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("math"):
                    builder.OpenComponent<MathBlock>(1);
                    builder.AddAttribute(2, nameof(MathBlock.Name), node.Name);
                    builder.AddAttribute(3, nameof(MathBlock.Html), node.InnerHtml);
                    builder.CloseComponent();
                    break;
                default:
                    builder.OpenElement(1, node.Name);
                    foreach (var attribute in node.Attributes)
                        builder.AddAttribute(2, attribute.Name, attribute.Value);
                    RenderNodes(builder, node.ChildNodes);
                    builder.CloseElement();
                    break;
            }
        }
    }


    private void RenderLayoutArea(RenderTreeBuilder builder, string address, string area, string areaId)
    {
        builder.OpenElement(1, "div");
        builder.AddAttribute(2, "class", "layout-area");

        builder.OpenComponent<LayoutAreaView>(3);
        builder.AddAttribute(4, nameof(LayoutAreaView.ViewModel), new LayoutAreaControl((Address)address, new LayoutAreaReference(area) { Id = areaId })
        {
            ShowProgress = true,
            ProgressMessage = $"Loading {area}"
        });
        builder.CloseComponent();

        builder.CloseElement();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        IsNotPrerender = true;
    }

    private bool IsNotPrerender { get; set; }
    private string ComponentContainerId(string id) => $"component-container-{id}";
}
