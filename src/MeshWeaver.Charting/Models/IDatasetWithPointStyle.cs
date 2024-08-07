using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models;

public interface IDataSetWithPointStyle
{
    /// <summary>
    /// Style of the point for legend.
    /// </summary>
    Shapes? PointStyle { get; init; }
}