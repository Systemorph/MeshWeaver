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
    Unavailable,

    /// <summary>
    /// 🚨 <b>DERIVED, NEVER PERSISTED.</b> The last compile SUCCEEDED — for a framework build
    /// identity that is not this process's, so the bytes it named cannot be loaded here. Nothing is
    /// wrong with the source and nothing is unavailable; the remedy is a local recompile against
    /// the live framework, which the compile watcher drives on its own.
    ///
    /// <para><b>Why a fourth state rather than reusing one.</b> Each of the three candidates
    /// carries a different remedy, and all three are wrong here.
    /// <see cref="Error"/> says <i>correct the code</i> — the code is fine, and conflating the two
    /// is exactly the #641 defect in the other direction. <see cref="Unavailable"/> says <i>retry
    /// or wait</i> — the state is fully determined and waiting is precisely what does not help.
    /// <see cref="Ok"/> is the lie this member exists to stop: on 2026-09-06 two CRM types on a
    /// client portal read <c>Ok</c> for two and a half hours while every one of their per-instance
    /// hubs was dead, because <c>Ok</c> is a claim scoped to
    /// <c>NodeTypeDefinition.CompiledFrameworkVersion</c> and every instrument read only the
    /// verdict, never its scope (Systemorph/MeshWeaver#3472).</para>
    ///
    /// <para>🚨 <b>It is computed by the READER and is never written to a record.</b> "Can this
    /// build be loaded" is answerable only relative to a process, so two replicas on two images
    /// legitimately disagree — and persisting a reader-relative verdict into a shared record is how
    /// the same incident's ping-pong (#3395) was made. The record keeps saying what the compiler
    /// did; only the report is scoped. <c>NodeTypeBuildIdentity.ReportedStatus</c> is the one
    /// function that derives it. Appended LAST so the persisted ordinal of every existing member is
    /// unchanged.</para>
    /// </summary>
    Foreign
}
