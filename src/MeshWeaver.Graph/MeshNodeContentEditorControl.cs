using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Serialization;
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
    /// Builds the editable field list from a content record type: <c>[Browsable(false)]</c> and
    /// <c>[JsonIgnore]</c> properties are skipped, <c>[Translation]</c>/<c>[Description]</c>/
    /// <c>[Display]</c>/<c>[DisplayName]</c> supply the label (else the wordified property name),
    /// the key is the property's JSON name, and the kind follows the property type (bool →
    /// checkbox, enum → dropdown, everything else → text).
    ///
    /// <para>🚨 <b><see cref="MeshNodeEditorField.Key"/> is a WIRE name, not a CLR name</b> —
    /// <c>[JsonPropertyName]</c> wins over the camelCase property name (#3542).
    /// <c>MeshNodeContentEditorView</c> uses this key verbatim on BOTH sides of the binding, as a
    /// key into the node content's JSON object: <c>LoadValues</c> reads <c>obj[f.Key]</c> and
    /// <c>Persist</c> writes <c>obj[f.Key]</c>. A key derived from the CLR name therefore binds the
    /// control to a field that does not exist the moment a property is renamed behind a
    /// <c>[JsonPropertyName]</c>: the read misses (the control renders unset over a value that IS
    /// there) and the write lands under a key the record ignores. Both halves are SILENT — nothing
    /// throws and nothing logs. 🚨 And the edit is not merely unread: the owning hub materialises
    /// the content as the record type and re-serialises it, so the unknown key is dropped on that
    /// round trip and never reaches storage at all. Measured — the control keeps the chosen value
    /// in its own field state, so the editor shows it until the next emission and then silently
    /// reverts to unset.</para>
    ///
    /// <para>That is not hypothetical: it disabled the platform's own update policy.
    /// <c>Admin/UpdatePolicy</c>'s <c>Policy</c> became <c>DeclaredPolicy</c> +
    /// <c>[JsonPropertyName("policy")]</c> so that an absent declaration could fail closed to
    /// <c>None</c> (#3607), and from that commit the Updates tab's strategy dropdown wrote
    /// <c>declaredPolicy</c>. An install whose policy was lost could no longer be repaired from the
    /// one surface built for it, and under <c>None</c> nothing evaluates, so nothing ever
    /// contradicted the tab. See <c>Doc/Architecture/EditorFieldKeys</c>.</para>
    ///
    /// <para>🚨 <c>[JsonIgnore]</c> is skipped for the same reason, one step further on: such a
    /// property is one the record does NOT persist, so a control over it could only ever write a
    /// key the type drops on read. Only the default <see cref="JsonIgnoreCondition.Always"/> form
    /// is skipped — <c>[JsonIgnore(Condition = Never)]</c> means the opposite ("always write this,
    /// even at its default") and stays editable.</para>
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
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>()
                is not { Condition: JsonIgnoreCondition.Always })
            .Select(p =>
            {
                var label = p.LocalizedDescription(locale)
                    ?? p.GetCustomAttribute<DisplayAttribute>()?.Name
                    ?? p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                    ?? p.Name.Wordify();
                // The WIRE name, so the control binds to the field the record actually carries. The
                // camelCase fallback is unchanged on purpose: it is the key every stored record was
                // written under, and re-deriving it differently would re-key live content.
                var key = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                    ?? p.Name.ToCamelCase()!;
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
