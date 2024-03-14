namespace OpenSmc.Charting.Models;

public interface IDataSetWithStack
{
    /// <summary>
    /// The ID of the group to which this dataset belongs to (when stacked, each group will be a separate stack). Defaults to dataset type.
    /// </summary>
    public string Stack { get; init; }
}