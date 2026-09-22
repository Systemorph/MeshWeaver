using System.Diagnostics;

// The pipeline types live in the Graph.Configuration namespace beside NodeTypeDefinition,
// whatever assembly they ship in; ProcessBoot follows NodeTypeBuildIdentity.
namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>When THIS process began — the one boundary every "since boot" reading splits on.</b>
///
/// <para>A NodeType record carries the framework identity of whoever last compiled it
/// (<see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>)
/// and when (<see cref="NodeTypeDefinition.LastCompileSucceededAt"/>).
/// During a rolling update two platform generations serve one mesh for the whole termination
/// grace, and a stamp that names a framework this process does not run has two very different
/// meanings depending on WHEN it landed: before this process started it is the ordinary state
/// after a platform roll (the previous image's build, which the compile watcher heals); after
/// this process started it means a replica on ANOTHER image owns the type now, and a heal from
/// here would re-key the record backwards and start the ping-pong that took memex's control
/// instance down mid-roll (34 records re-keyed by the draining replicas within minutes of the new
/// one booting). The live record census (<c>NodeTypeLiveRecordCensus</c>) and the bind-time
/// framework-stale decision (<c>NodeTypeEnrichmentHelpers</c>) must therefore split on the SAME
/// instant, or one would report a cross-stamp the other acted on as a heal.</para>
///
/// <para>The process start time, not a hosted service's <c>StartAsync</c>: a service starts
/// seconds after the process and nothing this process compiles can be stamped before it exists,
/// so the earlier boundary is the honest one and it is the same for every reader. Read once from
/// the runtime and never written again — an immutable fact about the process, which is the one
/// shape of static state the platform admits.</para>
/// </summary>
public static class ProcessBoot
{
    /// <summary>The UTC instant this process started, as the runtime reports it.</summary>
    public static readonly DateTimeOffset StartedAtUtc = ReadStart();

    private static DateTimeOffset ReadStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception)
        {
            // A sandbox that refuses the process handle still needs SOME boundary; "now" is the
            // latest instant that is certainly not earlier than the process, so a reader errs
            // toward "before boot" — the benign side — never toward reporting a cross-stamp that
            // did not happen.
            return DateTimeOffset.UtcNow;
        }
    }
}
