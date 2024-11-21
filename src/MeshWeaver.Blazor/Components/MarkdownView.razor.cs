using Markdig;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Markdig.Syntax;
using MeshWeaver.Markdown;
using MarkdownExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Blazor.Components;

public partial class MarkdownView
{
    private ElementReference element;
    private IJSObjectReference htmlUtils;
    private IJSObjectReference highlight;
    private bool markdownChanged;

    private IReadOnlyList<LayoutAreaControl> LayoutAreaComponents { get; set; } = [];
    private string Html { get; set; }
    protected override void BindData()
    {
        base.BindData();
        RenderMarkdown();
    }

    private void RenderMarkdown()
    {
        AddBinding(Convert<string>(ViewModel.Data).Subscribe(
            markdown => InvokeAsync(() =>
            {
                var pipeline = MarkdownExtensions.CreateMarkdownPipeline(Stream.Owner);
                var document = Markdig.Markdown.Parse(markdown, pipeline);
                var newLayoutComponents =
                    document.Descendants<LayoutAreaComponentInfo>()
                        .Select(component => new LayoutAreaControl(Stream.Owner, component.Reference) { Id = component.DivId })
                        .ToArray();
                var newHtml = document.ToHtml(pipeline);
                if (newHtml == Html && LayoutAreaComponents.SequenceEqual(newLayoutComponents))
                    return;
                Html = newHtml;
                LayoutAreaComponents = newLayoutComponents;
                markdownChanged = true;
                RequestStateChange();
            }))
        );
    }



    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            htmlUtils = await JsRuntime.Import("htmlUtils.js");
            highlight = await JsRuntime.Import("highlight.js");
        }

        if (firstRender || markdownChanged)
        {
            markdownChanged = false;

            foreach (var component in LayoutAreaComponents)
            {
                await htmlUtils.InvokeVoidAsync("moveElementContents", ComponentContainerId(component.Id.ToString()), component.Id.ToString());
            }

            await highlight.InvokeVoidAsync("highlightCode", element);
        }
    }

    private string ComponentContainerId(string id) => $"component-container-{id}";

}
