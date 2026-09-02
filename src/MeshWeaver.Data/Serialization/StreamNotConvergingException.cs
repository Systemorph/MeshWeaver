namespace MeshWeaver.Data.Serialization;

/// <summary>
/// A mirror asked its owner for a fresh authoritative snapshot, was ACKNOWLEDGED, and never
/// received one — repeatedly. Terminal: the stream that carries this can never emit again.
///
/// <para>🚨 <b>This exception exists so that a lost frame cannot become a SILENT permanent wedge
/// (Systemorph/MeshWeaver#1384).</b> The recovery path it ends is event-driven by design and has no
/// timer anywhere in it: a frame whose <c>BasedOnVersion</c> does not chain onto the last one this
/// mirror applied PROVES a frame was lost in transport, and the only sound reaction is a fresh
/// snapshot from the owner. #2654 made that recovery survive a single lost answer — the resync gate
/// is released by the re-ask's round trip, so the next proven gap earns one new re-ask. What it
/// could not do is TERMINATE: when the owner→subscriber leg keeps eating this stream's snapshots,
/// every re-ask is acknowledged, every acknowledgement releases the gate, every answer dies on the
/// way back, and the mirror re-asks for the rest of the process's life. Its subscribers see none of
/// it — a layout area holds "awaiting first data", a data-bound view holds its last value, and no
/// error and no completion ever reaches them.</para>
///
/// <para>Measured on memex-cloud, 2026-09-01, on <c>Event/SavGeneralversammlung2026/Talk</c>:
/// <c>[SYNC_STREAM] Frame loss detected … incoming Patch v13 chains onto v12 but the last applied
/// frame is v11</c>, followed by <c>Layout area 'Present' … was torn down having never rendered</c>
/// — while plain node reads on the same path answered instantly. Recycling the pod that held the
/// activation did not clear it: the owner was healthy, and the wedge lived entirely in a subscriber
/// that had not been told anything was wrong.</para>
///
/// <para>Fault, therefore, rather than wait. A faulted stream is the one state every consumer
/// already knows how to act on: the store's terminal error reaches every reader,
/// <c>StreamLiveness.IsUsable</c> stops serving the stream from the workspace's caches, and the
/// next natural caller opens a fresh stream that subscribes from scratch. Nothing here retries —
/// re-establishing is the subscriber's decision, taken because it was finally told.</para>
/// </summary>
public sealed class StreamNotConvergingException : SynchronizationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StreamNotConvergingException"/> class.
    /// </summary>
    /// <param name="message">A message naming the stream, its owner and how many acknowledged
    /// fresh-snapshot requests went unanswered.</param>
    public StreamNotConvergingException(string message) : base(message) { }
}
