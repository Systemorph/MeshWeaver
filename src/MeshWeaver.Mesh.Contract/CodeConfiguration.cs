namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a code configuration with C# source code for dynamic compilation.
/// Stored as MeshNode.Content in the Source sub-partition of NodeType hubs.
/// Identity (Id) and display name (Name) live on the parent MeshNode.
/// </summary>
public record CodeConfiguration
{
    /// <summary>
    /// The C# source code content.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// The programming language for syntax highlighting (e.g., "csharp", "json", "javascript").
    /// Defaults to "csharp".
    /// </summary>
    public string Language { get; init; } = "csharp";

    /// <summary>
    /// When <c>true</c>, the Content layout surfaces a Run button next to Edit that
    /// posts a <c>SubmitCodeRequest</c> to the kernel and streams output into a
    /// result pane below the code block. Default <c>false</c> — Code nodes that
    /// aren't marked executable stay read-only.
    /// </summary>
    public bool IsExecutable { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent successful execution kicked off via the
    /// Run button or <c>ExecuteScriptRequest</c>. Surfaced on the Code node's
    /// Content area as "Last executed: …" with a link to the full activity
    /// history. <c>null</c> until the first run finishes.
    /// </summary>
    public DateTimeOffset? LastExecutedAt { get; init; }

    /// <summary>
    /// User identifier (typically the AccessContext ObjectId / username) of the
    /// person who triggered the most recent run. <c>null</c> if the run was
    /// system-initiated or the identity wasn't available.
    /// </summary>
    public string? LastExecutedBy { get; init; }

    /// <summary>
    /// Full path of the <c>Activity</c> MeshNode for the most recent run. The
    /// Code node's Content view subscribes to this activity's <c>Progress</c>
    /// area so the "Output" pane shows the last run's log immediately on page
    /// load — instead of waiting forever on a kernel area that may not exist.
    /// </summary>
    public string? LastActivityPath { get; init; }

    /// <summary>
    /// Parent path under which <c>Activity</c> nodes are created
    /// when this Code node executes. The activity lives at
    /// <c>{ActivityParentPath}/_Activity/{guid}</c>.
    ///
    /// <para>If null (the default), activities are created at the partition root —
    /// i.e. <c>{firstSegmentOfCodePath}/_Activity/{guid}</c>. This puts every run
    /// in the user's home activity feed, regardless of where in the partition
    /// the Code node lives, and avoids deeply-nested satellite paths that race
    /// the routing materialisation pipeline.</para>
    ///
    /// <para>Set explicitly for docs/sample Code nodes that want to log into a
    /// different home (e.g. when a documentation page is showcasing a script
    /// and wants the runs to appear in the *viewing user's* feed rather than
    /// the doc partition's).</para>
    /// </summary>
    public string? ActivityParentPath { get; init; }

    /// <summary>
    /// 🚨 Round-trip buffer for content members this compiled shape does not declare
    /// (schema evolution: written by a newer build, or removed since the JSON was
    /// persisted). <c>[JsonExtensionData]</c> captures them on read and re-emits them on
    /// write, and rides record <c>with</c>-copies — so neither the persistence echo nor
    /// an edit through a narrower shape can silently drop them (the content-narrowing
    /// silent-data-loss class; see <c>NodeTypeDefinition.UnknownMembers</c>). Never read
    /// programmatically. <c>[Browsable(false)]</c> keeps it out of reflected editors.
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, System.Text.Json.JsonElement>? UnknownMembers { get; init; }
}
