using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;

namespace OpenSmc.Blazor
{
    public partial class LayoutStack
    {
        private IReadOnlyCollection<(string Area, object ViewModel)> Areas { get; set; } = Array.Empty<(string Area, object ViewModel)>();
        protected override Task OnInitializedAsync()
        {
            Disposables.Add(Stream.Subscribe(item => InvokeAsync(() => Render(item)))); 
            return base.OnInitializedAsync();
        }

        private void Render(ChangeItem<EntityStore> item)
        {
            var dictionary = item.Value.Collections.GetValueOrDefault(LayoutAreaReference.CollectionName);
            if (dictionary != null)
            {
                var newAreas = ViewModel.Areas.Select(a => (Area:a, ViewModel:dictionary.Instances.GetValueOrDefault(a)))
                    .Where(x => x.ViewModel != null).ToArray();
                if (Areas.Count == newAreas.Length && Areas.Zip(newAreas, (x, y) => x.Equals(y)).All(x => x))
                    return;
                Areas = newAreas;
                StateHasChanged();
            }
        }
    }
}
