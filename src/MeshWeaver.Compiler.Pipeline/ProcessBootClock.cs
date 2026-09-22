using System.Diagnostics;

// The pipeline types live in the Graph.Configuration namespace beside NodeTypeDefinition,
// whatever assembly they ship in; ProcessBootClock follows NodeTypeBuildIdentity.
namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>When THIS process began — the one boundary every "since boot" reading splits on.</b>
///
/// <para>A NodeType record carries the framework identity of whoever last compiled it
/// (<see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>) and when
/// (<see cref="NodeTypeDefinition.LastCompileSucceededAt"/>). During a rolling update two
/// platform generations serve one mesh for the whole termination grace, and a stamp that names a
/// framework this process does not run has two very different meanings depending on WHEN it
/// landed: before this process started it is the ordinary state after a platform roll (the
/// previous image's build, which the compile watcher heals); after this process started it means
/// a replica on ANOTHER image owns the type now, and a heal from here would re-key the record
/// backwards and start the ping-pong that took memex's control instance down mid-roll (34 records
/// re-keyed by the draining replicas within minutes of the new one booting). The live record
/// census (<c>NodeTypeLiveRecordCensus</c>) and the bind-time framework-stale decision
/// (<c>NodeTypeEnrichmentHelpers</c>) must therefore split on the SAME instant, or one would
/// report a cross-stamp the other acted on as a heal.</para>
///
/// <para>The process start time, not a hosted service's <c>StartAsync</c>: a service starts
/// seconds after the process and nothing this process compiles can be stamped before it exists,
/// so the earlier boundary is the honest one and it is the same for every reader.</para>
///
/// <para>🚨 <b>A mesh-scoped singleton, read ONCE at registration on the host's startup thread —
/// never a static touched from a hub turn.</b> <see cref="ReadFromProcess"/> asks the OS for the
/// process's start time, which is a metadata read (<c>/proc/self/stat</c> on Linux); the bind path
/// that consumes the boundary runs on a per-node hub turn, where a blocking read has no place. So
/// the hosting registration reads it while it configures services, and a consumer resolves the
/// instance off its mesh hub's provider. A mesh with no registration (a host that never registered
/// persistence, a bare test mesh) resolves <see langword="null"/> and takes the benign side: nothing
/// is "after boot", so nothing is yielded on and the heal behaves as it always did.</para>
/// </summary>
public sealed class ProcessBootClock
{
    /// <summary>Creates a clock for a known start instant — injected in tests and staged rolls.</summary>
    public ProcessBootClock(DateTimeOffset startedAtUtc) => StartedAtUtc = startedAtUtc;

    /// <summary>The UTC instant this process started.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>
    /// Reads the running process's start time from the OS. Called at service registration, on the
    /// startup thread; a runtime that refuses the process handle answers "now" — the latest instant
    /// that is certainly not earlier than the process, so a reader errs toward "before boot" (the
    /// benign side) rather than toward reporting a cross-stamp that did not happen.
    /// </summary>
    public static ProcessBootClock ReadFromProcess()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new ProcessBootClock(new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (Exception)
        {
            return new ProcessBootClock(DateTimeOffset.UtcNow);
        }
    }
}
