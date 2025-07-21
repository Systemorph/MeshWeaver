#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace MeshWeaver.DataCubes;

public record DimensionDescriptor
{
    public DimensionDescriptor(string systemName, Type type)
    {
        SystemName = systemName;
        Type = type;
    }

    public Type Type { get; }
    public string SystemName { get; }
}