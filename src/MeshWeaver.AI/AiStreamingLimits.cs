namespace MeshWeaver.AI;

/// <summary>
/// Mesh-scoped override for the hard wall-clock cap on a single AI streaming round
/// (see <c>ThreadExecution.MaxStreamingDuration</c> for the production default and the
/// full rationale — issue #147). Register as a singleton in the mesh's service
/// collection to override the cap; when absent, the production default applies.
/// <para>Primary consumer: tests, which register a short cap to pin the
/// streaming-timeout behavior deterministically (an instance singleton owned by the
/// mesh — never a mutable static — per the no-static-state rule).</para>
/// </summary>
/// <param name="MaxStreamingDuration">
/// The wall-clock ceiling for one streaming round at the AI I/O boundary. When the
/// ceiling is reached, the round's linked CancellationTokenSource fires and the round
/// terminates as a graceful ERROR (response cell <c>Status = Error</c>), never a
/// silent hang.
/// </param>
public sealed record AiStreamingLimits(TimeSpan MaxStreamingDuration);
