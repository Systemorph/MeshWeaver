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

namespace MeshWeaver.Blazor
{
    public class BlazorView<TViewModel, TView> : ComponentBase, IDisposable
        where TViewModel : UiControl
        where TView : BlazorView<TViewModel, TView>
    {
        [Inject] private IMessageHub Hub { get; set; }
        [Inject] private ILogger<TView> Logger { get; set; }

        [Parameter]
        public TViewModel ViewModel { get; set; }

        [Parameter]
        public ISynchronizationStream<JsonElement, LayoutAreaReference> Stream { get; set; }
        [Parameter]
        public string Area { get; set; }

        protected string Style { get; set; }

        private TViewModel viewModel;
        private ISynchronizationStream<JsonElement, LayoutAreaReference> stream;
        protected override void OnParametersSet()
        {
            base.OnParametersSet();
            if (Equals(viewModel, ViewModel) && Equals(stream, Stream))
                return;
            viewModel = ViewModel;
            stream = Stream;
            Logger.LogDebug("Preparing data bindings for area {Area}", Area);

            BindData();
        }

        protected string Label { get; set; }

        protected string Class { get; set; }
        protected string Id { get; set; }

        protected List<IDisposable> Disposables { get; } = new();

        public virtual void Dispose()
        {
            DisposeBindings();
            foreach (var d in Disposables)
                d.Dispose();
            Disposables.Clear();
        }


        private readonly List<IDisposable> bindings = new();
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
                bindings.Add(Convert(value, conversion)
                .Subscribe(v =>
                    {
                        Logger.LogTrace("Binding property {property} of {area}", property.Name, Area);
                        property.SetValue(this, v);
                        RequestStateChange();
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
            StartDebounceTimer();
        }
        private readonly Timer debounceTimer;

        public BlazorView()
        {
            debounceTimer = new(_ => InvokeAsync(StateHasChanged));
        }
        private void StartDebounceTimer()
        {
            debounceTimer.Change(100, Timeout.Infinite);
        }
        protected IObservable<T> Convert<T>(object value,  Func<object, T> conversion = null)
        {
            if (value is JsonPointerReference reference)
            {
                return DataBind(reference, conversion);
            }

            return Observable.Return(ConvertSingle(value, conversion));
        }

        private IObservable<T> DataBind<T>(JsonPointerReference reference, Func<object, T> conversion)
        {
            var pointer = JsonPointer.Parse(reference.Pointer);

            return Stream
                .Where(change => change.Patch == null || change.Patch.Operations.Any(p => p.Path.ToString().StartsWith(reference.Pointer)))
                .Select(change => ConvertJson(pointer.Evaluate(change.Value), conversion)
                );
        }

        protected string SubArea(string area)
        => $"{Area}/{area}";

        private T ConvertSingle<T>(object value, Func<object, T> conversion)
        {
            if (value == null)
                return default;
            if (value is JsonElement element)
                return ConvertJson(element, conversion);
            if (conversion != null)
                return conversion(value);
            if (value is T t)
                return t;

            throw new InvalidOperationException($"Cannot convert {value} to {typeof(T)}");

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
            DisposeBindings();

            if (ViewModel != null)
            {
                DataBind(ViewModel.Id, x => x.Id);
                DataBind(ViewModel.Label, x => x.Label);
                DataBind(ViewModel.Class, x => x.Class);
                DataBind(ViewModel.Style, x => x.Style);
            }
        }

        protected virtual void DisposeBindings()
        {
            bindings.Clear();
        }


        protected void OnClick()
        {
            Stream.Hub.Post(new ClickedEvent(Area) { Reference = Stream.Reference, Owner = Stream.Owner }, o => o.WithTarget(Stream.Owner));
        }



    }
}
