using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

public abstract class ListBase<TViewModel, TView> : FormComponentBase<TViewModel, TView, ListBase<TViewModel, TView>.Option>
    where TViewModel : UiControl, IListControl
    where TView : ListBase<TViewModel, TView>
{
    public record Option(object Item, string Text, string ItemString);

    protected IReadOnlyCollection<Option> Options { get; set; } = [];


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
    }

    protected override Func<object, Option> ConversionToValue =>
        value =>
        {
            var mapToString = MapToString(value);
            return Options.FirstOrDefault(x =>
                x.ItemString == mapToString);
        };


    private static string MapToString(object instance) =>
        instance == null || IsDefault((dynamic)instance)
            ? null
            : instance.ToString();
    private static bool IsDefault<T>(T instance) => instance.Equals(default(T));

    protected override object ConvertToData(Option value) => value?.Item;
    protected override bool NeedsUpdate(Option value)
    {
        if (Value is null)
            return value is not null;
        return Value.ItemString != value?.ItemString;
    }
}
