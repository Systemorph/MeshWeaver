namespace OpenSmc.Charting.Models;

public interface IDataSetWithOrder
{
    /// <summary>
    /// The drawing order of dataset. Also affects order for stacking, tooltip and legend.
    /// </summary>
    int? Order { get; init; }
}