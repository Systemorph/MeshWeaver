using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.Composition;

public record LayoutAreaHost : IDisposable
{
    private readonly IUiControlService uiControlService;
    public LayoutAreaReference Reference { get; }
    public ISynchronizationStream<EntityStore> Stream { get; }
    public IMessageHub Hub => Workspace.Hub;
    public IWorkspace Workspace { get; }
    private readonly Dictionary<object, object?> variables = new();

    public T? GetVariable<T>(object key) => variables.TryGetValue(key, out var value) ? (T?)value : default;
    public bool ContainsVariable(object key) => variables.ContainsKey(key);
    public object? SetVariable(object key, object? value) => variables[key] = value;
    public T GetOrAddVariable<T>(object key, Func<T> factory)
    {
        if (!ContainsVariable(key))
        {
            SetVariable(key, factory.Invoke());
        }

        return GetVariable<T>(key) ?? factory();
    }

    public LayoutDefinition LayoutDefinition { get; }
    private readonly ILogger<LayoutAreaHost> logger;

    public LayoutAreaHost(IWorkspace workspace,
        LayoutAreaReference reference,
        IUiControlService uiControlService,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>>? configuration)
    {

        this.uiControlService = uiControlService;
        Workspace = workspace;
        LayoutDefinition = uiControlService.LayoutDefinition;

        // When Area is null/empty, resolve to the default area
        var isDefaultArea = string.IsNullOrEmpty(reference.Area);
        var resolvedArea = isDefaultArea ? ResolveDefaultArea() : reference.Area!;
        var context = new RenderingContext(resolvedArea) { Layout = reference.Layout };

        // Capture the delivery-scoped AccessContext (AsyncLocal only) at construction time.
        // Context returns only the per-request AsyncLocal — it does NOT fall back to
        // circuitContext, which would leak one user's identity to other users when
        // the LayoutAreaHost is cached and reused across requests (e.g., in Orleans grains).
        var accessService = workspace.Hub.ServiceProvider.GetService<AccessService>();
        var capturedAccessContext = accessService?.Context;

        configuration ??= c => c;
        // Create stream with deferred initialization to avoid circular dependency
        // where initialization lambda uses 'this' before Stream property is assigned
        Stream = new SynchronizationStream<EntityStore>(
            new(workspace.Hub.Address, reference),
            workspace.Hub,
            reference,
            workspace.ReduceManager.ReduceTo<EntityStore>(),
            c => configuration.Invoke(c.WithDeferredInitialization())
                .WithInitialization(async (_, _) =>
                {
                    // Restore captured AccessContext for the rendering scope
                    if (capturedAccessContext != null)
                        accessService?.SetContext(capturedAccessContext);

                    try
                    {
                        var store = new EntityStore()
                            .Update(LayoutAreaReference.Areas, x => x)
                            .Update(LayoutAreaReference.Data, x => x);

                        // Push progress data before rendering begins
                        store = store.Update(LayoutAreaReference.Data,
                            coll => coll.SetItem("progress", new { message = "Building layout...", progress = 0 }));

                        var result = await LayoutDefinition.RenderAsync(this, context, store);

                        // Clear progress after render
                        result = result with
                        {
                            Store = result.Store.Update(LayoutAreaReference.Data,
                                coll => coll.SetItem("progress", new { message = "", progress = 100 }))
                        };

                        // When Area was null/empty, store a NamedAreaControl at "" pointing to the resolved area
                        if (isDefaultArea && !string.IsNullOrEmpty(resolvedArea))
                        {
                            var namedArea = new NamedAreaControl(resolvedArea);
                            result = result with
                            {
                                Store = result.Store.Update(LayoutAreaReference.Areas,
                                    coll => coll.SetItem(string.Empty, namedArea)),
                                Updates = result.Updates.Append(
                                    new EntityUpdate(LayoutAreaReference.Areas, string.Empty, namedArea))
                            };
                        }

                        return result.Store;
                    }
                    finally
                    {
                        // Clear restored AccessContext to avoid leaking into unrelated async work
                        if (capturedAccessContext != null)
                            accessService?.SetContext(null);
                    }
                })
                .WithExceptionCallback(ex =>
            {
                FailRendering(ex);
                return Task.CompletedTask;
            }));
        Reference = reference;
        logger = Stream.Hub.ServiceProvider.GetRequiredService<ILogger<LayoutAreaHost>>();

        // Manually trigger initialization now that Stream property is assigned
        // This resolves the circular dependency where initialization lambda uses 'this'
        Stream.Hub.Post(new InitializeHubRequest());
        Stream.RegisterForDisposal(this);
        Stream.RegisterForDisposal(
            Stream.Hub.Register<ClickedEvent>(
                OnClick,
                delivery => Stream.ClientId.Equals(delivery.Message.StreamId)
            )
        );
        Stream.RegisterForDisposal(
            Stream.Hub.Register<CloseDialogEvent>(
                OnCloseDialog,
                delivery => Stream.ClientId.Equals(delivery.Message.StreamId)
            )
        );
        Stream.RegisterForDisposal(
            Stream.Hub.Register<BlurEvent>(
                OnBlur,
                delivery => Stream.ClientId.Equals(delivery.Message.StreamId)
            )
        );
    }




    private IMessageDelivery OnClick(IMessageDelivery<ClickedEvent> request)
    {
        if (GetControl(request.Message.Area) is UiControl { ClickAction: not null } control)
            try
            {
                control.ClickAction.Invoke(
                    new(request.Message.Area, request.Message.Payload ?? new object(), Hub, this)
                );
            }
            catch (Exception ex)
            {
                FailRequest(ex, request);
            }
        return request.Processed();
    }

    private IMessageDelivery OnCloseDialog(IMessageDelivery<CloseDialogEvent> request)
    {
        if (GetControl(request.Message.Area) is DialogControl { CloseAction: not null } control)
            InvokeAsync(() => control.CloseAction.Invoke(
                new(request.Message.Area, request.Message.State, request.Message.Payload ?? new object(), Hub, this)
            ), ex => FailRequest(ex, request));
        return request.Processed();
    }

    private IMessageDelivery OnBlur(IMessageDelivery<BlurEvent> request)
    {
        if (GetControl(request.Message.Area) is IFormControl { OnBlur: Func<UiActionContext, Task> blurAction })
            try
            {
                blurAction.Invoke(
                    new(request.Message.Area, request.Message.Payload ?? new object(), Hub, this)
                );
            }
            catch (Exception ex)
            {
                FailRequest(ex, request);
            }
        return request.Processed();
    }

    private Task FailRequest(Exception? exception, IMessageDelivery request)
    {
        logger.LogWarning(exception, "Request failed");
        Hub.Post(new DeliveryFailure(request, exception?.Message), o => o.ResponseFor(request));
        return Task.CompletedTask;
    }

    public object? GetControl(string area) =>
        Stream
            .Current?.Value?.Collections.GetValueOrDefault(LayoutAreaReference.Areas)
            ?.Instances.GetValueOrDefault(area);





    private UiControl? ConvertToControl(object instance)
        => uiControlService.Convert(instance);


    internal EntityStoreAndUpdates RenderArea(RenderingContext context, object? view, EntityStore store)
    {
        if (view == null)
            return new(store, [], Stream.StreamId);

        var control = ConvertToControl(view);
        if (control == null)
            return new(store, [], Stream.StreamId);

        var dataContext = control.DataContext ?? context.DataContext;

        control = control with { DataContext = dataContext, };


        var ret = ((IUiControl)control).Render(this, context, store);
        foreach (var (a, c) in ret.Updates
                     .Where(x => x.Collection == LayoutAreaReference.Areas)
                     .Select(x => (x.Id!.ToString()!, Control: x.Value as UiControl))
                     .Where(x => x.Control != null))
            RegisterForDisposal(a, c!);

        return ret;
    }
    internal EntityStoreAndUpdates RenderArea<T>(RenderingContext context, Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<IObservable<T?>>> asyncGenerator, EntityStore store)
    {
        var ret = DisposeExistingAreas(store, context);
        InvokeAsync(async ct =>
        {
            logger?.LogDebug("Start rendering of {area}", context.Area);
            var observable = await asyncGenerator.Invoke(this, context, ct);
            RegisterForDisposal(context.Parent?.Area ?? context.Area,
                observable
                    .Subscribe(c => UpdateArea(context, c), FailRendering)
            );

            logger?.LogDebug("End rendering of {area}", context.Area);

        }, ex =>
        {
            FailRendering(ex);
            return Task.CompletedTask;
        });


        return ret;
    }


    public void UpdateArea(RenderingContext context, object? view)
    {
        Stream.Update(store =>
        {
            var changes = DisposeChildAreas(store ?? new EntityStore(), context);
            var updates = RenderArea(context, view, changes.Store);
            return Stream.ApplyChanges(
                new(
                    updates.Store,
                changes.Updates.Concat(updates.Updates),
                Stream.StreamId
                    )
            );
        }, ex =>
        {
            logger.LogWarning(ex, "Cannot update {Area}", context.Area);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Clears all views under the given area immediately,
    /// pushing the removal to the client so stale content disappears
    /// before the new content is ready.
    /// </summary>
    public void ClearArea(RenderingContext context)
    {
        Stream.Update(store =>
        {
            var s = store ?? new EntityStore();
            var changes = RemoveViews(s, context.Area);
            return Stream.ApplyChanges(changes);
        }, ex =>
        {
            logger.LogWarning(ex, "Cannot clear {Area}", context.Area);
            return Task.CompletedTask;
        });
    }

    public void SubscribeToDataStream<T>(string id, IObservable<T> stream)
        => RegisterForDisposal(id, stream.Subscribe(x => Update(LayoutAreaReference.Data, coll => coll.SetItem(id, x!))));

    public void Update(string collection, Func<InstanceCollection, InstanceCollection> update)
    {
        Stream.Update(ws =>
            Stream.ApplyChanges((ws ?? new EntityStore()).MergeWithUpdates((ws ?? new EntityStore()).Update(collection, update), Stream.StreamId)),
            ex =>
            {
                logger.LogWarning(ex, "Cannot update {Collection}", collection);
                return Task.CompletedTask;
            });
    }

    public void UpdateData(string id, object? data)
    {
        if (data != null)
            Update(LayoutAreaReference.Data, store => store.SetItem(id, data));
    }


    private readonly ConcurrentDictionary<string, List<IDisposable>> disposablesByArea = new();


    public void RegisterForDisposal(string area, IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(area, _ => new()).Add(disposable);
    }

    /// <summary>
    /// Registers a disposable for the given key, disposing any previously registered disposable with the same key.
    /// Use this when re-creating subscriptions to prevent duplicate/endless emissions.
    /// </summary>
    public void ReplaceDisposable(string key, IDisposable disposable)
    {
        // Dispose existing disposables with this key
        if (disposablesByArea.TryRemove(key, out var existing))
        {
            foreach (var d in existing)
            {
                try { d.Dispose(); }
                catch { /* ignore disposal errors */ }
            }
        }
        // Add the new disposable
        disposablesByArea.GetOrAdd(key, _ => new()).Add(disposable);
    }

    public void RegisterForDisposal(IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(string.Empty, _ => new()).Add(disposable);
    }

    public IObservable<T?> GetDataStream<T>(string id)
        where T : class
        => GetStream<T>(new EntityReference(LayoutAreaReference.Data, id));


    private IObservable<T?> GetStream<T>(EntityReference reference) where T : class
    {
        return Stream
            .Reduce(reference)!
            .Select(Convert<T>)
            .Where(x => x is not null)
            .DistinctUntilChanged();
    }

    private T? Convert<T>(ChangeItem<object> ci) where T : class
    {
        var result = ci.Value;
        if (result is null)
            return null;

        if (result is JsonElement je)
            return je.Deserialize<T?>(Hub.JsonSerializerOptions);
        if (result is JsonNode jn)
            return jn.Deserialize<T?>(Hub.JsonSerializerOptions);

        if (result is T t)
            return t;
        // Check if T is an array
        if (typeof(T).IsArray)
        {
            var elementType = typeof(T).GetElementType()!;
            if (result is Array array)
            {
                var ret = Array.CreateInstance(elementType, array.Length);
                for (var i = 0; i < ret.Length; i++)
                    ret.SetValue(array.GetValue(i), i);
                return (T)(object)ret;
            }
        }
        // Handle other IEnumerable<T> types
        else
        {
            var elementType = typeof(T).GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments().First();

            if (elementType != null)
            {
                if (result is IEnumerable enumerable)
                {
                    var genericListType = typeof(List<>).MakeGenericType(elementType);
                    var list = (IList)Activator.CreateInstance(genericListType)!;

                    foreach (var item in enumerable)
                        list.Add(item);
                    return (T)list;
                }
            }
        }

        throw new NotSupportedException(
            $"Cannot convert instance of type {result.GetType().Name} to Type {typeof(T).FullName}");
    }

    public void Dispose()
    {
        foreach (var disposable in disposablesByArea.ToArray())
            disposable.Value.ForEach(d => d.Dispose());
        disposablesByArea.Clear();
    }

    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback)
    {
        Stream.Hub.InvokeAsync(action, exceptionCallback);
    }

    public void InvokeAsync(Action action, Func<Exception, Task> exceptionCallback) =>
        InvokeAsync(_ =>
        {
            action();
            return Task.CompletedTask;
        }, exceptionCallback);

    private EntityStoreAndUpdates DisposeExistingAreas(EntityStore store, RenderingContext context)
    {
        var contextArea = context.Area;
        foreach (var area in disposablesByArea.Where(x => x.Key.StartsWith(contextArea)).ToArray())
            if (disposablesByArea.TryRemove(area.Key, out var disposables))
                disposables.ForEach(d => d.Dispose());

        return RemoveViews(store, contextArea);
    }

    /// <summary>
    /// Disposes subscriptions for child areas only (not the area itself),
    /// then removes all views under the area from the store.
    /// Used by UpdateArea to avoid disposing the feeding observable subscription.
    /// </summary>
    private EntityStoreAndUpdates DisposeChildAreas(EntityStore store, RenderingContext context)
    {
        var contextArea = context.Area;
        foreach (var area in disposablesByArea.Where(x => x.Key.StartsWith(contextArea) && x.Key != contextArea).ToArray())
            if (disposablesByArea.TryRemove(area.Key, out var disposables))
                disposables.ForEach(d => d.Dispose());

        return RemoveViews(store, contextArea);
    }

    private EntityStoreAndUpdates RemoveViews(EntityStore store, string contextArea)
    {
        var existing =
            store.Collections
                .GetValueOrDefault(LayoutAreaReference.Areas)
                ?.Instances
                .Where(x => ((string)x.Key).StartsWith(contextArea))
                .ToArray();

        if (existing == null)
            return new(store, [], Stream.StreamId);

        return new(store.Update(LayoutAreaReference.Areas,
            i => i with { Instances = i.Instances.RemoveRange(existing.Select(x => x.Key)) }), existing.Select(i =>
            new EntityUpdate(LayoutAreaReference.Areas, i.Key, null) { OldValue = i.Value }), Stream.StreamId);
    }


    internal EntityStoreAndUpdates RenderArea<T>(RenderingContext context, ViewStream<T> generator, EntityStore store) where T : UiControl?
    {
        var ret = DisposeExistingAreas(store, context);
        RegisterForDisposal(context.Parent?.Area ?? context.Area,
            generator.Invoke(this, context, ret.Store)
                .Subscribe(c => UpdateArea(context, c), FailRendering)
        );
        return ret;
    }
    internal EntityStoreAndUpdates RenderArea<T>(RenderingContext context, AsyncViewStream<T> asyncGenerator, EntityStore store) where T : UiControl?
    {
        var ret = DisposeExistingAreas(store, context);

        logger.LogDebug("Schedule rendering of {area}", context.Area);
        InvokeAsync(async ct =>
        {
            logger.LogDebug("Start rendering of {area}", context.Area);
            var observable = await asyncGenerator.Invoke(this, context, store, ct);
            RegisterForDisposal(context.Parent?.Area ?? context.Area,
                observable
                    .Subscribe(c => UpdateArea(context, c), FailRendering)
            );

            logger.LogDebug("End rendering of {area}", context.Area);
        }, ex =>
        {
            FailRendering(ex);
            return Task.CompletedTask;
        });
        return ret;
    }

    private void FailRendering(Exception ex)
    {
        logger.LogWarning(ex, "Rendering failed");
    }

    public void UpdateProgress(string area, ProgressControl progress)
        => Stream.ApplyChanges(new(Stream.Current?.Value ?? new EntityStore(), [new(LayoutAreaReference.Areas, area, progress)], Stream.StreamId));

    internal EntityStoreAndUpdates RenderArea(RenderingContext context, ViewDefinition generator, EntityStore store)
    {
        var ret = DisposeExistingAreas(store, context);
        logger.LogDebug("Schedule rendering of {area}", context.Area);
        InvokeAsync(async ct =>
        {
            logger.LogDebug("Start rendering of {area}", context.Area);
            var view = await generator.Invoke(this, context, ct);

            UpdateArea(context, view!);
            logger.LogDebug("End rendering of {area}", context.Area);
        }, ex =>
        {
            FailRendering(ex);
            return Task.CompletedTask;
        });
        return ret;
    }
    internal EntityStoreAndUpdates RenderArea(
        RenderingContext context,
        IObservable<ViewDefinition> generator,
        EntityStore store)
    {
        RegisterForDisposal(context.Area, generator.Subscribe(vd =>
                InvokeAsync(async ct =>
                {
                    var view = await vd.Invoke(this, context, ct);
                    UpdateArea(context, view!);
                }, ex =>
                {
                    FailRendering(ex);
                    return Task.CompletedTask;
                })
            )
        );

        return DisposeExistingAreas(store, context);
    }


    internal EntityStoreAndUpdates RenderArea(RenderingContext context, IObservable<object?> generator, EntityStore store)
    {
        var ret = DisposeExistingAreas(store, context);

        RegisterForDisposal(
            context.Area,
            generator
                .DistinctUntilChanged()
                .Subscribe(view => UpdateArea(context, view))
        );
        return ret;
    }

    internal ISynchronizationStream<EntityStore> GetStream()
    {
        return Stream;
    }

    /// <summary>
    /// Navigates to the specified URI by posting a NavigationRequest to the subscriber (portal).
    /// Safe to call from LayoutAreaHost context (click handlers, etc.)
    /// </summary>
    /// <param name="uri">The URI to navigate to.</param>
    /// <param name="forceLoad">Whether to force a full page reload.</param>
    /// <param name="replace">Whether to replace the current history entry instead of adding a new one.</param>
    public void NavigateTo(string uri, bool forceLoad = false, bool replace = false)
    {
        var subscriber = Stream.Get<Address>(nameof(SubscribeRequest.Subscriber));
        if (subscriber != null)
        {
            if (subscriber.Host is { })
                subscriber = subscriber.Host;
            Stream.Hub.Post(new NavigationRequest(uri) { ForceLoad = forceLoad, Replace = replace }, o => o.WithTarget(subscriber));
        }
        else
        {
            logger.LogWarning("Cannot navigate: no subscriber address found for stream {StreamId}", Stream.StreamId);
        }
    }

    internal IEnumerable<LayoutAreaDefinition> GetLayoutAreaDefinitions()
        => LayoutDefinition.AreaDefinitions.Values.Where(l => l.IsVisible());

    /// <summary>
    /// Resolves the default area name from LayoutDefinition.DefaultArea,
    /// or falls back to the first visible area definition.
    /// </summary>
    private string ResolveDefaultArea()
    {
        // Try to use the configured default area
        if (!string.IsNullOrEmpty(LayoutDefinition.DefaultArea))
            return LayoutDefinition.DefaultArea;

        // Fall back to the first visible area definition
        var firstArea = LayoutDefinition.AreaDefinitions.Values
            .Where(l => l.IsVisible())
            .OrderBy(l => l.Order ?? 0)
            .FirstOrDefault();

        return firstArea?.Area ?? string.Empty;
    }
}
