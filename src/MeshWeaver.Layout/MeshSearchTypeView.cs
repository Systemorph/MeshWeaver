namespace MeshWeaver.Layout;

/// <summary>
/// How the results of ONE NodeType render inside a <see cref="MeshSearchControl"/> — what a
/// catalog hands the view for each type that DECLARES its instance list
/// (<c>NodeTypeDefinition.ListView</c>). In a catalog grouped by type
/// (<see cref="MeshSearchRenderMode.Grouped"/> on <c>NodeType</c>), the section of
/// <see cref="NodeType"/> renders in <see cref="RenderMode"/> with <see cref="Columns"/>; every type
/// without an entry keeps the control's own presentation (cards).
/// </summary>
/// <param name="NodeType">The NodeType path the entry applies to (a result's <c>nodeType</c>).</param>
/// <param name="RenderMode">The section's presentation: <see cref="MeshSearchRenderMode.Flat"/>
/// (cards), <see cref="MeshSearchRenderMode.List"/> or <see cref="MeshSearchRenderMode.Table"/>.</param>
public record MeshSearchTypeView(string NodeType, MeshSearchRenderMode RenderMode)
{
    /// <summary>The <see cref="MeshSearchRenderMode.Table"/> columns of the section; null shows one
    /// Name column, exactly like a Table with no columns.</summary>
    public IReadOnlyList<MeshSearchColumn>? Columns { get; init; }
}
