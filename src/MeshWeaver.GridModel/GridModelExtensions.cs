using MeshWeaver.Messaging;

namespace MeshWeaver.GridModel;

public static class GridModelExtensions
{
    /// <summary>
    /// Adds all GridModel types to the MessageHub configuration for proper serialization support.
    /// This includes GridOptions, ColDef, ColGroupDef, GridControl, and related helper types.
    /// </summary>
    /// <param name="configuration">The MessageHub configuration to extend</param>
    /// <returns>The updated MessageHub configuration</returns>
    public static MessageHubConfiguration AddGridModel(this MessageHubConfiguration configuration)
    {
        return configuration.WithTypes(
            typeof(GridOptions),
            typeof(ColDef),
            typeof(ColGroupDef),
            typeof(CellRendererParams),
            typeof(CellStyle),
            typeof(GridControl)
        );
    }
}
