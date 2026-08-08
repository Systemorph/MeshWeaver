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
    Error,

    /// <summary>
    /// The compile state could NOT BE DETERMINED: a settle wait or a registration
    /// lookup timed out, or a recorded assembly could not be resolved from the
    /// store. <b>Nothing is known to be wrong with the source</b> — this is an
    /// availability problem, and the remedy is to retry / wait, never "correct the
    /// code".
    ///
    /// <para>Kept distinct from <see cref="Error"/> precisely because a reader that
    /// cannot tell the two apart tells the author to fix code that compiles fine:
    /// a 60 s settle timeout used to be persisted as <c>Error</c> with the message
    /// "The operation has timed out.", and every instance page of that type then
    /// rendered the "There was a compilation error… Please correct the code"
    /// overlay (issue #641).</para>
    ///
    /// <para>🚨 Writers must treat this as a MARKER, never an answer: it may never
    /// overwrite a state another driver owns (<see cref="Pending"/> /
    /// <see cref="Compiling"/> — a compile is in flight and will write its own
    /// terminal state), a strictly better one (<see cref="Ok"/> — a usable build),
    /// or a never-compiled <c>null</c> (which the first-build kickoff needs).
    /// Appended LAST so the persisted ordinal of every existing member is
    /// unchanged.</para>
    /// </summary>
    Unavailable
}
