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
    [Parameter] public required TViewModel ViewModel { get; set; } 

    [Parameter] public ISynchronizationStream<JsonElement>? Stream { get; set; } 
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

    private bool _viewDisposed;

    /// <summary>True after <see cref="DisposeAsync"/> has been entered. Subscription callbacks
    /// can check this to avoid invoking <see cref="StateHasChanged"/> on a dead renderer.</summary>
    protected bool IsViewDisposed => _viewDisposed;

    public virtual ValueTask DisposeAsync()
    {
        // Set the flag BEFORE disposing subscriptions so any in-flight callbacks
        // queued by Subscribe(...) onto the synchronization context can short-circuit
        // on IsViewDisposed instead of touching a torn-down renderer.
        _viewDisposed = true;
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
        Expression<Func<TView, T?>> propertySelector, 
        Func<object?,T?, T?>? conversion = null,
        T defaultValue = default!)
    {
        try
        {
            var expr = propertySelector.Body as MemberExpression;
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
                try
                {
                    if (Model is not null && !reference.Pointer.StartsWith('/'))
                    {
                        var convertedValue = Hub.ConvertSingle(Model.GetValueFromModel(reference), conversion, defaultValue);
                        setter(convertedValue);
                    }
                    else if(Stream is not null)
                    {
                        bindings.Add(Stream.DataBind(reference, DataContext, conversion, defaultValue)
                            .Subscribe(v =>
                                {
                                    if (_viewDisposed) return;
                                    try
                                    {
                                        Logger.LogTrace("Binding property in Area {area}", Area);
                                        InvokeAsync(() =>
                                        {
                                            // Re-check after dispatch — the renderer may have
                                            // been torn down between Subscribe.OnNext and the
                                            // synchronization-context callback firing.
                                            if (_viewDisposed) return;
                                            try
                                            {
                                                setter(v);
                                                RequestStateChange();
                                            }
                                            catch (ObjectDisposedException) { /* renderer gone */ }
                                            catch (Exception ex)
                                            {
                                                Logger.LogError(ex, "Error setting bound property value in Area {area}", Area);
                                            }
                                        });
                                    }
                                    catch (ObjectDisposedException) { /* renderer gone */ }
                                    catch (Exception ex)
                                    {
                                        Logger.LogError(ex, "Error scheduling bound property update in Area {area}", Area);
                                    }
                                }
                            )
                        );
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error binding JsonPointerReference '{pointer}' in Area {area}", reference.Pointer, Area);
                    // Set default value on binding failure
                    setter(Hub.ConvertSingle(null, conversion, defaultValue));
                }
            }
            else if (value is ContextProperty contextProperty)
            {
                try
                {
                    var val = 
                        Context is JsonObject jo 
                            ? jo[contextProperty.Property]
                            : Context?.GetType().GetProperty(contextProperty.Property)?.GetValue(Context);
                    setter(Hub.ConvertSingle(val, conversion, defaultValue));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error binding ContextProperty '{property}' in Area {area}", contextProperty.Property, Area);
                    // Set default value on binding failure
                    setter(Hub.ConvertSingle(null, conversion, defaultValue));
                }
            }
            else
            {
                try
                {
                    setter(Hub.ConvertSingle(value, conversion, defaultValue));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error binding value '{value}' in Area {area}", value, Area);
                    // Set default value on binding failure
                    setter(Hub.ConvertSingle(null, conversion, defaultValue));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Critical error in DataBind for Area {area}", Area);
            // Attempt to set default value as last resort
            try
            {
                var expr = propertySelector.Body as MemberExpression;
                if (expr?.Member is PropertyInfo pi)
                {
                    pi.SetValue(this, defaultValue);
                }
                else if (expr?.Member is FieldInfo fi)
                {
                    fi.SetValue(this, defaultValue);
                }
            }
            catch (Exception innerEx)
            {
                Logger.LogError(innerEx, "Failed to set default value after DataBind error in Area {area}", Area);
            }
        }
    }

    protected void RequestStateChange()
    {
        StateHasChanged();
    }

    protected string SubArea(string area)
        => $"{Area}/{area}";

    protected virtual void UpdatePointer(object? value, JsonPointerReference reference)
    {
        if(Stream is null)
            throw new InvalidOperationException("Stream must be set before updating pointers.");
        Stream.UpdatePointer(value, DataContext ?? "/", reference, Model);
    }


    protected virtual void BindData()
    {
        DataBind(ViewModel.Id, x => x.Id);
        DataBind(ViewModel.Class, x => x.Class);
        DataBind(ViewModel.Style, x => x.Style);
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
        if(Stream is null)
            throw new InvalidOperationException("Stream must be set before sending click events.");
        Stream.Hub.Post(new ClickedEvent(Area, Stream.StreamId), o => o.WithTarget(Stream.Owner));
    }


    protected async Task<bool> IsDarkModeAsync()
    {
        return Mode switch
        {
            DesignThemeModes.Dark => true,
            DesignThemeModes.Light => false,
            _ => await GetSystemDarkModeAsync()
        };
    }

    private async Task<bool> GetSystemDarkModeAsync()
    {
        try
        {
            // Use getEffectiveTheme which considers both user preference and system settings
            var theme = await JSRuntime.InvokeAsync<string>("themeHandler.getEffectiveTheme");
            return theme == "dark";
        }
        catch (JSException)
        {
            // JS not available yet (prerendering) or themeHandler not loaded - default to light mode
            return false;
        }
    }
}

