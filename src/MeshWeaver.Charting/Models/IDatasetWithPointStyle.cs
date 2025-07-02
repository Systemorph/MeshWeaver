#nullable enable
using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models;

public interface IDataSetWithPointStyle<T>
{
    /// <summary>
    /// Style of the point for legend.
    /// </summary>
    Shapes? PointStyle { get; init; }

    /// <summary>
    /// Style of the point for legend.
    /// </summary>
    T WithPointStyle(Shapes? pointStyle);
}
