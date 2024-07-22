using System.Collections;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using Microsoft.DotNet.Interactive.Formatting;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.Layout.Composition;

public record LayoutAreaHost : IDisposable
{
    public ISynchronizationStream<EntityStore, LayoutAreaReference> Stream { get; }
    public IMessageHub Hub => Stream.Hub;
    public IWorkspace Workspace => Hub.GetWorkspace();
    private readonly Dictionary<object, object> variables = new ();

    public T GetVariable<T>(object key) => (T)variables[key];
    public bool ContainsVariable(object key) => variables.ContainsKey(key);
    public void SetVariable(object key, object value) => variables[key] = value;
    public T GetOrAddVariable<T>(object key, Func<T> factory)
    {
        if (!ContainsVariable(key))
        {
            SetVariable(key, factory.Invoke());
        }

        return GetVariable<T>(key);
    }

    public LayoutAreaHost(
        ISynchronizationStream<WorkspaceState> workspaceStream,
        LayoutAreaReference reference,
        object subscriber
    )
    {
        Stream = new SynchronizationStream<EntityStore, LayoutAreaReference>(
            workspaceStream.Owner,
            subscriber,
            workspaceStream.Hub,
            reference,
            workspaceStream.ReduceManager.ReduceTo<EntityStore>(),
            InitializationMode.Automatic
        );
        Stream.AddDisposable(this);
        Stream.AddDisposable(
            Stream.Hub.Register<ClickedEvent>(
                OnClick,
                delivery => Stream.Reference.Equals(delivery.Message.Reference)
            )
        );
        executionHub = Stream.Hub.GetHostedHub(
            new LayoutExecutionAddress(Stream.Hub.Address),
            x => x
        );
    }

    private IMessageDelivery OnClick(IMessageDelivery<ClickedEvent> request)
    {
        if (GetControl(request.Message.Area) is UiControl { ClickAction: not null } control)
            control.ClickAction.Invoke(
                new(request.Message.Area, request.Message.Payload, Hub, this)
            );
        return request.Processed();
    }

    public object GetControl(string area) =>
        Stream
            .Current.Value.Collections.GetValueOrDefault(LayoutAreaReference.Areas)
            ?.Instances.GetValueOrDefault(area);

    public void UpdateLayout(string area, object control)
    {
        Stream.Update(ws => UpdateImpl(area, control, ws));
    }

    private static UiControl ConvertToControl(object instance)
    {
        if (instance is UiControl control)
            return control;

        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        return Controls.Html(instance.ToDisplayString(mimeType));
    }

    private ChangeItem<EntityStore> UpdateImpl(string area, object control, EntityStore ws)
    {
        var converted = ConvertToControl(control);

        var newStore = (ws ?? new()).Update(
            LayoutAreaReference.Areas,
            instances => instances.Update(area, converted)
        );

        return new(
            Stream.Owner,
            Stream.Reference,
            newStore,
            Stream.Owner,
            null, // todo we can fill this in here and use.
            Stream.Hub.Version
        );
    }

    private readonly IMessageHub executionHub;

    public string UpdateData(string id, object data)
        => Update(LayoutAreaReference.Data, id, data);
    public string UpdateProperties(string id, object data)
        => Update(LayoutAreaReference.Properties, id, data);
    public string Update(string collection, string id, object data)
    {
        Stream.Update(ws =>
            new(
                Stream.Owner,
                Stream.Reference,
                (ws ?? new()).Update(collection, i => i.Update(id, data)),
                Stream.Owner,
                null, // todo we can fill this in here and use.
                Stream.Hub.Version
            )
        );
        return LayoutAreaReference.GetDataPointer(id);
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
    }

    public void InvokeAsync(Func<CancellationToken, Task> action)
    {
        executionHub.Schedule(action);
    }

    public void InvokeAsync(Action action) =>
        InvokeAsync(_ =>
        {
            action();
            return Task.CompletedTask;
        });

    public void DisposeExistingAreas(RenderingContext context)
    {
        foreach (var area in disposablesByArea.Where(x => x.Key.StartsWith(context.Area)).ToArray())
        {
            if (disposablesByArea.TryRemove(area.Key, out var disposables))
            {
                disposables.ForEach(d => d.Dispose());
            }
        }

        Stream.Update(ws =>
            new(
                Stream.Owner,
                Stream.Reference,
                (ws ?? new()).Update(LayoutAreaReference.Areas,
                    i => i
                        with
                        {
                            Instances = i.Instances
                                .Where(x => !((string)x.Key).StartsWith(context.Area))
                                .ToImmutableDictionary()
                        }),
                Stream.Owner,
                null,
                Stream.Hub.Version
            )
        );
    }

}
