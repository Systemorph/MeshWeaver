using OpenSmc.Data;

namespace OpenSmc.Layout;

public record AreaReference(object Id) : InstanceReference(Id)
{
    public object Options { get; init; }
    internal string Area => Id.ToString();
}