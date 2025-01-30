using HtmlAgilityPack;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Components;

public partial class MarkdownComponent
{
    private IJSObjectReference highlight;
    private IJSObjectReference mermaid;
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            highlight ??= await JsRuntime.Import("highlight.js");
            mermaid ??= await JsRuntime.Import("mermaid.js");
            await highlight.InvokeVoidAsync("highlightCode", Element);
            await mermaid.InvokeVoidAsync("contentLoaded", Mode is DesignThemeModes.Dark);
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
        // Handle code blocks
        foreach (var node in nodes)
        {
            switch (node)
            {
                case HtmlTextNode text:
                    builder.AddMarkupContent(sequence++, text.Text);
                    break;
                case { Name: "code" } when node.GetAttributeValue("class", "").Contains(ExecutableCodeBlockRenderer.CodeBlock):
                    var arguments = node.GetAttributeValue($"data-{ExecutableCodeBlockRenderer.Arguments}", null);
                    var language = node.GetAttributeValue($"data-{ExecutableCodeBlockRenderer.Language}", null);
                    var rawContent = node.GetAttributeValue($"data-{ExecutableCodeBlockRenderer.RawContent}", null);
                    var content = node.InnerHtml;
                    var control = new CodeBlockControl(rawContent, language)
                        .WithArguments(arguments)
                        .WithHtml(content);

                    RenderCodeBlock(builder, control, ref sequence);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains(LayoutAreaMarkdownRenderer.LayoutArea):
                    //var divId = node.GetAttributeValue("id", string.Empty);
                    var address = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Address}", null);
                    var area = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Area}", null);
                    var areaId = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.AreaId}", null);
                    RenderLayoutArea(builder, address, area, areaId, ref sequence);
                    break;
                default:
                    builder.OpenElement(sequence++, node.Name);
                    foreach (var attribute in node.Attributes)
                    {
                        builder.AddAttribute(sequence++, attribute.Name, attribute.Value);
                    }

                    RenderNodes(builder, node.ChildNodes, ref sequence);
                    builder.CloseElement();
                    break;
            }
        }
    }

    private bool ConvertToBool(string val)
    {
        throw new NotImplementedException();
    }

    private void RenderMermaidDiagram(RenderTreeBuilder builder, string content, ref int sequence)
    {
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "mermaid");
        builder.AddContent(sequence++, content);
        builder.CloseElement();
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

    private void RenderCodeBlock(RenderTreeBuilder builder, CodeBlockControl viewModel, ref int sequence)
    {
        builder.OpenComponent<CodeBlockView>(sequence++);
        builder.AddAttribute(sequence++,
            nameof(CodeBlockView.ViewModel),
            viewModel
        );
        builder.CloseComponent();
    }


}
