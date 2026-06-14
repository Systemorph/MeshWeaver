using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Code-behind for <see cref="MeshNodeContentEditorView"/> — the GUI-client, cache-bound editor
/// for <see cref="MeshNodeContentEditorControl"/>.
///
/// <para>This is the CORRECT data-binding shape (see <c>Doc/GUI/DataBinding</c> "The Golden Rule"):
/// reads come straight from <c>Hub.GetMeshNodeStream(NodePath)</c> (the process-wide
/// <c>IMeshNodeStreamCache</c>) and every edit writes back through
/// <c>GetMeshNodeStream(NodePath).Update(...)</c> as a per-field read-modify-write patch. There is
/// NO server-side <c>/data</c> replica of the node and NO debounced save subscription
/// (<c>SetupAutoSave</c>) — one source of truth, the node stream itself. The fields to render are
/// declared on the control (computed on the backend), so the view needs no client type registry.</para>
/// </summary>
public partial class MeshNodeContentEditorView
{
    private string NodePath { get; set; } = string.Empty;
    private bool CanEdit { get; set; } = true;
    private IReadOnlyList<MeshNodeEditorField> Fields { get; set; } = Array.Empty<MeshNodeEditorField>();
    private bool _loaded;
    private string? _focusedKey;

    private readonly Dictionary<string, string?> _text = new();
    private readonly Dictionary<string, bool> _bool = new();

    protected override void BindData()
    {
        base.BindData();
        NodePath = ViewModel.NodePath;
        CanEdit = ViewModel.CanEdit;
        Fields = ViewModel.Fields ?? (IReadOnlyList<MeshNodeEditorField>)Array.Empty<MeshNodeEditorField>();
        if (string.IsNullOrEmpty(NodePath)) return;

        // Bind DIRECTLY to the node stream — reads stay live with the node, no replica.
        AddBinding(Hub.GetMeshNodeStream(NodePath)
            .Where(n => n is not null)
            .Subscribe(node =>
            {
                if (IsViewDisposed) return;
                LoadValues(node!);
                _loaded = true;
                InvokeAsync(StateHasChanged);
            }));
    }

    private void LoadValues(MeshNode node)
    {
        var obj = ToJsonObject(node.Content);
        foreach (var f in Fields)
        {
            // Don't clobber the field the user is actively editing with an echoed emission.
            if (f.Key == _focusedKey) continue;
            var value = obj is null ? null : obj[f.Key];
            if (f.Kind == MeshNodeEditorFieldKind.Bool)
                _bool[f.Key] = value is JsonValue jb && jb.TryGetValue<bool>(out var b) && b;
            else
                _text[f.Key] = value is JsonValue js ? js.ToString() : value?.ToString();
        }
    }

    private string? TextOf(MeshNodeEditorField f) => _text.GetValueOrDefault(f.Key);
    private bool BoolOf(MeshNodeEditorField f) => _bool.GetValueOrDefault(f.Key);

    private void OnFocus(MeshNodeEditorField f) => _focusedKey = f.Key;
    private void OnBlur(MeshNodeEditorField f)
    {
        if (_focusedKey == f.Key) _focusedKey = null;
    }

    private void OnTextChanged(MeshNodeEditorField f, string? value)
    {
        _text[f.Key] = value;
        Persist(f.Key, value is null ? null : JsonValue.Create(value));
    }

    private void OnBoolChanged(MeshNodeEditorField f, bool value)
    {
        _bool[f.Key] = value;
        Persist(f.Key, JsonValue.Create(value));
    }

    /// <summary>
    /// Per-field read-modify-write straight to the node via the cache: re-read the latest content
    /// inside the lambda and set ONLY this field, so concurrent writers / hidden fields
    /// (e.g. the sync operation's LastSyncCommitSha) are never clobbered.
    /// </summary>
    private void Persist(string key, JsonNode? value)
    {
        if (!CanEdit || string.IsNullOrEmpty(NodePath)) return;
        var opts = Hub.JsonSerializerOptions;
        Hub.GetMeshNodeStream(NodePath)
            .Update(node =>
            {
                var obj = ToJsonObject(node.Content) ?? new JsonObject();
                obj[key] = value is null ? null : JsonNode.Parse(value.ToJsonString());
                return node with { Content = JsonSerializer.SerializeToElement<object>(obj, opts) };
            })
            .Subscribe(_ => { }, ex => Logger.LogWarning(ex,
                "MeshNodeContentEditor: persist failed for {Path} field {Key}", NodePath, key));
    }

    private JsonObject? ToJsonObject(object? content)
    {
        if (content is null) return null;
        try
        {
            return content is JsonElement je
                ? JsonNode.Parse(je.GetRawText()) as JsonObject
                : JsonSerializer.SerializeToNode(content, Hub.JsonSerializerOptions) as JsonObject;
        }
        catch
        {
            return null;
        }
    }
}
