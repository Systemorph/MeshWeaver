using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.DataBinding;

namespace MeshWeaver.Blazor.Components;

public abstract class FormComponentBase<TViewModel, TView, TValue> : BlazorView<TViewModel, TView>
    where TViewModel : UiControl, IFormControl
    where TView : FormComponentBase<TViewModel, TView, TValue>
{
    private TValue? data;

    public const string Edit = nameof(Edit);
    protected string? Label { get; set; }
    protected int ImmediateDelay { get; set; }
    private JsonPointerReference? DataPointer { get; set; }

    protected bool AutoFocus { get; set; }
    protected bool Immediate { get; set; }
    protected Icon? IconStart { get; set; }
    protected Icon? IconEnd { get; set; }
    protected string? Placeholder { get; set; }
    protected bool Disabled { get; set; }
    protected bool Readonly { get; set; }
    protected bool Required { get; set; }


    private Subject<TValue>? valueUpdateSubject;

    protected TValue? Value
    {
        get => data;
        set
        {

            var needsUpdate = !EqualityComparer<TValue>.Default.Equals(this.data, value);
            this.data = value;
            if (needsUpdate)
                if (DataPointer is not null && this.data is not null) UpdatePointer(this.data, DataPointer);
        }
    }

    protected void SetValue(TValue? v)
        => this.data = v;

    private const int DebounceWindow = 20;
    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Label, x => x.Label);
        DataBind(ViewModel.Placeholder, x => x.Placeholder);
        DataBind(ViewModel.Disabled, x => x.Disabled);
        DataBind(ViewModel.AutoFocus, x => x.AutoFocus);
        DataBind(ViewModel.Immediate, x => x.Immediate);
        DataBind(ViewModel.ImmediateDelay, x => x.ImmediateDelay);
        DataBind(ViewModel.IconStart, x => x.IconStart);
        DataBind(ViewModel.IconEnd, x => x.IconEnd);
        DataBind(ViewModel.Readonly, x => x.Readonly);
        DataBind(ViewModel.Required, x => x.Required);

        DataPointer = ViewModel.Data as JsonPointerReference;
        valueUpdateSubject = new();

        AddBinding(valueUpdateSubject
            .Debounce(TimeSpan.FromMilliseconds(DebounceWindow))
            .DistinctUntilChanged()
            .Skip(1)
            .Subscribe(x => { if (DataPointer is not null) UpdatePointer(ConvertToData(x)!, DataPointer); })
        );
        DataBind(ViewModel.Data, x => x.data, ConversionToValue!);
    }

    protected virtual TValue? ConversionToValue(object v, TValue defaultValue)
    {
        if (v is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.Undefined
                ? default
                : je.Deserialize<TValue>(Stream!.Hub.JsonSerializerOptions);
        }

        // Handle specific conversions
        if (v is string stringValue && typeof(TValue) != typeof(string))
        {
            var targetType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);

            try
            {
                // Handle common numeric types
                if (targetType == typeof(int))
                    return (TValue)(object)int.Parse(stringValue);
                if (targetType == typeof(double))
                    return (TValue)(object)double.Parse(stringValue);
                if (targetType == typeof(decimal))
                    return (TValue)(object)decimal.Parse(stringValue);
                if (targetType == typeof(float))
                    return (TValue)(object)float.Parse(stringValue);
                if (targetType == typeof(long))
                    return (TValue)(object)long.Parse(stringValue);

                // Handle boolean
                if (targetType == typeof(bool))
                    return (TValue)(object)bool.Parse(stringValue);

                // Handle date/time
                if (targetType == typeof(DateTime))
                    return (TValue)(object)DateTime.Parse(stringValue);

                // Handle enums
                if (targetType.IsEnum)
                    return (TValue)Enum.Parse(targetType, stringValue);

                // Handle GUID
                if (targetType == typeof(Guid))
                    return (TValue)(object)Guid.Parse(stringValue);
            }
            catch
            {
                // If conversion fails, return default value for the type
                return defaultValue;
            }
        }
        // Check if direct type assignment is possible
        if (v is TValue typedValue)
        {
            return typedValue;
        }

        try
        {
            return (TValue)Convert.ChangeType(v, typeof(TValue));
        }
        catch
        {
            // If conversion fails, return default value
            return defaultValue;
        }

    }

    protected virtual object? ConvertToData(TValue v) => v;

    protected virtual bool NeedsUpdate(TValue v)
    {
        return !Equals(data, v);
    }


}
