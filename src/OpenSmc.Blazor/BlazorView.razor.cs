using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;

namespace OpenSmc.Blazor
{
    public partial class BlazorView<TViewModel> : IDisposable
    {
        protected override void OnParametersSet()
        {
            base.OnParametersSet();
            BindDataContext();
        }

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

        protected object DataContext { get; set; }

        public void BindDataContext()
        {
            if (ViewModel is UiControl control)
                if (control.DataContext is WorkspaceReference reference)
                    Disposables.Add(Stream
                        .Reduce(reference)
                        .Subscribe(v =>
                        {
                            DataContext = v;
                            InvokeAsync(StateHasChanged);
                        }));
                else
                    DataContext = control.DataContext;
        }

        protected List<IDisposable> Disposables { get; } = new();

        public void Dispose()
        {
            foreach (var d in Disposables)
            {
                d.Dispose();
            }
        }

        protected object GetControl(ChangeItem<JsonElement> item, string area)
        {
            return item.Value.TryGetProperty(LayoutAreaReference.Areas, out var controls) &&
                   controls.TryGetProperty(area, out var node)
                ? node.Deserialize<object>(Stream.Hub.JsonSerializerOptions)
                : null;
        }

        protected virtual void DataBind<T>(object value, Action<T> bindingAction)
        {
            Disposables.Add(GetObservable<T>(value).Subscribe(bindingAction));
        }


        protected virtual IObservable<T> GetObservable<T>(object value)
        {
            if (value is null)
                return Observable.Empty<T>();
            if (value is IObservable<T> observable)
                return observable;
            if (value is T t)
                return Observable.Return(t);
            if (value is WorkspaceReference reference)
                return Stream.Reduce(reference).Select(ConvertTo<T>);
            // TODO V10: Should we add more ways to convert? Converting to primitives? (11.06.2024, Roland Bürgi)
            throw new InvalidOperationException($"Cannot bind to {value.GetType().Name}");
        }

        private T ConvertTo<T>(IChangeItem changeItem)
        {
            var value = changeItem.Value;
            if (value is JsonNode node)
                return node.Deserialize<T>(Stream.Hub.JsonSerializerOptions);
            if (value is T t)
                return t;
            throw new InvalidOperationException($"Cannot convert to {typeof(T).Name}");
        }

    }
}
