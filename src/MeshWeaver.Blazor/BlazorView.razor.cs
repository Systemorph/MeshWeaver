using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using Microsoft.AspNetCore.Components;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

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

    protected string Style { get; set; }

    protected TViewModel RenderedViewModel { get; private set; }
    private ISynchronizationStream<JsonElement, LayoutAreaReference> stream;

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        BindDataAfterParameterReset();
    }

    protected virtual  void BindDataAfterParameterReset()
    {
        if (IsUpToDate())
            return;
        RenderedViewModel = ViewModel;
        stream = Stream;
        Logger.LogDebug("Preparing data bindings for area {Area}", Area);
        DisposeBindings();
        BindData();
        Logger.LogDebug("Finished data bindings for area {Area}", Area);
    }

    protected bool IsUpToDate() => RenderedViewModel != null && RenderedViewModel.Equals(ViewModel) && Equals(stream, Stream);

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
        else
        {
            property.SetValue(this, ConvertSingle(value, conversion));
        }
    }

    protected void RequestStateChange()
    {
        StateHasChanged();
    }
    protected IObservable<T> Convert<T>(object value,  Func<object, T> conversion = null)
    {
        if (value is JsonPointerReference reference)
        {
            return DataBind(reference, conversion);
        }

        return Observable.Return(ConvertSingle(value, conversion));
    }

    private IObservable<T> DataBind<T>(JsonPointerReference reference, Func<object, T> conversion) => 
        Stream.GetStream<object>(JsonPointer.Parse(reference.Pointer))
            .Select(conversion ?? (x => (T)x));

    protected string SubArea(string area)
        => $"{Area}/{area}";

    private T ConvertSingle<T>(object value, Func<object, T> conversion)
    {
        if(conversion != null)
            return conversion.Invoke(value);
        return value switch
        {
            null => default,
            JsonElement element => ConvertJson(element, conversion),
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

    protected void UpdatePointer<T>(T value, JsonPointerReference reference)
    {
        if (reference != null)
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
