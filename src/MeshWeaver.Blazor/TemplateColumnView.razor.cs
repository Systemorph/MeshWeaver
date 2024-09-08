using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
using System.Text.Json.Nodes;

namespace MeshWeaver.Blazor;

public partial class TemplateColumnView
{
    private string Title { get; set; }
    private string Align { get; set; }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        DataBind(Skin.Title, x => x.Title);
        DataBind(Skin.Align, x => x.Align);
    }


    private void RenderTemplateColumn(RenderTreeBuilder builder)
    {
        var column = ViewModel;

        builder.OpenComponent(0, typeof(TemplateColumn<>).MakeGenericType(typeof(JsonObject)));
        var index = 0;
        if (Skin.Title != null)
            builder.AddComponentParameter(++index, nameof(TemplateColumn<object>.Title), Title);
        if (Skin.Title != null)
            builder.AddComponentParameter(++index, nameof(TemplateColumn<object>.Align), Align);

        builder.CloseComponent();
    }

}
