using System.Collections.Generic;

namespace MeshWeaver.Markdown.Export.Messaging;

/// <summary>
/// Outcome of <see cref="Handlers.SendDocumentDispatch.ExportAndSend"/>: the source node was
/// exported to a document (via the same node ⇒ file pipeline the download uses) and emailed as an
/// attachment to the resolved recipients.
///
/// <para>Plain value record — carried back to the caller (layout-area click action / test), never a
/// hub message. On success <see cref="SentTo"/> lists the addresses that were successfully mailed.</para>
/// </summary>
/// <param name="Success">True when every resolved recipient was mailed without error.</param>
/// <param name="ActivityPath">Path of the export <c>Activity</c> node that produced the file (for
/// diagnostics); <c>null</c> when the export never started.</param>
/// <param name="SentTo">The addresses that were successfully mailed.</param>
/// <param name="Error">Human-readable reason when <see cref="Success"/> is false; <c>null</c> otherwise.</param>
public sealed record SendDocumentResult(
    bool Success,
    string? ActivityPath,
    IReadOnlyList<string> SentTo,
    string? Error = null);
