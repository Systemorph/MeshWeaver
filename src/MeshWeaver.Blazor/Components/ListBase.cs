using System.Collections;
using MeshWeaver.Layout;
using LayoutOption=MeshWeaver.Layout.Option;
using Option=MeshWeaver.Blazor.Components.OptionsExtension.Option;
namespace MeshWeaver.Blazor.Components;

public abstract class ListBase<TViewModel, TView> : FormComponentBase<TViewModel, TView, Option>
    where TViewModel : UiControl, IListControl
    where TView : ListBase<TViewModel, TView>
{

    protected IReadOnlyCollection<Option> Options { get; set; } = [];


    protected override void BindData()
    {
        base.BindData();
        DataBind(
            ViewModel.Options,
            x => x.Options,
            o =>
                (o as IEnumerable)?.Cast<LayoutOption>().Select(option =>
                    new Option(option.GetItem(), option.Text, OptionsExtension.MapToString(option.GetItem(), option.GetItemType()), option.GetItemType()))
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

            var mapToString = OptionsExtension.MapToString(value, itemType);
            return Options.FirstOrDefault(x =>
                    x.ItemString == mapToString);

        };



    protected override object ConvertToData(Option value) => value?.Item;
    protected override bool NeedsUpdate(Option value)
    {
        if (Value is null)
            return value is not null;
        return Value.ItemString != value?.ItemString;
    }
}
