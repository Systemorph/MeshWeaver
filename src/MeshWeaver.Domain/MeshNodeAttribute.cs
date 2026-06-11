using System.Reflection;
using System.Text.RegularExpressions;

namespace MeshWeaver.Domain;

/// <summary>
/// Visual density of the <c>MeshNodePicker</c> rendered for a <see cref="MeshNodeAttribute"/> property.
/// </summary>
public enum MeshNodePickerLayout
{
    /// <summary>Full card: large avatar, name + node-type subtitle, comfortable padding.</summary>
    Default,

    /// <summary>Compact: small icon, name only, minimal padding — for tight rows (e.g. the chat composer).</summary>
    Thin
}

/// <summary>
/// Direction the <c>MeshNodePicker</c> dropdown opens relative to the field.
/// </summary>
public enum MeshNodePickerOpenDirection
{
    /// <summary>Dropdown opens below the field (default).</summary>
    Down,

    /// <summary>Dropdown opens above the field — for fields anchored to the bottom of the viewport (e.g. the chat composer).</summary>
    Up
}

/// <summary>
/// Marks a property as a reference to a MeshNode.
/// The editor will render a MeshNodePickerControl with the specified queries.
/// Supports template variables in query strings:
/// - {node.PropertyName} — replaced with the node's property value (case-insensitive) at control creation time.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class MeshNodeAttribute(params string[] queries) : Attribute
{
    /// <summary>
    /// Query strings for the MeshNodePickerControl.
    /// Each query is run in parallel and results are merged.
    /// </summary>
    public string[] Queries { get; } = queries;

    /// <summary>
    /// Visual density of the rendered picker. Defaults to <see cref="MeshNodePickerLayout.Default"/>.
    /// Set to <see cref="MeshNodePickerLayout.Thin"/> for a narrow, low-footprint control.
    /// </summary>
    public MeshNodePickerLayout Layout { get; set; } = MeshNodePickerLayout.Default;

    /// <summary>
    /// Direction the dropdown opens. Defaults to <see cref="MeshNodePickerOpenDirection.Down"/>.
    /// Set to <see cref="MeshNodePickerOpenDirection.Up"/> for bottom-anchored fields.
    /// </summary>
    public MeshNodePickerOpenDirection Open { get; set; } = MeshNodePickerOpenDirection.Down;

    /// <summary>
    /// When true and no value is set, the picker auto-selects (and persists) the first available
    /// result as the default. Opt-in — defaults to false.
    /// </summary>
    public bool DefaultToFirst { get; set; }

    /// <summary>
    /// Resolves template variables in query strings using the node object.
    /// Replaces any {node.PropertyName} with the property value via reflection.
    /// </summary>
    public static string[] ResolveQueries(string[] queries, object? node)
    {
        if (queries == null || queries.Length == 0)
            return queries ?? [];

        if (node == null)
            return queries;

        var nodeType = node.GetType();

        return queries.Select(q =>
            Regex.Replace(q, @"\{node\.(\w+)\}", match =>
            {
                var propName = match.Groups[1].Value;
                var prop = nodeType.GetProperty(propName,
                    BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                return prop?.GetValue(node)?.ToString() ?? "";
            })
        ).ToArray();
    }

    /// <summary>
    /// Resolves template variables in query strings.
    /// Replaces {node.namespace} and {node.path} with actual values from the editing context.
    /// </summary>
    public static string[] ResolveQueries(string[] queries, string? nodeNamespace, string? nodePath = null)
    {
        if (queries == null || queries.Length == 0)
            return queries ?? [];

        return queries.Select(q =>
        {
            var resolved = q;
            if (nodeNamespace != null)
                resolved = resolved.Replace("{node.namespace}", nodeNamespace);
            if (nodePath != null)
                resolved = resolved.Replace("{node.path}", nodePath);
            return resolved;
        }).ToArray();
    }
}
