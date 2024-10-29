using System.Collections;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using Microsoft.DotNet.Interactive.Formatting;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.Composition;

public record LayoutAreaHost : IDisposable
{
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
        LayoutDefinition layoutDefinition,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> configuration)
    {
        Workspace = workspace;
        LayoutDefinition = layoutDefinition;
        Stream = new SynchronizationStream<EntityStore>(
            new(workspace.Hub.Address, reference),
            workspace.Hub,
            reference,
            workspace.ReduceManager.ReduceTo<EntityStore>(),
            configuration);
        Reference = reference;
        Stream.AddDisposable(this);
        Stream.AddDisposable(
            Stream.Hub.Register<ClickedEvent>(
                OnClick,
                delivery => Stream.StreamId.Equals(delivery.Message.StreamId)
            )
        );
        executionHub = Stream.Hub.GetHostedHub(
            new LayoutExecutionAddress(Stream.Hub.Address),
            x => x
        );

        logger = Stream.Hub.ServiceProvider.GetRequiredService<ILogger<LayoutAreaHost>>();
    }

    private IMessageDelivery OnClick(IMessageDelivery<ClickedEvent> request)
    {
        if (GetControl(request.Message.Area) is UiControl { ClickAction: not null } control)
            InvokeAsync(() => control.ClickAction.Invoke(
                new(request.Message.Area, request.Message.Payload, Hub, this)
            ));
        return request.Processed();
    }

    public object GetControl(string area) =>
        Stream
            .Current.Value.Collections.GetValueOrDefault(LayoutAreaReference.Areas)
            ?.Instances.GetValueOrDefault(area);





    private static UiControl ConvertToControl(object instance)
    {
        if (instance is UiControl control)
            return control;

        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        return Controls.Html(instance.ToDisplayString(mimeType));
    }


    internal EntityStoreAndUpdates RenderArea(RenderingContext context, object view, EntityStore store)
    {
        if (view == null)
            return new(store, [], Stream.StreamId);

        var control = ConvertToControl(view);


        var dataContext = control.DataContext ?? context.DataContext;

        control = control with { DataContext = dataContext, };


        return ((IUiControl)control).Render(this, context, store);
    }


    private readonly IMessageHub executionHub;


    public void UpdateArea(RenderingContext context, object view)
    {
        Stream.UpdateAsync(store =>
        {
            var changes = DisposeExistingAreas(store, context);
            var updates = RenderArea(context, view, changes.Store);
            return Stream.ApplyChanges(
                new(
                    updates.Store,
                changes.Updates.Concat(updates.Updates),
                Stream.StreamId
                    )
            ) ;
        });
    }


    public void Update(string collection, Func<InstanceCollection, InstanceCollection> update)
    {
        Stream.UpdateAsync(ws =>
            Stream.ApplyChanges(ws.MergeWithUpdates((ws ?? new()).Update(collection, update), Stream.StreamId))
        );
    }


    private readonly ConcurrentDictionary<string, List<IDisposable>> disposablesByArea = new();

    public void AddDisposable(string area, IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(area, _ => new()).Add(disposable);
    }

    public IObservable<T> GetDataStream<T>(string id)
        where T : class
        => GetStream<T>(LayoutAreaReference.Data, id);
    public IObservable<T> GetPropertiesStream<T>(string id)
        where T : class
        => GetStream<T>(LayoutAreaReference.Properties, id);
    public IObservable<T> GetStream<T>(string collection, string id)
        where T : class
    {
        var reference = new EntityReference(collection, id);
        return Stream
            .Select(ci => Convert<T>(ci, reference))
            .Where(x => x != null)
            .DistinctUntilChanged();
    }

    private static T Convert<T>(ChangeItem<EntityStore> ci, EntityReference reference) where T : class
    {
        var result = ci.Value.Reduce(reference);
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
        foreach (var disposable in disposablesByArea)
            disposable.Value.ForEach(d => d.Dispose());
        disposablesByArea.Clear();
    }

    public void InvokeAsync(Func<CancellationToken, Task> action)
    {
        executionHub.InvokeAsync(action);
    }

    public void InvokeAsync(Action action) =>
        InvokeAsync(_ =>
        {
            action();
            return Task.CompletedTask;
        });

    private EntityStoreAndUpdates DisposeExistingAreas(EntityStore store, RenderingContext context)
    {
        var contextArea = context.Area;
        foreach (var area in disposablesByArea.Where(x => x.Key.StartsWith(contextArea)).ToArray())
            if (disposablesByArea.TryRemove(area.Key, out var disposables))
                disposables.ForEach(d => d.Dispose());

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
            new EntityUpdate(LayoutAreaReference.Areas, contextArea, null) { OldValue = i.Value }), Stream.StreamId);
    }


    internal EntityStoreAndUpdates RenderArea<T>(RenderingContext context, ViewStream<T> generator, EntityStore store)
    {
        var ret = DisposeExistingAreas(store, context);
        AddDisposable(context.Parent?.Area ?? context.Area,
            generator.Invoke(this, context, ret.Store)
                .Subscribe(c => InvokeAsync(() => UpdateArea(context, c)))
        );
        return ret;
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
        });
        return DisposeExistingAreas(store, context);
    }
    internal EntityStoreAndUpdates RenderArea(
        RenderingContext context,
        IObservable<ViewDefinition> generator,
        EntityStore store)
    {
        AddDisposable(context.Area, generator.Subscribe(vd =>
                InvokeAsync(async ct =>
                {
                    var view = await vd.Invoke(this, context, ct);
                    UpdateArea(context, view);
                })
            )
        );

        return DisposeExistingAreas(store, context);
    }


    internal EntityStoreAndUpdates RenderArea(RenderingContext context, IObservable<object> generator, EntityStore store)
    {
        AddDisposable(
            context.Area,
            generator.Subscribe(view =>
                InvokeAsync(() => UpdateArea(context, view))
            )
        );

        return DisposeExistingAreas(store, context);
    }

    internal ISynchronizationStream<EntityStore> RenderLayoutArea()
    {
        logger.LogDebug("Scheduling re-rendering");

        InvokeAsync(() =>
        {
            DisposeAllAreas();
            logger.LogDebug("Start re-rendering");
            var reference = (LayoutAreaReference)Stream.Reference;
            var context = new RenderingContext(reference.Area) { Layout = reference.Layout };
            Stream.Initialize(
                    LayoutDefinition
                        .Render(this, context, new EntityStore()
                            .Update(LayoutAreaReference.Areas, x => x)
                            .Update(LayoutAreaReference.Data, x => x)
                        )
                        .Store);
            logger.LogDebug("End re-rendering");
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
}
