namespace MeshWeaver.Charting.Models;

public interface IDataSetWithTension<T>
{
    /// <summary>
    /// Bezier curve tension of the line. Set to 0 to draw straight lines. This option is ignored if monotone cubic interpolation is used. Note This was renamed from 'tension' but the old name still works.
    /// </summary>
    double? Tension { get; init; }

    /// <summary>
    /// Bezier curve tension of the line. Set to 0 to draw straight lines. This option is ignored if monotone cubic interpolation is used. Note This was renamed from 'tension' but the old name still works.
    /// </summary>
    public T Smoothed(double? tension);
}
