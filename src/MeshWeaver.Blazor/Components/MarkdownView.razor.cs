using Markdig;
using MarkdownExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Blazor.Components;

public partial class MarkdownView
{
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
                // TODO V10: Collection would not be good here. (25.01.2025, Roland Bürgi)
                var pipeline = MarkdownExtensions.CreateMarkdownPipeline(Stream.Owner, Stream.Owner);
                var document = Markdig.Markdown.Parse(markdown, pipeline);
                Html = document.ToHtml(pipeline);
                RequestStateChange();
            }))
        );
    }




    private string ComponentContainerId(string id) => $"component-container-{id}";

}
