using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Layout;
using MeshWeaver.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Graph;

/// <summary>The input kind for a <see cref="MeshNodeEditorField"/>.</summary>
public enum MeshNodeEditorFieldKind
{
    /// <summary>Single-line text field.</summary>
    Text,
    /// <summary>Boolean checkbox.</summary>
    Bool,
}

/// <summary>
/// One editable field of a <see cref="MeshNodeContentEditorControl"/>: the JSON
/// <paramref name="Key"/> inside the node content, the display <paramref name="Label"/>, and the
/// input <paramref name="Kind"/>. Computed on the backend (where the content type + its attributes
/// are available) and carried on the control, so the GUI view needs NO client-side type registry.
/// </summary>
public record MeshNodeEditorField(string Key, string Label, MeshNodeEditorFieldKind Kind)
{
    /// <summary>
    /// Builds the editable field list from a content record type: <c>[Browsable(false)]</c>
    /// properties are skipped, <c>[Description]</c>/<c>[Display]</c>/<c>[DisplayName]</c> supply the
    /// label (else the wordified property name), the key is the camelCase property name, and the
    /// kind follows the property type (bool → checkbox, everything else → text).
    /// </summary>
    public static ImmutableList<MeshNodeEditorField> FromType(Type contentType) =>
        contentType.GetProperties()
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Select(p => new MeshNodeEditorField(
                p.Name.ToCamelCase()!,
                p.GetCustomAttribute<DescriptionAttribute>()?.Description
                    ?? p.GetCustomAttribute<DisplayAttribute>()?.Name
                    ?? p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                    ?? p.Name.Wordify(),
                p.PropertyType == typeof(bool) || p.PropertyType == typeof(bool?)
                    ? MeshNodeEditorFieldKind.Bool
                    : MeshNodeEditorFieldKind.Text))
            .ToImmutableList();
}

/// <summary>
/// A data-bound editor for a mesh node's content that the GUI client binds DIRECTLY to the node via
/// <c>IMeshNodeStreamCache</c> (<c>Hub.GetMeshNodeStream(NodePath)</c>) — reads come from that
/// stream and edits write back through <c>GetMeshNodeStream(NodePath).Update(...)</c>.
///
/// <para>This is the antidote to the "replicate the node into a layout-area <c>/data</c> copy + a
/// server-side save subscription (<c>SetupAutoSave</c>)" antipattern: ONE source of truth (the node
/// stream), no <c>/data</c> replica, no debounced save loop. The backend only DECLARES this control
/// with a <see cref="NodePath"/> and the <see cref="Fields"/> to edit; all value resolution and
/// write-back happen GUI-side, per <c>Doc/GUI/DataBinding</c> ("The Golden Rule: the GUI is fully
/// data-bound").</para>
///
/// <para>For rich content types that need markdown editors / pickers / dimension selects, use the
/// dedicated node-bound controls (e.g. <see cref="MeshNodePickerControl"/>,
/// <c>MarkdownEditorControl.WithAutoSave</c>) — this control covers simple scalar/bool fields.</para>
/// </summary>
public record MeshNodeContentEditorControl(string NodePath)
    : UiControl<MeshNodeContentEditorControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Whether the fields are editable. When false the editor renders read-only.</summary>
    public bool CanEdit { get; init; } = true;

    /// <summary>The fields to render, computed on the backend from the content type.</summary>
    public ImmutableList<MeshNodeEditorField> Fields { get; init; } = ImmutableList<MeshNodeEditorField>.Empty;

    /// <summary>
    /// Declares an editor for the node at <paramref name="nodePath"/> whose content is of type
    /// <paramref name="contentType"/> — the fields are reflected from that type on the backend.
    /// </summary>
    public static MeshNodeContentEditorControl ForType(string nodePath, Type contentType, bool canEdit = true) =>
        new(nodePath) { CanEdit = canEdit, Fields = MeshNodeEditorField.FromType(contentType) };
}
