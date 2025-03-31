using MeshWeaver.Articles;
using MeshWeaver.Layout.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace MeshWeaver.Blazor.Articles;

public partial class ArticleHeaderDisplay
{
    [Parameter] public ModelParameter<Article> Model { get; set; }
    private bool isEditMenuOpen;
    
    private string Name
    {
        get => Model?.Element.Name;
        set => Model?.Update(a => a with { Name = value });
    }

    private string Collection
    {
        get => Model?.Element.Collection;
        set => Model?.Update(a => a with { Collection = value });
    }

    private string Title
    {
        get => Model?.Element.Title;
        set => Model?.Update(a => a with { Title = value });
    }

    private List<string> Tags
    {
        get => Model?.Element.Tags;
        set => Model?.Update(a => a with { Tags = value });
    }

    private string Thumbnail
    {
        get => Model?.Element.Thumbnail;
        set => Model?.Update(a => a with { Thumbnail = value });
    }

    private string Abstract
    {
        get => Model?.Element.Abstract;
        set => Model?.Update(a => a with { Abstract = value });
    }

    private IReadOnlyCollection<Author> Authors
    {
        get => Model?.Element.AuthorDetails;
    }

    private DateTime? Published
    {
        get => Model?.Element.Published;
        set => Model?.Update(a => a with { Published = value });
    }

    private DateTime? LastUpdated
    {
        get => Model?.Element.LastUpdated;
    }

    private string Html
    {
        get => Model?.Element.PrerenderedHtml;
    }

    private string VideoUrl
    {
        get => Model?.Element.VideoUrl;
        set => Model?.Update(a => a with { VideoUrl = value });
    }
    private string EditLink => $"/article/edit/{Collection}/{Name}";
    private string ThumbnailPath => $"/static/{Collection}/{Thumbnail}";
    private string GetEmbedUrl(string videoUrl)
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

}
