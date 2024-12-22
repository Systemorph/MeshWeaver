namespace MeshWeaver.Charting.Models;

public interface IDataSetWithPointRadiusAndRotation<T>
{
    /// <summary>
    /// The radius of the point shape. If set to 0, nothing is rendered.
    /// </summary>
    int? PointRadius { get; init; }

    /// <summary>
    /// The radius of the point shape. If set to 0, nothing is rendered.
    /// </summary>
    public T WithPointRadius(int? pointRadius);

    /// <summary>
    /// The rotation of the point in degrees.
    /// </summary>
    int? PointRotation { get; init; }

    T WithPointRadiusAndRotation(int? pointRadius, int? pointRotation);
}
