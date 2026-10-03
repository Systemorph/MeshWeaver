using MeshWeaver.Data;

namespace MeshWeaver.Layout;

/// <summary>
/// Builds the pointer a <c>BindToNode</c> builder puts in a control's bindable slot. A node-bound
/// DataContext resolves RELATIVE pointers against the node; an absolute one (<c>/…</c>) always reads
/// the layout area's data. So a field handed to <c>BindToNode</c> with a leading slash would bind
/// the wrong store without a sound — it is refused here instead.
/// </summary>
internal static class NodeFieldPointer
{
    /// <summary>The relative pointer for <paramref name="field"/>.</summary>
    /// <param name="field">A relative JSON pointer: a property name or a <c>/</c>-separated path.</param>
    /// <param name="parameterName">The caller's parameter name, for the exception.</param>
    /// <exception cref="ArgumentException"><paramref name="field"/> is empty or starts with <c>/</c>.</exception>
    public static JsonPointerReference Relative(string field, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(field))
            throw new ArgumentException(
                "A node-bound control needs the field to bind; the pointer is empty.", parameterName);
        if (field.StartsWith('/'))
            throw new ArgumentException(
                $"'{field}' is an ABSOLUTE pointer, which reads the layout area's data and never the node. "
                + "Pass the field relative to the node (\"instructions\", \"review/notes\"); for a text that "
                + "lives in /data, set the control's slot to a JsonPointerReference directly.",
                parameterName);
        return new JsonPointerReference(field);
    }
}
