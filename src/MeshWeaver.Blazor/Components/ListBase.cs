using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Pointer;
using MeshWeaver.Data;
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
        DataBind(
            ViewModel.Options,
            x => x.Options,
            ConvertOptions
        );
        base.BindData();
    }

    private object? lastParsedOptions;
    private IReadOnlyCollection<Option> ConvertOptions(object options, IReadOnlyCollection<Option> defaultValue)
    {
        if (options is JsonElement je)
            options = je.Deserialize<IReadOnlyCollection<object>>(Hub.JsonSerializerOptions);
        if(options is IReadOnlyCollection<object> optionsEnum && lastParsedOptions is IReadOnlyCollection<object> lastOptionsEnumerable && optionsEnum.Count == lastOptionsEnumerable.Count && optionsEnum.Zip(lastOptionsEnumerable, (x,y) => x.Equals(y)).All(x => x))
            return Options;
        lastParsedOptions = options;
        var ret = (options as IEnumerable)?.Cast<LayoutOption>().Select(option =>
                new Option(option.GetItem(), option.Text, OptionsExtension.MapToString(option.GetItem(), option.GetItemType()), option.GetItemType()))
            .ToArray();

        if (Model is not null && ret?.FirstOrDefault() is { } o && ViewModel.Data is JsonPointerReference reference)
        {
            var el = JsonPointer.Parse($"{DataContext}/{reference.Pointer}").Evaluate(Stream.Current.Value);
            if (el.HasValue)
            {
                var val = el.Value.Deserialize<object>();
                if (val is not null)
                {
                    var mapToString = OptionsExtension.MapToString(val, o.ItemType);
                    var option = ret.FirstOrDefault(x => x.ItemString == mapToString);
                    SetValue(option);
                }
            }

        }
        else
        {
            if (ViewModel.Data is JsonPointerReference p)
            {
                var pointer = JsonPointer.Parse($"{DataContext}/{p.Pointer}");
                Options = ret;
                SetValue(ConversionToValue(pointer.Evaluate(Stream.Current.Value), ret?.FirstOrDefault()!));

            }

        }
        return ret ?? [];
    }


    protected override Option ConversionToValue(object value, Option defaultValue)
        {
            if (Options is null)
                return null!;
            var itemType = Options.FirstOrDefault()?.ItemType;
            if (itemType == null)
                return null!;

            var mapToString = OptionsExtension.MapToString(value, itemType);
            return Options.FirstOrDefault(x =>
                    x.ItemString == mapToString);

        }


    protected override void UpdatePointer(object value, JsonPointerReference reference)
    {
        if (value is Option o)
            value = o.Item;
        base.UpdatePointer(value, reference);
    }

    protected override object ConvertToData(Option value) => value?.Item;
    protected override bool NeedsUpdate(Option value)
    {
        if (Value is null)
            return value is not null;
        return Value.ItemString != value?.ItemString;
    }
}
