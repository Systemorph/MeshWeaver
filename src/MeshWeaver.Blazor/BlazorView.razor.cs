using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor;

public class BlazorView<TViewModel, TView> : ComponentBase, IAsyncDisposable
    where TViewModel : IUiControl
    where TView : BlazorView<TViewModel, TView>
{
    [Inject] protected ILogger<TView> Logger { get; set; } = null!;
    [Inject] protected PortalApplication PortalApplication { get; set; } = null!;
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;
    protected IMessageHub Hub => PortalApplication.Hub;
    [Parameter] public TViewModel ViewModel { get; set; } = default(TViewModel)!;

    [Parameter] public ISynchronizationStream<JsonElement> Stream { get; set; } = null!;
    [Parameter] public string Area { get; set; } = null!;

    [CascadingParameter(Name = "Context")] public object? Context { get; set; }

    [CascadingParameter(Name = "ThemeMode")]
    public DesignThemeModes Mode { get; set; }

    [CascadingParameter(Name = nameof(DataContext))]
    public string? DataContext { get; set; }

    [CascadingParameter(Name = nameof(Model))]
    public ModelParameter<JsonElement>? Model { get; set; }

    protected string? Style { get; set; }

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


    protected string? Class { get; set; }
    protected string? Id { get; set; }

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
        object? value, 
        Expression<Func<TView, T>> propertySelector, 
        Func<object,T, T>? conversion = null,
        T defaultValue = default!)
    {
        var expr = propertySelector?.Body as MemberExpression;
        Action<object?> setter = expr?.Member is PropertyInfo pi 
            ? o => pi.SetValue(this,o) 
            : expr?.Member is FieldInfo fi 
                ? o => fi.SetValue(this, o) 
                : throw new ArgumentException(
                    "Expression provided must point to a property or field.", 
                    nameof(propertySelector)
                    );

        if (value is JsonPointerReference reference)
        {
            if (Model is not null && !reference.Pointer.StartsWith('/'))
                setter(Hub.ConvertSingle(Model.GetValueFromModel(reference), conversion, defaultValue));
            else
                bindings.Add(Stream.DataBind(reference, DataContext, conversion, defaultValue)
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
        else if (value is ContextProperty contextProperty)
        {
            var val = 
                Context is JsonObject jo 
                    ? jo[contextProperty.Property]
                    : Context?.GetType().GetProperty(contextProperty.Property)?.GetValue(Context);
            setter(Hub.ConvertSingle(val, conversion, defaultValue));

        }
        else
        {
            setter(Hub.ConvertSingle(value, conversion, defaultValue));
        }
    }

    protected void RequestStateChange()
    {
        StateHasChanged();
    }

    protected string SubArea(string area)
        => $"{Area}/{area}";

    protected virtual void UpdatePointer(object value, JsonPointerReference reference)
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
        }
    }
    private readonly List<IDisposable> bindings = new();

    protected virtual void DisposeBindings()
    {
        foreach (var d in bindings)
            d.Dispose();
        bindings.Clear();
    }

    protected virtual void OnClick()
    {
        Stream.Hub.Post(new ClickedEvent(Area, Stream.StreamId), o => o.WithTarget(Stream.Owner));
    }


    protected async Task<bool> IsDarkModeAsync()
    {
        return Mode switch
        {
            DesignThemeModes.Dark => true,
            DesignThemeModes.Light => false,
            _ => await JSRuntime.InvokeAsync<bool>("themeHandler.isDarkMode")
        };

    }
}

