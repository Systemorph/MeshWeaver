using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Code-behind for <see cref="MeshNodeRoleEditorView"/> — the GUI-client, cache-bound editor for one
/// role of an <c>AccessAssignment</c> node.
///
/// <para>Reads come straight from <c>Hub.GetMeshNodeStream(NodePath)</c> and every edit (role id or
/// the Deny flag) writes back through <c>GetMeshNodeStream(NodePath).Update(...)</c> as a
/// read-modify-write that touches ONLY the role at <see cref="MeshNodeRoleEditorControl.RoleIndex"/>.
/// There is NO <c>/data</c> replica and NO debounced save subscription — one source of truth, the
/// node stream itself (the same shape as <see cref="MeshNodeContentEditorView"/>).</para>
/// </summary>
public partial class MeshNodeRoleEditorView
{
    private string NodePath { get; set; } = string.Empty;
    private int RoleIndex { get; set; }
    private bool CanEdit { get; set; } = true;

    private bool _loaded;
    private bool _hasRole;
    private string? _role;
    private bool _denied;
    private IReadOnlyList<string> _options = Array.Empty<string>();

    /// <summary>
    /// Reads <c>NodePath</c>, <c>RoleIndex</c>, <c>CanEdit</c>, and role options from the
    /// view-model, then subscribes to the node stream so the displayed role and Deny flag
    /// stay live with the underlying <c>AccessAssignment</c> node.
    /// </summary>
    protected override void BindData()
    {
        base.BindData();
        NodePath = ViewModel.NodePath;
        RoleIndex = ViewModel.RoleIndex;
        CanEdit = ViewModel.CanEdit;
        var optionIds = ViewModel.RoleOptions ?? (IReadOnlyList<string>)Array.Empty<string>();
        if (string.IsNullOrEmpty(NodePath)) return;

        // Bind DIRECTLY to the node stream — reads stay live with the node, no replica.
        AddBinding(Hub.GetMeshNodeStream(NodePath)
            .Where(n => n is not null)
            .Subscribe(node =>
            {
                if (IsViewDisposed) return;
                Load(node!, optionIds);
                _loaded = true;
                InvokeAsync(StateHasChanged);
            }));
    }

    private void Load(MeshNode node, IReadOnlyList<string> optionIds)
    {
        var assignment = Deserialize(node.Content);
        if (assignment is null || RoleIndex < 0 || RoleIndex >= assignment.Roles.Count)
        {
            _hasRole = false;
            return;
        }

        var role = assignment.Roles[RoleIndex];
        _role = role.Role;
        _denied = role.Denied;
        _hasRole = true;

        var list = optionIds.ToList();
        if (!string.IsNullOrEmpty(_role) && !list.Contains(_role))
            list.Insert(0, _role);
        _options = list;
    }

    private void OnRoleChanged(string? value)
    {
        _role = value;
        Persist(r => r with { Role = value ?? string.Empty });
    }

    private void OnDeniedChanged(bool value)
    {
        _denied = value;
        Persist(r => r with { Denied = value });
    }

    /// <summary>
    /// Read-modify-write straight to the node via the cache: re-read the latest assignment inside the
    /// lambda and change ONLY the role at <see cref="RoleIndex"/>, so concurrent edits to sibling
    /// roles or other fields are never clobbered.
    /// </summary>
    private void Persist(Func<RoleAssignment, RoleAssignment> map)
    {
        if (!CanEdit || string.IsNullOrEmpty(NodePath)) return;
        Hub.GetMeshNodeStream(NodePath)
            .Update(node =>
            {
                var assignment = Deserialize(node.Content);
                if (assignment is null || RoleIndex < 0 || RoleIndex >= assignment.Roles.Count)
                    return node;
                var roles = assignment.Roles.ToList();
                roles[RoleIndex] = map(roles[RoleIndex]);
                return node with { Content = assignment with { Roles = roles } };
            })
            .Subscribe(_ => { }, ex => Logger.LogWarning(ex,
                "MeshNodeRoleEditor: persist failed for {Path} role {Index}", NodePath, RoleIndex));
    }

    private AccessAssignment? Deserialize(object? content)
    {
        if (content is AccessAssignment a) return a;
        if (content is JsonElement je)
        {
            try { return JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText(), Hub.JsonSerializerOptions); }
            catch { return null; }
        }
        return null;
    }
}
