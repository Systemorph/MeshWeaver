using Markdig;
using MeshWeaver.Layout.Markdown;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor;

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
                var layoutAreaExtension = new LayoutAreaMarkdownExtension(Stream.Hub);
                var pipeline = new MarkdownPipelineBuilder()
                    .UseAdvancedExtensions()
                    .UseEmojiAndSmiley()
                    .Use(layoutAreaExtension)
                    .Use(new ImgPathMarkdownExtension(path => ToStaticHref(path, Stream.Owner)))
                    .Build();
                var newHtml = Markdown.ToHtml(markdown, pipeline);
                var newLayoutComponents =
                    layoutAreaExtension
                        .MarkdownParser
                        .Areas
                        .Select(component => new LayoutAreaControl(Stream.Owner, component.Reference) { Id = component.DivId })
                        .ToArray();
                if (newHtml == Html && LayoutAreaComponents.SequenceEqual(newLayoutComponents))
                    return;
                Html = newHtml;
                LayoutAreaComponents = newLayoutComponents;
                markdownChanged = true;
                RequestStateChange();
            }))
        );
    }

    public string ToStaticHref(string url,  object address)
        => $"static/{address}/{url}";

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
