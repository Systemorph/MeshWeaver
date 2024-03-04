namespace OpenSmc.Layout;

[method: Newtonsoft.Json.JsonConstructor]
public record LayoutArea(
    string Area,
    object View,
    object Options,
    IReadOnlyCollection<ViewDependency> Dependencies)
{
    public LayoutArea(string area, object view)
        : this(area, view, null, null)
    {
    }
    public LayoutArea(string area, object view, object Options)
        : this(area, view, Options, null)
    {
    }
}

public record ViewDependency(object Id, string Property);
