namespace MeshWeaver.Blazor.Components;

/// <summary>
/// What a markdown view hands <c>MarkdownHtmlRenderer</c> (MeshWeaver.Blazor.Views) to make its executable cells
/// EDITABLE (#1636) — the fence workbench's counterpart of the Code node's inline cell editor.
///
/// <para>🚨 <b>Its presence IS the permission decision.</b> A view supplies this object only when the
/// viewer holds <c>Permission.Update</c> on the node, resolved server-side by the layout area that
/// produced the control (<c>MarkdownOverviewLayoutArea</c> folds
/// <c>hub.GetEffectivePermissions</c> into <c>CollaborativeMarkdownControl.CanEdit</c>) — the same
/// check, from the same evaluator, that decides whether a Code node renders an editor or a
/// <c>&lt;pre&gt;</c>. Null means read-only, which is the default everywhere. There is no second
/// permission model here and there must not be one: a client-side re-derivation could only ever
/// disagree with the server, and the direction it would disagree in is the one that shows an editor
/// to someone who cannot save.</para>
/// </summary>
/// <param name="NodePath">Path of the markdown MeshNode whose body holds the fences — the auto-save
/// target. Null/empty when there is no node behind the markdown (a generated
/// <c>Controls.Markdown</c>): the cell is still editable and runnable, but nothing is persisted.</param>
/// <param name="CodeOf">Resolves a submission id to the code the document currently holds for it,
/// so the editor seeds from the hosting view's own parse rather than from re-decoded HTML. Null
/// falls back to the rendered <c>&lt;pre&gt;</c>'s text.</param>
/// <param name="OnBufferChanged">Receives (submissionId, text) on every keystroke so the view's Run
/// submits what the viewer is LOOKING at. The auto-save is debounced by design, so without this a
/// Run pressed straight after typing executes the previous code — the exact trap
/// <c>CodeLayoutAreas.RunFromBuffer</c> exists to close for the Code-node cell.</param>
public sealed record MarkdownCellEditing(
    string? NodePath,
    Func<string, string?>? CodeOf,
    Action<string, string>? OnBufferChanged);
