using System.Collections;
using MeshWeaver.Layout;
using MeshWeaver.Reflection;

namespace MeshWeaver.Blazor.Components;

public abstract class ListBase<TViewModel, TView> : FormComponentBase<TViewModel, TView, ListBase<TViewModel, TView>.Option>
    where TViewModel : UiControl, IListControl
    where TView : ListBase<TViewModel, TView>
{
    public record Option(object Item, string Text, string ItemString, Type ItemType);

    protected IReadOnlyCollection<Option> Options { get; set; } = [];


    protected override void BindData()
    {
        base.BindData();
        DataBind(
            ViewModel.Options,
            x => x.Options,
            o =>
                (o as IEnumerable)?.Cast<Layout.Option>().Select(option =>
                    new Option(option.GetItem(), option.Text, MapToString(option.GetItem(), option.GetItemType()), option.GetItemType()))
                .ToArray()
        );
    }

    protected override Func<object, Option> ConversionToValue =>
        value =>
        {
            if (Options is null)
                return null;
            var itemType = Options.FirstOrDefault()?.ItemType;
            if (itemType == null)
                return null;

            var mapToString = MapToString(value, itemType);
            return Options.FirstOrDefault(x =>
                    x.ItemString == mapToString);

        };


    private static string MapToString(object instance, Type itemType) =>
        instance == null || IsDefault((dynamic)instance)
            ? GetDefault(itemType)
            : instance.ToString();

    private static string GetDefault(Type itemType)
    {
        if (itemType == typeof(string) ||itemType.IsNullableGeneric())
            return null;
        return Activator.CreateInstance(itemType)!.ToString();
    }

    private static bool IsDefault<T>(T instance) => instance.Equals(default(T));

    protected override object ConvertToData(Option value) => value?.Item;
    protected override bool NeedsUpdate(Option value)
    {
        if (Value is null)
            return value is not null;
        return Value.ItemString != value?.ItemString;
    }
}
