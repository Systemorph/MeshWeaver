namespace MeshWeaver.Charting.Models;

public interface IDataSetWithPointRadiusAndRotation
{
    /// <summary>
    /// The radius of the point shape. If set to 0, nothing is rendered.
    /// </summary>
    int? PointRadius { get; init; }

    /// <summary>
    /// The rotation of the point in degrees.
    /// </summary>
    int? PointRotation { get; init; }
}