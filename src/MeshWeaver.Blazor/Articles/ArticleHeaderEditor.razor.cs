using MeshWeaver.Articles;
using MeshWeaver.Layout.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MeshWeaver.Blazor.Articles;

public partial class ArticleHeaderEditor
{
    private Task SaveAsync()
    {
        return Task.CompletedTask;

    }

    private Task DoneAsync(MouseEventArgs arg)
    {
        return SwitchModeAsync(ArticleDisplayMode.Display);
    }
}
