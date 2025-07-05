using MeshWeaver.ContentCollections;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Articles;

public partial class ArticleHeaderEditor
{
    [Inject] private IToastService ToastService { get; set; } = null!;
    [Inject] private IContentService ContentService { get; set; } = null!;

    private void SaveAsync()
    {
        try
        {
            Collection.SaveArticleAsync(Model!.Submit());
            ShowSuccess();
            Model!.Confirm();
        }
        catch(Exception ex)
        {
            ShowError(ex.Message);
            Model?.Reset();
        }

    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Collection = ContentService.GetCollection(CollectionName!)!;
    }
    private ContentCollection Collection { get; set; } = null!;

    private void Reset()
    {
        Model.Reset();
        InvokeAsync(StateHasChanged);
    }

    private void ShowSuccess()
    {
        var message = "Saved successfully";
        ToastService.ShowToast(ToastIntent.Success, message);
    }

    private void ShowError(string message)
    {
        ToastService.ShowToast(ToastIntent.Error, $"Saving failed: {message}");
    }


    private Task DoneAsync(MouseEventArgs arg)
    {
        return SwitchModeAsync(ArticleDisplayMode.Display);
    }
}
