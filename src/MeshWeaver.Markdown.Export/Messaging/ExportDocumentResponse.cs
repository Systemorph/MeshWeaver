using MeshWeaver.Markdown.Export.Configuration;

namespace MeshWeaver.Markdown.Export.Messaging;

/// <summary>
/// Start-acknowledgement returned by <c>ExportDocumentHandler</c>: the export
/// runs asynchronously as an Activity, and the caller subscribes to that
/// activity for live progress and the rendered bytes.
///
/// <para><b>To get the rendered bytes</b>, the caller subscribes to
/// <c>workspace.GetMeshNodeStream(ActivityPath)</c>, projects to
/// <c>ActivityLog</c>, filters on terminal status, and reads
/// <c>ActivityLog.ReturnValue</c> — the script writes the
/// <c>{format, fileName, mimeType, content}</c> object there. The handler
/// itself does NOT wait for the activity to finish (per
/// <c>Doc/Architecture/AsynchronousCalls.md</c> → "🚨 NOTHING ASYNC EVER":
/// awaiting an activity from a hub handler deadlocks the action block under
/// load).</para>
/// </summary>
/// <param name="Format">Format being rendered.</param>
/// <param name="ActivityPath">Mesh path of the running <c>Activity</c> MeshNode.
/// Subscribe to <c>GetMeshNodeStream(ActivityPath)</c> for progress + result.
/// Empty when <see cref="Error"/> is set.</param>
/// <param name="Error">Dispatch error message (e.g. permission denied,
/// template not found). <c>null</c> on successful start. Script-time errors
/// surface to subscribers via <c>ActivityLog.Status = Failed</c>.</param>
public record ExportDocumentResponse(
    ExportFormat Format,
    string ActivityPath,
    string? Error = null);
