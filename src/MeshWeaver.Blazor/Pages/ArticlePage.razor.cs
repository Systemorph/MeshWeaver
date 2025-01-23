using MeshWeaver.Articles;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Pages;

public partial class ArticlePage(IArticleService meshCatalog, IMessageHub hub)
{
    private string Prerendered => Article?.PrerenderedHtml;
    private MeshArticle Article { get; set; }
    private UiControl ArticleControl { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        ArticleControl = GetArticleControl(Article);
        //Stream = hub.GetWorkspace().GetRemoteStream<LayoutAreaReference,JsonPointerReference>(new KernelAddress(), new LayoutAreaReference("Article"))
    }

    private UiControl GetArticleControl(MeshArticle article)
    {
        if(article is null)
            return new MarkdownControl($":x: **Article not found**");

        if(article.Content is null)
            return new MarkdownControl($":x: **Article has no content**");

        return new MarkdownControl(article.Content);
    }
}
