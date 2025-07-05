using MeshWeaver.ContentCollections;
using MeshWeaver.Layout.Client;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Articles;

public abstract class ArticleHeaderBase : ComponentBase
{
    [Parameter] public ModelParameter<Article>? Model { get; set; }

    protected string? Name
    {
        get => Model?.Element.Name;
        set => Model?.Update(a => a with { Name = value! });
    }

    protected string? CollectionName
    {
        get => Model?.Element.Collection;
        set => Model?.Update(a => a with { Collection = value! });
    }

    protected string? Title
    {
        get => Model?.Element.Title;
        set => Model?.Update(a => a with { Title = value! });
    }

    protected List<string>? Tags
    {
        get => Model?.Element.Tags;
        set => Model?.Update(a => a with { Tags = value! });
    }

    protected string? Thumbnail
    {
        get => Model?.Element.Thumbnail;
        set => Model?.Update(a => a with { Thumbnail = value! });
    }

    protected string? Abstract
    {
        get => Model?.Element.Abstract;
        set => Model?.Update(a => a with { Abstract = value! });
    }

    protected IReadOnlyCollection<Author>? Authors
    {
        get => Model?.Element.AuthorDetails;
    }

    protected DateTime? Published
    {
        get => Model?.Element.Published;
        set => Model?.Update(a => a with { Published = value });
    }

    protected DateTime? LastUpdated
    {
        get => Model?.Element.LastUpdated;
    }

    protected string? Html
    {
        get => Model?.Element.PrerenderedHtml;
    }

    protected string? VideoUrl
    {
        get => Model?.Element.VideoUrl;
        set => Model?.Update(a => a with { VideoUrl = value! });
    }
    protected string EditLink => $"/article/edit/{CollectionName}/{Name}";
    protected string ThumbnailPath => $"/static/{CollectionName}/{Thumbnail}";
    protected string GetEmbedUrl(string videoUrl)
    {
        if (videoUrl.Contains("youtube.com/watch"))
        {
            var videoId = videoUrl.Split("v=")[1];
            var ampersandPosition = videoId.IndexOf('&');
            if (ampersandPosition != -1)
            {
                videoId = videoId.Substring(0, ampersandPosition);
            }
            return $"https://www.youtube.com/embed/{videoId}";
        }
        return videoUrl;
    }
    [Parameter]
    public EventCallback<ArticleDisplayMode> DisplayModeChanged { get; set; }

    protected Task SwitchModeAsync(ArticleDisplayMode mode)
        => DisplayModeChanged.InvokeAsync(mode);
}
