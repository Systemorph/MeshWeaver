using System.Text.Json;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;

namespace OpenSmc.Blazor;

public record RenderedArea(string Area, UiControl ViewModel);

public abstract class ContainerView<TViewModel> : BlazorView<TViewModel>
    where TViewModel : UiControl, IContainerControl
{
    protected IReadOnlyCollection<RenderedArea> Areas { get; set; } = Array.Empty<RenderedArea>();
    
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        Disposables.Add(Stream.Subscribe(item => InvokeAsync(() => Render(item)))); 
    }

    private void Render(ChangeItem<JsonElement> item)
    {
        var newAreas = ViewModel.Areas.Select(area => new RenderedArea(area,GetControl(item, area)))
            .Where(x => x.ViewModel != null).ToArray();
        if (Areas.Count == newAreas.Length && Areas.Zip(newAreas, (x, y) => x.Equals(y)).All(x => x))
            return;
        Areas = newAreas;
        StateHasChanged();
    }
}
