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
        DataBind(
            ViewModel.Options,
            x => x.Options,
            o =>
                ((IEnumerable<Layout.Option>)o).Select(option =>
                    new Option(option.GetItem(), option.Text, MapToString(option.GetItem())))
                .ToArray()
        );
        DataBind(ViewModel.Data, x => x.Selected, o =>
        {
            var mapToString = MapToString(o);
            return Options.FirstOrDefault(x =>
                x.ItemString == mapToString);
        });
        Pointer = ViewModel.Data as JsonPointerReference;
    }


    private static string MapToString(object instance) =>
        instance == null || IsDefault((dynamic)instance)
            ? null
            : instance.ToString();
    private static bool IsDefault<T>(T instance) => instance.Equals(default(T));

    private JsonPointerReference Pointer { get; set; }

    private Option selected;
}
