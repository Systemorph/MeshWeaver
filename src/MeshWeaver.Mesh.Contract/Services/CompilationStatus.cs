namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Distinct lifecycle states of a NodeType's compile. Consumers (e.g. MCP
/// <c>GetDiagnostics</c>) must distinguish <see cref="Unknown"/> — "nothing
/// is recorded because no compile has run since the last invalidation" —
/// from <see cref="Ok"/> — "the last compile actually succeeded". Returning
/// the former as the latter causes false-green diagnostics (edit → recycle →
/// diagnostics reports Ok → user navigates → fresh compile fails).
/// </summary>
public enum CompilationStatus
{
    /// <summary>No compile has completed since the last invalidation.</summary>
    Unknown,

    /// <summary>
    /// Caller has requested a compile (set on the NodeType MeshNode via stream.Update);
    /// the per-NodeType hub's compile watcher will pick this up, flip to
    /// <see cref="Compiling"/>, and run Roslyn. Used as the trigger signal in the
    /// stream-update / sync-stream-broadcast slow path.
    /// </summary>
    Pending,

    /// <summary>A compile is currently running.</summary>
    Compiling,

    /// <summary>The most recent compile completed successfully.</summary>
    Ok,

    /// <summary>The most recent compile failed; <c>CompilationError</c> on the NodeTypeDefinition has the text.</summary>
    Error
}
