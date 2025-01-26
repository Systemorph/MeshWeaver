using HtmlAgilityPack;
using MeshWeaver.Data;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Components;

public partial class MarkdownComponent : IDisposable
{
    private IJSObjectReference htmlUtils;
    private IJSObjectReference highlight;
    private bool markdownChanged;
    private readonly Lazy<KernelAddress> kernelAddress = new(() => new());
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender || markdownChanged)
        {
            htmlUtils ??= await JsRuntime.Import("htmlUtils.js");
            await htmlUtils.InvokeVoidAsync("formatMath", Element);
            highlight ??= await JsRuntime.Import("highlight.js");
            await highlight.InvokeVoidAsync("highlightCode", Element);
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
                case { Name: "code-block" }:
                    var id = node.GetAttributeValue("id", string.Empty);
                    var hideOutput = node.GetAttributeValue("data-hide-output", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
                    var content = node.InnerHtml;
                    Hub.Post(new SubmitCodeRequest(content) { ViewId = id }, o => o.WithTarget(kernelAddress.Value));
                    if (!hideOutput)
                        RenderCodeBlock(builder, id, ref sequence);
                    break;
                case { Name: "layout-area" }:
                    //var divId = node.GetAttributeValue("id", string.Empty);
                    var address = node.GetAttributeValue("data-address", "false");
                    var area = node.GetAttributeValue("data-area", "false");
                    var areaId = node.GetAttributeValue("data-id", "false");
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

    private void RenderLayoutArea(RenderTreeBuilder builder, string address, string area, string areaId, ref int sequence)
    {
        builder.OpenComponent<LayoutAreaView>(sequence++);
        builder.AddAttribute(sequence++, nameof(LayoutAreaView.ViewModel), new LayoutAreaControl((Address)address, new LayoutAreaReference(area) { Id = areaId }));
        builder.CloseComponent();
    }

    private void RenderCodeBlock(RenderTreeBuilder builder, string id, ref int sequence)
    {
        builder.OpenComponent<LayoutAreaView>(sequence++);
        builder.AddAttribute(sequence++, nameof(LayoutAreaView.ViewModel), new LayoutAreaControl(kernelAddress, new LayoutAreaReference(id)));
        builder.CloseComponent();
    }


    public void Dispose()
    {
        //if (kernelAddress.IsValueCreated)
        //    Hub.Post(new DisposeRequest(), o => o.WithTarget(kernelAddress.Value));
    }
}
