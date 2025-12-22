using System.Collections.Concurrent;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Thread-safe registry for dynamically compiled views.
/// </summary>
public class DynamicViewRegistry : IDynamicViewRegistry
{
    private readonly ConcurrentDictionary<string, Func<LayoutAreaHost, RenderingContext, UiControl>> _views = new();
    private readonly ConcurrentDictionary<string, LayoutAreaDefinition> _areaDefinitions = new();

    public void RegisterView(string area, Func<LayoutAreaHost, RenderingContext, UiControl> view, LayoutAreaDefinition? areaDefinition = null)
    {
        _views[area] = view;
        if (areaDefinition != null)
        {
            _areaDefinitions[area] = areaDefinition;
        }
    }

    public void RegisterAreaDefinition(string area, LayoutAreaDefinition areaDefinition)
    {
        _areaDefinitions[area] = areaDefinition;
    }

    public Func<LayoutAreaHost, RenderingContext, UiControl>? GetView(string area)
    {
        return _views.TryGetValue(area, out var view) ? view : null;
    }

    public LayoutAreaDefinition? GetAreaDefinition(string area)
    {
        return _areaDefinitions.TryGetValue(area, out var def) ? def : null;
    }

    public IEnumerable<string> GetViewAreas() => _views.Keys;

    public IEnumerable<LayoutAreaDefinition> GetAreaDefinitions() => _areaDefinitions.Values;

    public bool HasView(string area) => _views.ContainsKey(area);
}
