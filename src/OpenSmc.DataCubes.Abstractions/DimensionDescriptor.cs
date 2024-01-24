using System.Diagnostics.CodeAnalysis;

namespace OpenSmc.DataCubes;

public record DimensionDescriptor
{
    public DimensionDescriptor([NotNull] string systemName, [NotNull] Type type)
    {
        SystemName = systemName;
        Type = type;
    }

    public Type Type { get; }
    public string SystemName { get; }
}