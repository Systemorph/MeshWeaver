using MeshWeaver.Layout;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Reads the row a row-scoped action was raised from (Doc/GUI/DataBinding → "Row-scoped actions").
/// Lives next to <see cref="ObjectAsExtensions"/> because the row arrives as JSON and is read the
/// one way an <c>object</c> payload is read on the mesh.
/// </summary>
public static class UiActionContextRowExtensions
{
    /// <summary>
    /// The row this action was raised from, as <typeparamref name="T"/> — the row as the client
    /// rendered it (<see cref="RowContext.Value"/>), converted with <see cref="ObjectAsExtensions.As{T}"/>
    /// against the owning hub's serializer options. Null when the control is not inside a bound row,
    /// or when the row does not convert (logged when <paramref name="logger"/> is given).
    /// </summary>
    /// <typeparam name="T">The row record the template was bound to.</typeparam>
    /// <param name="context">The UI action context.</param>
    /// <param name="logger">Optional; names an unconvertible row in the log.</param>
    /// <returns>The row, or null.</returns>
    public static T? RowAs<T>(this UiActionContext context, ILogger? logger = null)
        where T : class
        => context.Row?.Value.As<T>(context.Hub.JsonSerializerOptions, logger, $"row of {context.Area}");
}
