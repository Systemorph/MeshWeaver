using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using Microsoft.AspNetCore.Components;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;

namespace MeshWeaver.Blazor
{
    public partial class BlazorView<TViewModel, TView> : IDisposable
        where TViewModel : UiControl
        where TView : BlazorView<TViewModel, TView>
    {
        [Inject] private IMessageHub Hub { get; set; }
        protected override void OnParametersSet()
        {
            base.OnParametersSet();
            if(!IsUpToDate())
                BindData();
        }


        protected string Label { get; set; }

        protected string Class { get; set; }
        protected string Id { get; set; }

        protected List<IDisposable> Disposables { get; } = new();

        public virtual void Dispose()
        {
            foreach (var d in bindings.Concat(Disposables))
            {
                d.Dispose();
            }
        }


        private readonly List<IDisposable> bindings = new();

        protected void DataBindProperty<T>(object value, Expression<Func<TView, T>> propertySelector, Func<object, T> conversion = null)
        {
            var expr = (propertySelector as LambdaExpression)?.Body as MemberExpression;
            var property = expr?.Member as PropertyInfo;
            if (property == null)
                throw new ArgumentException("Expression needs to point to a property.");
            DataBind<T>(value, v =>
                {
                    if (Equals(property.GetValue(this), v))
                        return false;
                    property.SetValue(this, v);
                    return true;
                },
                conversion
            );
        }
        protected void DataBind<T>(object value, Func<T, bool> bindingAction, Func<object, T> conversion = null)
        {
            bindings.Add(Stream.GetObservable<T>(ViewModel.DataContext, value, conversion).Subscribe(x =>
            {
                InvokeAsync(() =>
                {
                    if(bindingAction(x))
                        StateHasChanged();
                });
            }));
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
                DataBindProperty(ViewModel.Id, x => x.Id);
                DataBindProperty(ViewModel.Label, x => x.Label);
                DataBindProperty(ViewModel.Class, x => x.Class);
                DataBindProperty(ViewModel.Style, x => x.Style);
            }
        }

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
