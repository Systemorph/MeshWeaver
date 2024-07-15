using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using Microsoft.AspNetCore.Components;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Blazor
{
    public partial class BlazorView<TViewModel> : IDisposable
        where TViewModel : UiControl
    {
        [Inject] private IMessageHub Hub { get; set; }
        protected override void OnParametersSet()
        {
            base.OnParametersSet();
            if(!IsUpToDate())
                BindData();
        }

        protected object Skin { get; set; }

        protected string Label { get; set; }

        protected string Class { get; set; }


        protected List<IDisposable> Disposables { get; } = new();

        public void Dispose()
        {
            foreach (var d in bindings.Concat(Disposables))
            {
                d.Dispose();
            }
        }


        private readonly List<IDisposable> bindings = new();
        protected virtual void DataBind<T>(object value, Action<T> bindingAction)
        {
            bindings.Add(GetObservable<T>(value).Subscribe(bindingAction));
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
                        patch,
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
            foreach (var d in bindings)
                d.Dispose();

            bindings.Clear();

            if (ViewModel != null)
            {
                DataBind<object>(ViewModel.Skin, x => Skin = x);
                DataBind<string>(ViewModel.Label, x => Label = x);
                DataBind<string>(ViewModel.Class, x => Class = x);
            }
        }

        protected virtual IObservable<T> GetObservable<T>(object value)
        {
            if (value is null)
                return Observable.Empty<T>();
            if (value is IObservable<T> observable)
                return observable;
            if (value is JsonPointerReference reference)
                return Stream.Where(x => !Hub.Address.Equals(x.ChangedBy))
                    .Select(x => Extract<T>(x, reference));
            if (value is T t)
                return Observable.Return(t);
            // TODO V10: Should we add more ways to convert? Converting to primitives? (11.06.2024, Roland Bürgi)
            throw new InvalidOperationException($"Cannot bind to {value.GetType().Name}");
        }

        private TResult Extract<TResult>(ChangeItem<JsonElement> changeItem, JsonPointerReference reference)
        {
            if (reference == null)
                return default;
            var pointer = JsonPointer.Parse(ViewModel.DataContext + reference.Pointer.TrimEnd('/'));
            var ret = pointer.Evaluate(changeItem.Value);
            return ret == null ? default : ret.Value.Deserialize<TResult>(Stream.Hub.JsonSerializerOptions);
        }

        protected void OnClick()
        {
            Stream.Hub.Post(new ClickedEvent(Area) { Reference = Stream.Reference, Owner = Stream.Owner }, o => o.WithTarget(Stream.Owner));
        }


        private IReadOnlyCollection<object> CurrentState { get; set; }
        protected virtual bool IsUpToDate()
        {
            var oldState = CurrentState ?? Array.Empty<object>();
            var current = GetState();
            if (oldState.Count == 0 && current.Count == 0)
                return true;
            return current.SequenceEqual(oldState );
        }

        protected virtual IReadOnlyCollection<object> GetState()
            => CurrentState = GetType()
                .GetProperties()
                .Where(p => p.HasAttribute<ParameterAttribute>())
                .Select(p => p.GetValue(this))
                .ToArray();
    }
}
