using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using Microsoft.AspNetCore.Components;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices.JavaScript;

namespace MeshWeaver.Blazor;

public class BlazorView<TViewModel, TView> : ComponentBase, IDisposable
    where TViewModel : IUiControl
    where TView : BlazorView<TViewModel, TView>
{
    [Inject] private IMessageHub Hub { get; set; }
    [Inject] protected ILogger<TView> Logger { get; set; }


    [Parameter]
    public TViewModel ViewModel { get; set; }

    [Parameter]
    public ISynchronizationStream<JsonElement, LayoutAreaReference> Stream { get; set; }
    [Parameter]
    public string Area { get; set; }

    [CascadingParameter(Name = nameof(Model))]
    public ModelParameter Model { get; set; }

    protected string Style { get; set; }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        BindDataAfterParameterReset();
    }


    protected virtual void BindDataAfterParameterReset()
    {
        Logger.LogDebug("Preparing data bindings for area {Area}", Area);
        DisposeBindings();
        BindData();
        Logger.LogDebug("Finished data bindings for area {Area}", Area);
    }


    protected string Label { get; set; }

    protected string Class { get; set; }
    protected string Id { get; set; }

    protected List<IDisposable> Disposables { get; } = new();

    public virtual void Dispose()
    {
        Logger.LogDebug("Disposing area {Area}", Area);
        DisposeBindings();
        foreach (var d in Disposables)
            d.Dispose();
        Disposables.Clear();
    }


    protected void AddBinding(IDisposable binding)
    {
        bindings.Add(binding);
    }

    protected void DataBind<T>(object value, Expression<Func<TView, T>> propertySelector, Func<object, T> conversion = null)
    {
        var expr = propertySelector?.Body as MemberExpression;
        var property = expr?.Member as PropertyInfo;
        if (property == null)
            throw new ArgumentException("Expression needs to point to a property.");

        if (value is JsonPointerReference reference)
        {
            if (Model is not null)
                property.SetValue(this, ConvertSingle(GetValueFromModel(reference), conversion));
            else
                bindings.Add(DataBind(reference, conversion)
                    .Subscribe(v =>
                        {
                            Logger.LogTrace("Binding property {property} of {area}", property.Name, Area);
                            InvokeAsync(() =>
                            {
                                property.SetValue(this, v);
                                RequestStateChange();
                            });
                        }
                    )
                );

        }
        else
        {
            property.SetValue(this, ConvertSingle(value, conversion));
        }
    }

    protected void RequestStateChange()
    {
        StateHasChanged();
    }
    protected IObservable<T> Convert<T>(object value, Func<object, T> conversion = null)
    {
        if (value is JsonPointerReference reference)
        {
            if(Model != null)
                return Observable.Return(ConvertSingle(GetValueFromModel(reference), conversion));
            return DataBind(reference, conversion);
        }

        return Observable.Return(ConvertSingle(value, conversion));
    }

    private object GetValueFromModel(JsonPointerReference reference)
    {
        var pointer = JsonPointer.Parse(reference.Pointer);
        return pointer.Evaluate(Model.Element);
    }

    private IObservable<T> DataBind<T>(JsonPointerReference reference, Func<object, T> conversion) =>
        Stream.GetStream<object>(JsonPointer.Parse(reference.Pointer))
            .Select(conversion ?? (x => ConvertSingle<T>(x, null)));


    protected T GetDataBoundValue<T>(object value)
    {
        if (value is JsonPointerReference reference)
            return GetDataBoundValue<T>(reference);

        if (value is string stringValue && typeof(T).IsEnum)
            return (T)Enum.Parse(typeof(T), stringValue);

        // Use Convert.ChangeType for flexible conversion
        return (T)System.Convert.ChangeType(value, typeof(T));
    }
    private T GetDataBoundValue<T>(JsonPointerReference reference)
    {
        var ret = JsonPointer.Parse(reference.Pointer).Evaluate(Stream.Current.Value);
        if (ret == null)
            return default;
        return ret.Value.Deserialize<T>(Stream.Hub.JsonSerializerOptions);
    }

    protected string SubArea(string area)
        => $"{Area}/{area}";

    private T ConvertSingle<T>(object value, Func<object, T> conversion)
    {
        if (conversion != null)
            return conversion.Invoke(value);
        return value switch
        {
            null => default,
            JsonElement element => ConvertJson(element, conversion),
            JsonObject obj => ConvertJson<T>(obj, conversion),
            T t => t,
            string s => ConvertString<T>(s),
            _ => throw new InvalidOperationException($"Cannot convert {value} to {typeof(T)}")
        };
    }

    private T ConvertString<T>(string s)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType.IsEnum)
            return (T)Enum.Parse(targetType, s);
        return Type.GetTypeCode(targetType) switch
        {
            TypeCode.Int32 => (T)(object)int.Parse(s),
            TypeCode.Double => (T)(object)double.Parse(s),
            TypeCode.Single => (T)(object)float.Parse(s),
            TypeCode.Boolean => (T)(object)bool.Parse(s),
            TypeCode.Int64 => (T)(object)long.Parse(s),
            TypeCode.Int16 => (T)(object)short.Parse(s),
            TypeCode.Byte => (T)(object)byte.Parse(s),
            TypeCode.Char => (T)(object)char.Parse(s),
            _ => throw new InvalidOperationException($"Cannot convert {s} to {typeof(T)}")
        };

    }

    private T ConvertJson<T>(JsonElement? value, Func<object, T> conversion)
    {
        if (value == null)
            return default;
        if (conversion != null)
            return conversion(JsonSerializer.Deserialize<object>(value.Value.GetRawText(), Hub.JsonSerializerOptions));
        return JsonSerializer.Deserialize<T>(value.Value.GetRawText(), Hub.JsonSerializerOptions);
    }
    private T ConvertJson<T>(JsonObject value, Func<object, T> conversion)
    {
        if (value == null)
            return default;
        if (conversion != null)
            return conversion(value.Deserialize<object>(Hub.JsonSerializerOptions));
        return value.Deserialize<T>(Hub.JsonSerializerOptions);
    }

    protected void UpdatePointer<T>(T value, JsonPointerReference reference)
    {
        if (reference != null)
        {
            if (Model != null)
            {
                var patch = GetPatch(value, reference, Model.Element);
                if(patch != null)
                    Model.Update(patch);
            }

            else
                Stream.Update(ci =>
                {
                    var patch = GetPatch(value, reference, ci);

                    return new ChangeItem<JsonElement>(
                        Stream.Owner,
                        Stream.Reference,
                        patch?.Apply(ci) ?? ci,
                        Hub.Address,
                        new(() => patch),
                        Hub.Version
                    );
                });

        }
    }

    private JsonPatch GetPatch<T>(T value, JsonPointerReference reference, JsonElement current)
    {
        var pointer = JsonPointer.Parse(ViewModel.DataContext + reference.Pointer);

        var existing = pointer.Evaluate(current);
        if (value == null)
            return existing == null
                ? null
                : new JsonPatch(PatchOperation.Remove(pointer));

        var valueSerialized = JsonSerializer.SerializeToNode(value, Hub.JsonSerializerOptions);

        return existing == null
                ? new JsonPatch(PatchOperation.Add(pointer, valueSerialized))
                : new JsonPatch(PatchOperation.Replace(pointer, valueSerialized))
            ;
    }

    protected virtual void BindData()
    {

        if (ViewModel != null)
        {
            DataBind(ViewModel.Id, x => x.Id);
            DataBind(ViewModel.Label, x => x.Label);
            DataBind(ViewModel.Class, x => x.Class);
            DataBind(ViewModel.Style, x => x.Style);
        }
    }
    private readonly List<IDisposable> bindings = new();

    protected virtual void DisposeBindings()
    {
        foreach (var d in bindings)
            d.Dispose();
        bindings.Clear();
    }


    protected void OnClick()
    {
        Stream.Hub.Post(new ClickedEvent(Area) { Reference = Stream.Reference, Owner = Stream.Owner }, o => o.WithTarget(Stream.Owner));
    }



}
