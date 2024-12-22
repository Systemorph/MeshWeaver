namespace MeshWeaver.Charting.Models;

public interface IDataSetWithOrder<T>
{
    /// <summary>
    /// The drawing order of dataset. Also affects order for stacking, tooltip and legend.
    /// </summary>
    int? Order { get; init; }

    /// <summary>
    /// The drawing order of dataset. Also affects order for stacking, tooltip and legend.
    /// </summary>
    public T WithOrder(int? order);
}
