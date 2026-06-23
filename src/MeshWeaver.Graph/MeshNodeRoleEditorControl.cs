using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.Graph;

/// <summary>
/// A node-bound editor for ONE role of an <c>AccessAssignment</c> node — the role at
/// <see cref="RoleIndex"/> inside the assignment's <c>Roles</c> list. The GUI binds DIRECTLY to the
/// node via <c>Hub.GetMeshNodeStream(NodePath)</c>: the role dropdown and the Deny checkbox read
/// from that stream and write back per-field through <c>GetMeshNodeStream(NodePath).Update(...)</c>.
///
/// <para>This is the same data-binding shape as <see cref="MeshNodeContentEditorControl"/> — ONE
/// source of truth (the node stream), no layout-area <c>/data</c> replica and no debounced save
/// subscription. The backend only DECLARES the control with a path + index; all value resolution
/// and write-back happen GUI-side.</para>
///
/// <para>The stored role value is the bare role <b>id</b> (e.g. <c>"Editor"</c>) — the value the
/// permission evaluator resolves — NOT a role node path.</para>
/// </summary>
public record MeshNodeRoleEditorControl(string NodePath, int RoleIndex)
    : UiControl<MeshNodeRoleEditorControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>When false the role renders read-only (a label, struck through if denied).</summary>
    public bool CanEdit { get; init; } = true;

    /// <summary>
    /// The role ids offered in the dropdown. The current value is added automatically if it is not
    /// already in the list (so a custom role still displays). Defaults to the built-in roles.
    /// </summary>
    public ImmutableList<string> RoleOptions { get; init; } =
        ImmutableList.Create("Admin", "Editor", "Viewer", "Commenter");
}
