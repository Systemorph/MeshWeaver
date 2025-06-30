using System.Collections;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using System.Collections.Immutable;
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
    private readonly Dictionary<object, object> variables = new();

    public T GetVariable<T>(object key) => (T)variables[key];
    public bool ContainsVariable(object key) => variables.ContainsKey(key);
    public object SetVariable(object key, object value) => variables[key] = value;
    public T GetOrAddVariable<T>(object key, Func<T> factory)
    {
        if (!ContainsVariable(key))
        {
            SetVariable(key, factory.Invoke());
        }

        return GetVariable<T>(key);
    }

    public LayoutDefinition LayoutDefinition { get; }
    private readonly ILogger<LayoutAreaHost> logger;
    public LayoutAreaHost(IWorkspace workspace,
        LayoutAreaReference reference,
        IUiControlService uiControlService,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> configuration)
    {
        this.uiControlService = uiControlService;
        Workspace = workspace;
        LayoutDefinition = uiControlService.LayoutDefinition;
        Stream = new SynchronizationStream<EntityStore>(
            new(workspace.Hub.Address, reference),
            workspace.Hub,
            reference,
            workspace.ReduceManager.ReduceTo<EntityStore>(),
            configuration);
        Reference = reference;
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
        executionHub = Stream.Hub.GetHostedHub(
            new LayoutExecutionAddress(),
            x => x
        );

        logger = Stream.Hub.ServiceProvider.GetRequiredService<ILogger<LayoutAreaHost>>();
    }

    private IMessageDelivery OnClick(IMessageDelivery<ClickedEvent> request)
    {
        if (GetControl(request.Message.Area) is UiControl { ClickAction: not null } control)
            InvokeAsync(() => control.ClickAction.Invoke(
                new(request.Message.Area, request.Message.Payload, Hub, this)
            ), ex => FailRequest(ex, request));
        return request.Processed();
    }

    private IMessageDelivery OnCloseDialog(IMessageDelivery<CloseDialogEvent> request)
    {
        if (GetControl(request.Message.Area) is DialogControl { CloseAction: not null } control)
            InvokeAsync(() => control.CloseAction.Invoke(
                new(request.Message.Area, request.Message.State, request.Message.Payload, Hub, this)
            ), ex => FailRequest(ex, request));
        return request.Processed();
    }

    private Task FailRequest(Exception exception, IMessageDelivery request)
    {
        logger.LogWarning(exception, "Request failed");
        Hub.Post(new DeliveryFailure(request, exception?.Message), o => o.ResponseFor(request));
        return Task.CompletedTask;
    }

    public object GetControl(string area) =>
        Stream
            .Current.Value.Collections.GetValueOrDefault(LayoutAreaReference.Areas)
            ?.Instances.GetValueOrDefault(area);





    private UiControl ConvertToControl(object instance)
        => uiControlService.Convert(instance);


    internal EntityStoreAndUpdates RenderArea(RenderingContext context, object view, EntityStore store)
    {
        if (view == null)
            return new(store, [], Stream.StreamId);

        var control = ConvertToControl(view);


        var dataContext = control.DataContext ?? context.DataContext;

        control = control with { DataContext = dataContext, };


        var ret = ((IUiControl)control).Render(this, context, store);
        foreach (var (a, c) in ret.Updates
                     .Where(x => x.Collection == LayoutAreaReference.Areas)
                     .Select(x => (x.Id.ToString(), Control: x.Value as UiControl))
                     .Where(x => x.Control != null))
            RegisterForDisposal(a, c);

        return ret;
    }


    private readonly IMessageHub executionHub;


    public void UpdateArea(RenderingContext context, object view)
    {
        Stream.Update(store =>
        {
            var changes = RemoveViews(store, context.Area);
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

    public void SubscribeToDataStream<T>(string id, IObservable<T> stream)
        => RegisterForDisposal(id, stream.Subscribe(x => Update(LayoutAreaReference.Data, coll => coll.SetItem(id, x))));

    public void Update(string collection, Func<InstanceCollection, InstanceCollection> update)
    {
        Stream.Update(ws =>
            Stream.ApplyChanges(ws.MergeWithUpdates((ws ?? new()).Update(collection, update), Stream.StreamId)),
            ex =>
            {
                logger.LogWarning(ex, "Cannot update {Collection}", collection);
                return Task.CompletedTask;
            });
    }

    public void UpdateData(string id, object data)
        => Update(LayoutAreaReference.Data, store => store.SetItem(id, data));


    private readonly ConcurrentDictionary<string, List<IDisposable>> disposablesByArea = new();


    public void RegisterForDisposal(string area, IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(area, _ => new()).Add(disposable);
    }

    public void RegisterForDisposal(IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(string.Empty, _ => new()).Add(disposable);
    }

    public IObservable<T> GetDataStream<T>(string id)
        where T : class
        => GetStream<T>(new EntityReference(LayoutAreaReference.Data, id));


    private IObservable<T> GetStream<T>(EntityReference reference) where T : class
    {
        return Stream
            .Reduce(reference)
            .Select(ci => Convert<T>(ci))
            .Where(x => x is not null)
            .DistinctUntilChanged();
    }

    private static T Convert<T>(ChangeItem<object> ci) where T : class
    {
        var result = ci.Value;
        if (result is null)
            return null;


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
                    return list as T;
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
        executionHub.InvokeAsync(action, exceptionCallback);
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


    internal EntityStoreAndUpdates RenderArea<T>(RenderingContext context, ViewStream<T> generator, EntityStore store) where T : UiControl
    {
        var ret = DisposeExistingAreas(store, context);
        RegisterForDisposal(context.Parent?.Area ?? context.Area,
            generator.Invoke(this, context, ret.Store)
                .Subscribe(c => UpdateArea(context, c), FailRendering)
        );
        return ret;
    }

    private void FailRendering(Exception ex)
    {
        logger.LogWarning(ex, "Rendering failed");
    }

    public void UpdateProgress(string area, ProgressControl progress)
        => Stream.ApplyChanges(new(Stream.Current.Value, [new(LayoutAreaReference.Areas, area, progress)], Stream.StreamId));

    internal EntityStoreAndUpdates RenderArea(RenderingContext context, ViewDefinition generator, EntityStore store)
    {
        logger.LogDebug("Schedule rendering of {area}", context.Area);
        InvokeAsync(async ct =>
        {
            logger.LogDebug("Start rendering of {area}", context.Area);
            var view = await generator.Invoke(this, context, ct);

            UpdateArea(context, view);
            logger.LogDebug("End rendering of {area}", context.Area);
        }, ex =>
        {
            FailRendering(ex);
            return Task.CompletedTask;
        });
        return DisposeExistingAreas(store, context);
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
                    UpdateArea(context, view);
                }, ex =>
                {
                    FailRendering(ex);
                    return Task.CompletedTask;
                })
            )
        );

        return DisposeExistingAreas(store, context);
    }


    internal EntityStoreAndUpdates RenderArea(RenderingContext context, IObservable<object> generator, EntityStore store)
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

    internal ISynchronizationStream<EntityStore> RenderLayoutArea()
    {
        logger.LogDebug("Scheduling re-rendering");

        InvokeAsync(() =>
        {
            //DisposeAllAreas();
            logger.LogDebug("Start re-rendering");
            var reference = (LayoutAreaReference)Stream.Reference;
            var context = new RenderingContext(reference.Area) { Layout = reference.Layout };
            Stream.Initialize(async ct =>
                    (await LayoutDefinition
                        .RenderAsync(this, context, new EntityStore()
                            .Update(LayoutAreaReference.Areas, x => x)
                            .Update(LayoutAreaReference.Data, x => x)
                        ))
                        .Store, ex =>
            {
                FailRendering(ex);
                return Task.CompletedTask;
            });
            logger.LogDebug("End re-rendering");
        }, ex =>
        {
            FailRendering(ex);
            return Task.CompletedTask;
        });
        return Stream;
    }

    private void DisposeAllAreas()
    {
        logger.LogDebug("Disposing all areas");
        disposablesByArea
            .Values
            .SelectMany(d => d)
            .ForEach(d => d.Dispose());
        disposablesByArea.Clear();
    }

    internal IEnumerable<LayoutAreaDefinition> GetLayoutAreaDefinitions()
        => LayoutDefinition.AreaDefinitions;
}
