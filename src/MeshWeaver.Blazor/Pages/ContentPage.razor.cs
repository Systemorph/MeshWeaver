using System.Composition;
using MeshWeaver.Articles;
using MeshWeaver.Layout.Views;
using MeshWeaver.Layout;
using MeshWeaver.Blazor.Infrastructure;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages;

public partial class ContentPage : IDisposable
{
    private Stream Content { get; set; }
    private string ContentType { get; set; }
    IDisposable ArticleStreamSubscription { get; set; }
    [Inject] private PortalApplication PortalApplication { get; set; }
    private UiControl RootControl { get; set; }
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        var collection = ArticleService.GetCollection(Collection);
        if (collection is null || string.IsNullOrEmpty(Path))
            return;

        ContentType = collection.GetContentType(Path);
        if(ContentType == "text/markdown")
            ArticleStreamSubscription ??= PortalApplication.Hub
                .RenderArticle(Collection, Path)
                .Subscribe(a =>
                {
                    RootControl = a as UiControl;
                    InvokeAsync(StateHasChanged);
                });
        else
        {
            Content = await collection.GetContentAsync(Path);
        }

    }
    private byte[] ReadStream(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private string ReadStreamAsString(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private MarkdownControl ConvertToMarkdown(object uiControl)
    {
        return uiControl switch
        {
            MarkdownControl markdownControl => markdownControl,
            ArticleControl article => new(null) { Html = article.Html },

            _ => new MarkdownControl($"Could not convert control: {uiControl}")
        };
    }

    public void Dispose()
    {
        Content?.Dispose();
        ArticleStreamSubscription?.Dispose();
    }
}
