using MeshWeaver.Activities;
using MeshWeaver.Articles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Articles;

public partial class ArticleView
{
    private ModelParameter<Article> data;
    private ActivityLog Log { get; set; }
    [Inject] private IToastService ToastService { get; set; }

    protected override void BindData()
    {
        DataBind(
            new JsonPointerReference(ViewModel.DataContext),
            x => x.data,
            jsonObject => Convert((Article)jsonObject)
        );
    }

    private async void Submit(EditContext context)
    {
        var log = await Stream.SubmitModel(Model);
        if (log.Status == ActivityStatus.Succeeded)
        {
            Log = null;
            ShowSuccess();
            Reset();
        }
        else
        {
            Log = log;
            ShowError();
        }
    }
    private ModelParameter<Article> ArticleModel { get; set; }
    private ModelParameter Convert(Article article)
    {
        if (article == null)
            return null;
        var ret = ArticleModel = new ModelParameter<Article>(article, (_,_) => throw new NotImplementedException());
        ret.ElementChanged += OnModelChanged;
        return ret;
    }

    private void Reset()
    {
        data.Reset();
        InvokeAsync(StateHasChanged);
    }

    private void ShowSuccess()
    {
        var message = "Saved successfully";
        ToastService.ShowToast(ToastIntent.Success, message);
    }

    private void ShowError()
    {
        var message = "Saving failed";
        ToastService.ShowToast(ToastIntent.Error, message);
    }

    public override ValueTask DisposeAsync()
    {
        if (data != null)
            data.ElementChanged -= OnModelChanged;
        return base.DisposeAsync();
    }

    private void OnModelChanged(object sender, Article e)
    {
        InvokeAsync(StateHasChanged);
    }
    
    private readonly KernelAddress KernelAddress = new();
    private string Name
    {
        get => ArticleModel?.Element.Name;
        set => ArticleModel?.Update(a => a with{Name = value});
    }

    private string Collection
    {
        get => ArticleModel?.Element.Collection;
        set => ArticleModel?.Update(a => a with { Collection = value });
    }

    private string Title
    {
        get => ArticleModel?.Element.Title;
        set => ArticleModel?.Update(a => a with { Title = value });
    }

    private List<string> Tags
    {
        get => ArticleModel?.Element.Tags;
        set => ArticleModel?.Update(a => a with { Tags = value });
    }

    private string Thumbnail
    {
        get => ArticleModel?.Element.Thumbnail;
        set => ArticleModel?.Update(a => a with { Thumbnail = value });
    }

    private string Abstract
    {
        get => ArticleModel?.Element.Abstract;
        set => ArticleModel?.Update(a => a with { Abstract = value });
    }

    private IReadOnlyCollection<Author> Authors
    {
        get => ArticleModel?.Element.AuthorDetails;
    }

    private DateTime? Published
    {
        get => ArticleModel?.Element.Published;
        set => ArticleModel?.Update(a => a with { Published = value });
    }

    private DateTime? LastUpdated
    {
        get => ArticleModel?.Element.LastUpdated;
    }

    private string Html
    {
        get => ArticleModel?.Element.PrerenderedHtml;
    }

    private string VideoUrl
    {
        get => ArticleModel?.Element.VideoUrl;
        set => ArticleModel?.Update(a => a with { VideoUrl = value });
    }

    private MarkdownControl MarkdownControl
    {
        get => new MarkdownControl(ArticleModel?.Element.Content){Html = ConvertHtml(ArticleModel?.Element.PrerenderedHtml)};
    }
    private string EditLink => $"/article/edit/{Collection}/{Name}";
    private string ThumbnailPath => $"/static/{Collection}/{Thumbnail}";


    private string ConvertHtml(object arg)
    {
        return arg?.ToString()!.Replace(ExecutableCodeBlockRenderer.KernelAddressPlaceholder, KernelAddress.ToString());
    }

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

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);

        if (firstRender)
        {
            if (ArticleModel?.Element.CodeSubmissions is not null && ArticleModel?.Element.CodeSubmissions.Any() == true)
            {
                foreach (var s in ArticleModel.Element.CodeSubmissions)
                    Hub.Post(s, o => o.WithTarget(KernelAddress));
            }

        }
    }


}
