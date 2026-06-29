using System.Collections;
using System.Text.Json;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using LayoutOption=MeshWeaver.Layout.Option;
using Option=MeshWeaver.Blazor.Components.OptionsExtension.Option;
namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Base class for list-based form components (drop-downs, radio groups, etc.) that bind a
/// selected <c>OptionsExtension.Option</c> to a JSON-pointer data source. Converts the
/// layout model's <c>IListControl.Options</c> into typed <c>Option</c> records and matches
/// the data-pointer value to the correct option by string key.
/// </summary>
/// <typeparam name="TViewModel">The layout control model type, constrained to <c>IListControl</c>.</typeparam>
/// <typeparam name="TView">The concrete Blazor view component deriving from this base.</typeparam>
public abstract class ListBase<TViewModel, TView> : FormComponentBase<TViewModel, TView, Option>
    where TViewModel : UiControl, IListControl
    where TView : ListBase<TViewModel, TView>
{

    /// <summary>The current set of selectable options, converted from the layout model and cached for change detection.</summary>
    protected IReadOnlyCollection<Option>? Options { get; set; } = [];


    /// <summary>
    /// Wires the <c>IListControl.Options</c> collection to the local <c>Options</c> property via
    /// <c>ConvertOptions</c> before delegating to the base binding pipeline, ensuring the option
    /// list is resolved before the selected-value binding runs.
    /// </summary>
    protected override void BindData()
    {
        DataBind(
            ViewModel.Options,
            x => x.Options,
            ConvertOptions!
        );
        base.BindData();
    }

    private object? lastParsedOptions;
    private IReadOnlyCollection<Option>? ConvertOptions(object options, IReadOnlyCollection<Option> defaultValue)
    {
        if (options is JsonElement je)
            options = je.Deserialize<IReadOnlyCollection<object>>(Hub.JsonSerializerOptions) ?? [];
        if(options is IReadOnlyCollection<object> optionsEnum && lastParsedOptions is IReadOnlyCollection<object> lastOptionsEnumerable && optionsEnum.Count == lastOptionsEnumerable.Count && optionsEnum.Zip(lastOptionsEnumerable, (x,y) => x.Equals(y)).All(x => x))
            return Options;
        lastParsedOptions = options;
        var ret = (options as IEnumerable)?.Cast<LayoutOption>().Select(option =>
                new Option(option.GetItem(), option.Text, OptionsExtension.MapToString(option.GetItem(), option.GetItemType()), option.GetItemType(), option.Icon))
            .ToArray();

        if (Model is not null && ret?.FirstOrDefault() is { } o && ViewModel.Data is JsonPointerReference reference)
        {
            var el = JsonPointer.Parse($"{DataContext}/{reference.Pointer}").Evaluate(Stream!.Current!.Value);
            if (el.HasValue && el.Value.ValueKind != JsonValueKind.Null)
            {
                var val = el.Value.Deserialize<object>();
                if (val is not null)
                {
                    var mapToString = OptionsExtension.MapToString(val, o.ItemType);
                    var option = ret.FirstOrDefault(x => x.ItemString == mapToString);
                    SetValue(option!);
                }
            }

        }
        else
        {
            if (ViewModel.Data is JsonPointerReference p)
            {
                var pointer = JsonPointer.Parse($"{DataContext}/{p.Pointer}");
                Options = ret ?? [];
                SetValue(ConversionToValue(pointer.Evaluate(Stream!.Current!.Value), ret?.FirstOrDefault()!));

            }

        }
        return ret ?? [];
    }


    /// <summary>
    /// Converts a raw data-source value (JSON element or boxed CLR object) to the matching
    /// <c>Option</c> from <c>Options</c> by comparing string representations via
    /// <c>OptionsExtension.MapToString</c>. Returns <c>null</c> when the option list is
    /// empty or no matching option is found.
    /// </summary>
    /// <param name="value">The raw value from the JSON-pointer data source.</param>
    /// <param name="defaultValue">Fallback option when no match can be found.</param>
    /// <returns>The matching <c>Option</c>, or <c>null</c> if options are not yet loaded.</returns>
    protected override Option? ConversionToValue(object? value, Option defaultValue)
        {
            if (Options is null)
                return null!;
            var itemType = Options.FirstOrDefault()?.ItemType;
            if (itemType == null)
                return null!;

            var mapToString = OptionsExtension.MapToString(value, itemType);
            return Options?.FirstOrDefault(x =>
                    x.ItemString == mapToString);

        }


    /// <summary>
    /// Unwraps an <c>Option</c> to its underlying <c>Item</c> before delegating to the base
    /// pointer update, so that the raw domain value (not the <c>Option</c> wrapper) is written
    /// to the JSON-pointer data source.
    /// </summary>
    /// <param name="value">The current form value — unwrapped if it is an <c>Option</c> instance.</param>
    /// <param name="reference">The JSON-pointer reference identifying the target field in the data stream.</param>
    protected override void UpdatePointer(object? value, JsonPointerReference reference)
    {
        if (value is Option o)
            value = o.Item;
        base.UpdatePointer(value, reference);
    }

    /// <summary>
    /// Extracts the raw domain item from the selected <c>Option</c> for writing to the data source.
    /// Returns <c>null</c> when no option is selected.
    /// </summary>
    /// <param name="value">The currently selected option, or <c>null</c> for no selection.</param>
    /// <returns>The underlying <c>Item</c> of the option, or <c>null</c>.</returns>
    protected override object? ConvertToData(Option? value) => value?.Item;

    /// <summary>
    /// Compares the incoming option's string key against the currently selected option to decide
    /// whether the component needs to re-render, avoiding unnecessary renders when the same item
    /// is re-selected.
    /// </summary>
    /// <param name="value">The candidate new option from the data stream.</param>
    /// <returns><c>true</c> if the selected item has changed; otherwise <c>false</c>.</returns>
    protected override bool NeedsUpdate(Option value)
    {
        if (Value is null)
            return value is not null;
        return Value.ItemString != value?.ItemString;
    }
}
