using MeshWeaver.Data;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor;

public abstract class ListBase<TViewModel, TView> : BlazorView<TViewModel, ListBase<TViewModel, TView>>
    where TViewModel : UiControl, IListControl
{
    protected record Option(object Item, string Text, string ItemString);

    protected IReadOnlyCollection<Option> Options { get; set; } = [];

    protected Option Selected
    {
        get => selected;
        set
        {
            var needsUpdate = selected != null && selected.ItemString != value?.ItemString;
            selected = value;
            if (needsUpdate)
                UpdatePointer(selected?.Item, Pointer);
        }
    }

    protected override void BindData()
    {
        base.BindData();
        DataBind<IEnumerable<Layout.Option>>(ViewModel.Options, BindOptions);
        DataBind<object>(ViewModel.Data, UpdateSelection);
        Pointer = ViewModel.Data as JsonPointerReference;
    }

    private bool UpdateSelection(object selection)
    {
        current = MapToString(selection);
        var newSelected = Options.FirstOrDefault(x => x.ItemString == current);
        if (Equals(Selected, newSelected))
            return false;
        Selected = newSelected;
        return true;
    }

    private bool BindOptions(IEnumerable<Layout.Option> enumerable)
    {
        
        var newOptions = enumerable.Select
            (x =>
                new Option(x.GetItem(), x.Text, MapToString(x.GetItem()))
            )
            .ToArray();

        if (Options.SequenceEqual(newOptions))
            return false;
        Options = newOptions;
        return true;
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
