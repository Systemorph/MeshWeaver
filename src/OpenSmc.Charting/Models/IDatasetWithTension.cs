namespace OpenSmc.Charting.Models;

public interface IDataSetWithTension
{
    /// <summary>
    /// Bezier curve tension of the line. Set to 0 to draw straight lines. This option is ignored if monotone cubic interpolation is used. Note This was renamed from 'tension' but the old name still works.
    /// </summary>
    double? Tension { get; init; }
}