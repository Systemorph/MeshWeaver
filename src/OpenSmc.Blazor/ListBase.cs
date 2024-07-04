using OpenSmc.Data;
using OpenSmc.Layout;

namespace OpenSmc.Blazor;

public abstract class ListBase<TViewModel> : BlazorView<TViewModel>
    where TViewModel : UiControl, IListControl
{
    protected record Option(object Item, string Text, string ItemString);

    protected IReadOnlyCollection<Option> Options { get; set; }

    protected Option Selected
    {
        get => selected;
        set
        {
            var needsUpdate = selected?.ItemString != value?.ItemString;
            selected = value;
            if (needsUpdate)
                UpdatePointer(selected?.Item, Pointer);
        }
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        DataBind<IEnumerable<Layout.Option>>(ViewModel.Options, BindOptions);
        DataBind<object>(ViewModel.Data, UpdateSelection);
        Pointer = ViewModel.Data as JsonPointerReference;
    }

    private void UpdateSelection(object selection)
    {
        current = MapToString(selection);
        Selected = Options.FirstOrDefault(x => x.ItemString == current);
    }

    private void BindOptions(IEnumerable<Layout.Option> enumerable)
    {
        Options = enumerable.Select
            (x =>
                new Option(x.GetItem(), x.Text, MapToString(x.GetItem()))
            )
            .ToArray();
    }

    private static string MapToString(object instance) =>
        instance == null || IsDefault((dynamic)instance)
            ? null
            : instance.ToString();
    private static bool IsDefault<T>(T instance) => instance.Equals(default(T));

    private JsonPointerReference Pointer { get; set; }

    private string current;
    private Option selected;
}
