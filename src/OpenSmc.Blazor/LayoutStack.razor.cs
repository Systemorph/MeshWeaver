using System.Text.Json;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;

namespace OpenSmc.Blazor;

public partial class LayoutStack
{
    private IReadOnlyCollection<(string Area, UiControl ViewModel)> Areas { get; set; } = Array.Empty<(string Area, UiControl ViewModel)>();
    protected override Task OnInitializedAsync()
    {
        Disposables.Add(Stream.Subscribe(item => InvokeAsync(() => Render(item)))); 
        return base.OnInitializedAsync();
    }

    private void Render(ChangeItem<JsonElement> item)
    {
        var newAreas = ViewModel.Areas.Select(a => (Area:a,ViewModel:GetControl(item, a)))
            .Where(x => x.ViewModel != null).ToArray();
        if (Areas.Count == newAreas.Length && Areas.Zip(newAreas, (x, y) => x.Equals(y)).All(x => x))
            return;
        Areas = newAreas;
        StateHasChanged();
    }
}
