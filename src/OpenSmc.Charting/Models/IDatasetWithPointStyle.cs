using OpenSmc.Charting.Enums;

namespace OpenSmc.Charting.Models;

public interface IDataSetWithPointStyle
{
    /// <summary>
    /// Style of the point for legend.
    /// </summary>
    Shapes? PointStyle { get; init; }
}