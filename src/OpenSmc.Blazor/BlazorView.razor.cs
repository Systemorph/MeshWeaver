using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using Microsoft.AspNetCore.Components;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor
{
    public partial class BlazorView<TViewModel> : IDisposable
        where TViewModel : UiControl
    {
        [Inject] private IMessageHub Hub { get; set; }
        protected override void OnParametersSet()
        {
            ResetBindings();
            base.OnParametersSet();
            if (ViewModel != null)
            {
                DataBind<string>(ViewModel.Skin, x => Skin = x);
                DataBind<string>(ViewModel.Label, x => Label = x);
            }
        }

        protected string Skin { get; set; }

        protected string Label { get; set; }

        protected object BindProperty(object instance, string propertyName)
        {
            if(instance == null)
                return null;

            var type = instance.GetType();
            var property = type.GetProperty(propertyName);
            if(property == null)
                return null;
            return property.GetValue(instance, null);
        }



        protected List<IDisposable> Disposables { get; } = new();

        public void Dispose()
        {
            foreach (var d in bindings.Concat(Disposables))
            {
                d.Dispose();
            }
        }

        protected UiControl GetControl(ChangeItem<JsonElement> item, string area)
        {
            return item.Value.TryGetProperty(LayoutAreaReference.Areas, out var controls) 
                   && controls.TryGetProperty(JsonSerializer.Serialize(area), out var node)
                ? node.Deserialize<UiControl>(Stream.Hub.JsonSerializerOptions)
                : null;
        }

        private readonly List<IDisposable> bindings = new();
        protected virtual void DataBind<T>(object value, Action<T> bindingAction)
        {
            bindings.Add(GetObservable<T>(value).Subscribe(bindingAction));
        }
        protected void UpdatePointer<T>(T value, JsonPointerReference reference)
        {
            if (reference != null)
                Stream.Update(ci => new ChangeItem<JsonElement>(
                    Stream.Id,
                    Stream.Reference,
                    ApplyPatch(value, reference, ci),
                    Hub.Address,
                    Hub.Version
                ));
        }

        private JsonElement ApplyPatch<T>(T value, JsonPointerReference reference, JsonElement current)
        {
            var pointer = JsonPointer.Parse(reference.Pointer);

            var existing = pointer.Evaluate(current);
            if (value == null) 
                return existing == null 
                    ? current 
                    : new JsonPatch(PatchOperation.Remove(pointer)).Apply(current);

            var valueSerialized = JsonSerializer.SerializeToNode(value, Hub.JsonSerializerOptions);

            return existing == null 
                ? new JsonPatch(PatchOperation.Add(pointer, valueSerialized)).Apply(current) 
                : new JsonPatch(PatchOperation.Replace(pointer, valueSerialized)).Apply(current);
        }

        public void ResetBindings()
        {
            foreach (var d in bindings)
                d.Dispose();

            bindings.Clear();
        }

        protected virtual IObservable<T> GetObservable<T>(object value)
        {
            if (value is null)
                return Observable.Empty<T>();
            if (value is IObservable<T> observable)
                return observable;
            if (value is JsonPointerReference reference)
                return Stream.Where(x => !Hub.Address.Equals(x.ChangedBy)).Select(x => Extract<T>(x, reference));
            if (value is T t)
                return Observable.Return(t);
            // TODO V10: Should we add more ways to convert? Converting to primitives? (11.06.2024, Roland Bürgi)
            throw new InvalidOperationException($"Cannot bind to {value.GetType().Name}");
        }

        private TResult Extract<TResult>(ChangeItem<JsonElement> changeItem, JsonPointerReference reference)
        {
            if (reference == null)
                return default;
            var pointer = JsonPointer.Parse(reference.Pointer);
            var ret = pointer.Evaluate(changeItem.Value);
            return ret == null ? default : ret.Value.Deserialize<TResult>(Stream.Hub.JsonSerializerOptions);
        }

        private T ConvertTo<T>(IChangeItem changeItem)
        {
            var value = changeItem.Value;
            if (value == null)
                return default;
            if (value is JsonElement node)
                return node.Deserialize<T>(Stream.Hub.JsonSerializerOptions);
            if (value is T t)
                return t;
            throw new InvalidOperationException($"Cannot convert to {typeof(T).Name}");
        }

    }
}
