using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using Microsoft.DotNet.Interactive.Formatting;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutArea : IDisposable
{
    private readonly IMessageHub hub;
    public IChangeStream<JsonElement, LayoutAreaReference> Stream { get; }

    public LayoutAreaReference Reference { get; init; }

    private readonly Subject<Func<ChangeItem<JsonElement>, ChangeItem<JsonElement>>> updateStream =
        new();
    public readonly IWorkspace Workspace;

    public void UpdateLayout(string area, object control)
    {
        updateStream.OnNext(ws => UpdateImpl(area, control, ws));
    }
    private static UiControl ConvertToControl(object instance)
    {
        if(instance is UiControl control)
            return control;

        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        return Controls.Html(instance.ToDisplayString(mimeType));
    }
    private ChangeItem<JsonElement> UpdateImpl(string area, object control, ChangeItem<JsonElement> ws)
    {
        // TODO V10: Dispose old areas (09.06.2024, Roland Bürgi)
        var path = $"/{LayoutAreaReference.Areas}/{area.Replace("/", "~1")}";
        return ws.SetValue(UpdateJsonElement(ws.Value, path, ConvertToControl(control)));
    }

    public LayoutArea(LayoutAreaReference Reference, IMessageHub hub, IChangeStream<WorkspaceState> workspaceStream)
    {
        this.hub = hub;
        Stream = new ChangeStream<JsonElement, LayoutAreaReference>(hub.Address, hub, Reference, workspaceStream.ReduceManager.ReduceTo<JsonElement>());
        this.Reference = Reference;
        Workspace = hub.GetWorkspace();
        Stream.AddDisposable(updateStream
            .Scan(
                new ChangeItem<JsonElement>(
                    hub.Address,
                    Reference,
                    JsonDocument.Parse("{}").RootElement,
                    hub.Address,
                    hub.Version
                ),
                (currentState, updateFunc) => updateFunc(currentState)
            )
            .Sample(TimeSpan.FromMilliseconds(100))
            .Subscribe(Stream));
        Stream.AddDisposable(this);
    }

    public void UpdateData(string pointer, object data)
    {
        updateStream.OnNext(ws =>
            ws.SetValue(UpdateJsonElement(ws.Value, pointer, data)));
    }

    private JsonElement UpdateJsonElement(JsonElement existing, string pointer, object data) => 
        new JsonPatch(GetPatchOperations(existing, JsonPointer.Parse(pointer), data)).Apply(existing);

    private PatchOperation GetPatchOperations(JsonElement existing, JsonPointer pointer, object data)
    {
        var parent = JsonPointer.Create(pointer.Segments.Take(pointer.Segments.Length -1).ToArray());
        var node = JsonSerializer.SerializeToNode(data, hub.JsonSerializerOptions);
        while (parent.Segments.Length > 0)
        {
            if(parent.Evaluate(existing).HasValue)
                break;
            node = new JsonObject(new KeyValuePair<string, JsonNode>[]
            {
                new(pointer.Segments[parent.Segments.Length].Value, node)
            });
            pointer = parent;
            parent = JsonPointer.Create(parent.Segments.Take(parent.Segments.Length - 1).ToArray());
        }
        return pointer.Evaluate(existing).HasValue ? PatchOperation.Replace(pointer, node) : PatchOperation.Add(pointer, node);
    }

    private readonly ConcurrentDictionary<string, List<IDisposable>> disposablesByArea = new();
    public void AddDisposable(string area, IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(area, _ => new()).Add(disposable);
    }

    public IObservable<T> GetDataStream<T>(string id)
        where T:class
    {
        var reference = JsonPointer.Parse(LayoutAreaReference.GetDataPointer(id));
        return Stream.Select(ci => reference.Evaluate(ci.Value)?.Deserialize<T>(hub.JsonSerializerOptions))
            .Where(x => x != null)
            .DistinctUntilChanged();
    }

    public void Dispose()
    {
        foreach (var disposable in disposablesByArea)
            disposable.Value.ForEach(d => d.Dispose());
    }
}
