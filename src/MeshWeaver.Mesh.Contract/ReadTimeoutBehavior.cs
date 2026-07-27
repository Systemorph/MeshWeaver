namespace MeshWeaver.Mesh;

/// <summary>
/// What a one-shot mesh read (<see cref="MeshNodeStreamExtensions.GetMeshNode"/>) does when
/// its wall-clock budget elapses before the owning per-node hub answers.
///
/// <para>The distinction exists because a stalled read and a missing node are different
/// facts. Collapsing both into <c>null</c> — the shape this API shipped with — let a 60 s
/// mesh stall masquerade as "node not found": callers substituted defaults, rendered empty
/// areas and compiled against missing NodeType definitions, while the stall itself left
/// nothing louder than a Debug log. See the ThreadAgentIntegrationTest CI failure of
/// 2026-07-26, where the read burned its full 60 s budget, returned <c>null</c>, and the
/// test still passed — only a dispose-time watchdog caught it, blaming the wrong thing.</para>
/// </summary>
public enum ReadTimeoutBehavior
{
    /// <summary>
    /// Default. Surface a <see cref="System.TimeoutException"/> naming the path, the elapsed
    /// time and the reading hub's in-flight snapshot (queue depths, the message it is
    /// executing, outstanding response callbacks). The caller decides how to degrade —
    /// but it can never mistake the stall for an absent node.
    /// </summary>
    Throw,

    /// <summary>
    /// Emit <c>null</c>, exactly as a genuine not-found does. Legitimate ONLY where
    /// "indeterminate ⇒ treat as absent" is the caller's documented, deliberate contract —
    /// a cosmetic fallback, or an existence probe whose follow-up write is an idempotent
    /// upsert. The timeout is still logged at Warning with the same diagnostics, so opting
    /// in suppresses the exception, never the evidence.
    /// </summary>
    EmitNull
}
