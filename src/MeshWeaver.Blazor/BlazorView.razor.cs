using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor;

public class BlazorView<TViewModel, TView> : ComponentBase, IAsyncDisposable
    where TViewModel : IUiControl
    where TView : BlazorView<TViewModel, TView>
{
    [Inject] protected ILogger<TView> Logger { get; set; }

    [Parameter]
    public TViewModel ViewModel { get; set; }

    [Parameter]
    public ISynchronizationStream<JsonElement> Stream { get; set; }
    [Parameter]
    public string Area { get; set; }

    [Parameter]
    public string DataContext { get; set; }

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


    protected string Class { get; set; }
    protected string Id { get; set; }

    protected List<IDisposable> Disposables { get; } = new();

    public virtual ValueTask DisposeAsync()
    {
        Logger.LogDebug("Disposing area {Area}", Area);
        DisposeBindings();
        foreach (var d in Disposables)
            d.Dispose();
        Disposables.Clear();
        return default;
    }

    protected void AddBinding(IDisposable binding)
    {
        bindings.Add(binding);
    }

    protected void DataBind<T>(
        object value, 
        Expression<Func<TView, T>> propertySelector, Func<object, T> conversion = null)
    {
        var expr = propertySelector?.Body as MemberExpression;
        Action<object> setter = expr?.Member is PropertyInfo pi 
            ? o => pi.SetValue(this,o) 
            : expr?.Member is FieldInfo fi 
                ? o => fi.SetValue(this, o) 
                : throw new ArgumentException(
                    "Expression provided must point to a property or field.", 
                    nameof(propertySelector)
                    );

        if (value is JsonPointerReference reference)
        {
            if (Model is not null)
                setter(Stream.ConvertSingle(Model.GetValueFromModel(reference), conversion));
            else
                bindings.Add(Stream.DataBind(reference, DataContext, conversion)
                    .Subscribe(v =>
                        {
                            Logger.LogTrace("Binding property in Area {area}", Area);
                            InvokeAsync(() =>
                            {
                                setter(v);
                                RequestStateChange();
                            });
                        }
                    )
                );

        }
        else
        {
            setter(Stream.ConvertSingle(value, conversion));
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
            if (Model != null)
                return Observable.Return(Stream.ConvertSingle(Model.GetValueFromModel(reference), conversion));
            return Stream.DataBind(reference, DataContext, conversion);
        }

        return Observable.Return(Stream.ConvertSingle(value, conversion));
    }

    protected string SubArea(string area)
        => $"{Area}/{area}";

    protected void UpdatePointer<T>(T value, JsonPointerReference reference)
    {
        Stream.UpdatePointer(value, DataContext, reference, Model);
    }

    protected virtual void BindData()
    {
        if (ViewModel != null)
        {
            DataBind(ViewModel.Id, x => x.Id);
            DataBind(ViewModel.Class, x => x.Class);
            DataBind(ViewModel.Style, x => x.Style);
            if (ViewModel.DataContext != null)
                DataContext = WorkspaceReference.Decode(ViewModel.DataContext).ToString();
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
        Stream.Hub.Post(new ClickedEvent(Area, Stream.StreamId), o => o.WithTarget(Stream.Owner));
    }
}
