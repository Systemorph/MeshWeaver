using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
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
    /// <summary>Single-choice dropdown over a fixed option set (an enum). Options are carried on
    /// <see cref="MeshNodeEditorField.Options"/>; the stored value is the option name (enums
    /// serialize as their string name — see <c>EnumMemberJsonStringEnumConverter</c>).</summary>
    Enum,
}

/// <summary>
/// One editable field of a <see cref="MeshNodeContentEditorControl"/>: the JSON
/// <paramref name="Key"/> inside the node content, the display <paramref name="Label"/>, and the
/// input <paramref name="Kind"/>. Computed on the backend (where the content type + its attributes
/// are available) and carried on the control, so the GUI view needs NO client-side type registry.
/// </summary>
public record MeshNodeEditorField(string Key, string Label, MeshNodeEditorFieldKind Kind)
{
    /// <summary>For an <see cref="MeshNodeEditorFieldKind.Enum"/> field, the selectable option names
    /// (the enum member names). Empty for every other kind.</summary>
    public ImmutableList<string> Options { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Optional display text per entry in <see cref="Options"/>, keyed by the stored option value.
    /// Lets a picker show a human-readable (and localizable) label while still storing the raw
    /// value — e.g. the language picker stores <c>de</c> but shows <c>Deutsch</c>. An option with no
    /// entry here falls back to showing its stored value, so this stays optional everywhere.
    /// </summary>
    public ImmutableDictionary<string, string> OptionLabels { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>The display text for <paramref name="option"/> — its <see cref="OptionLabels"/>
    /// entry when one exists, otherwise the stored value itself.</summary>
    public string LabelFor(string option)
        => OptionLabels.TryGetValue(option, out var label) ? label : option;

    /// <summary>
    /// Builds the editable field list from a content record type: <c>[Browsable(false)]</c>
    /// properties are skipped, <c>[Translation]</c>/<c>[Description]</c>/<c>[Display]</c>/
    /// <c>[DisplayName]</c> supply the label (else the wordified property name), the key is the
    /// camelCase property name, and the kind follows the property type (bool → checkbox, enum →
    /// dropdown, everything else → text).
    /// </summary>
    /// <param name="contentType">The content record type to derive fields from.</param>
    /// <param name="locale">
    /// The viewer's language, used to pick a <c>[Translation]</c> over the English
    /// <c>[Description]</c>. Null → English. Callers on a render path pass
    /// <c>accessService.ViewerLocale()</c>; the field list is built per render, so each viewer gets
    /// their own labels even though the type's attributes are shared.
    /// </param>
    public static ImmutableList<MeshNodeEditorField> FromType(Type contentType, string? locale = null) =>
        contentType.GetProperties()
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Select(p =>
            {
                var label = p.LocalizedDescription(locale)
                    ?? p.GetCustomAttribute<DisplayAttribute>()?.Name
                    ?? p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                    ?? p.Name.Wordify();
                var key = p.Name.ToCamelCase()!;
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (t.IsEnum)
                    return new MeshNodeEditorField(key, label, MeshNodeEditorFieldKind.Enum)
                    {
                        Options = Enum.GetNames(t).ToImmutableList(),
                    };
                var kind = t == typeof(bool) ? MeshNodeEditorFieldKind.Bool : MeshNodeEditorFieldKind.Text;
                return new MeshNodeEditorField(key, label, kind);
            })
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
