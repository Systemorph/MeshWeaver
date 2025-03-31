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
    private JsonPointerReference ArticlePointer { get; set; }
    protected override void BindData()
    {
        ArticlePointer = new JsonPointerReference(ViewModel.DataContext);
        DataBind(
            ArticlePointer,
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

    private MarkdownControl MarkdownControl
    {
        get => new MarkdownControl(ArticleModel?.Element.Content){Html = ConvertHtml(ArticleModel?.Element.PrerenderedHtml)};
    }


    private string ConvertHtml(object arg)
    {
        return arg?.ToString()!.Replace(ExecutableCodeBlockRenderer.KernelAddressPlaceholder, KernelAddress.ToString());
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


    private ArticleDisplayMode DisplayMode { get; set; }
    private Task ChangeDisplayMode(ArticleDisplayMode mode)
    {
        DisplayMode = mode;
        return InvokeAsync(StateHasChanged);
    }
}
