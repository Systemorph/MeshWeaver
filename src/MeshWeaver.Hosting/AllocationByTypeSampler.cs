using System.Collections.Immutable;
using System.Diagnostics.Tracing;

namespace MeshWeaver.Hosting;

/// <summary>Sampled allocation volume of one type inside one heartbeat window.</summary>
/// <param name="TypeName">The runtime's name for the allocated type, e.g. <c>System.Byte[]</c>.</param>
/// <param name="Bytes">The sampled bytes attributed to it (each sample stands for ~100&#160;KB).</param>
public sealed record TypeAllocation(string TypeName, long Bytes);

/// <summary>
/// What the process allocated between two heartbeat ticks, by type, as the runtime sampled it.
/// </summary>
/// <param name="TotalBytes">Every sampled byte in the window, across all types.</param>
/// <param name="Top">The heaviest types, largest first.</param>
public sealed record AllocationWindow(long TotalBytes, ImmutableList<TypeAllocation> Top)
{
    /// <summary>A window in which nothing was sampled — also what a host without the sampler reads.</summary>
    public static readonly AllocationWindow Empty = new(0, ImmutableList<TypeAllocation>.Empty);
}

/// <summary>
/// Names WHAT a process allocated, by type, from the runtime's own sampled allocation events —
/// the reading the multi-GiB heap step (MeshWeaver#5555) was missing.
///
/// <para>🚨 <b>Why an in-process sampler and not a heap dump.</b> The step is unpredictable (it has
/// landed at boot, at 01:00Z, at 03:31Z, on several replicas within seconds) and a dump is not free:
/// on a replica of this size <c>dotnet-dump collect --type Heap</c> froze the process for ~106&#160;s,
/// past the liveness budget, and restarted the container (<c>Doc/Architecture/PortalHeapIsHubs</c>).
/// Nobody can be standing at the pod with a dump tool when a step lands, and the dump would kill the
/// replica it is reading. The runtime already samples its own allocations — one
/// <c>GCAllocationTick</c> event per ~100&#160;KB allocated, carrying the type — so listening to
/// those costs one event per 100&#160;KB and names the type in-band, at the step, in the log the
/// fleet already keeps.</para>
///
/// <para>🚨 <b>Allocated is not retained.</b> The window counts what was ALLOCATED. A heap step is
/// what was allocated AND is still reachable, so the report names candidates, heaviest first; a type
/// that dominates a window whose heap grew by gigabytes is the leading one, and a heap dump of a
/// quiet replica can then confirm what roots it. It is a sample, so small types are noise and a
/// multi-GiB type is not.</para>
///
/// <para>Instance state only — the window is a field of this listener, which the heartbeat owns for
/// the life of the host. Nothing here is static except the constants.</para>
/// </summary>
public sealed class AllocationByTypeSampler : EventListener
{
    /// <summary>The runtime's event source.</summary>
    public const string RuntimeProviderName = "Microsoft-Windows-DotNETRuntime";

    /// <summary>The <c>GC</c> keyword — the one <c>GCAllocationTick</c> is raised under.</summary>
    private const EventKeywords GcKeyword = (EventKeywords)0x1;

    /// <summary><c>GCAllocationTick</c>'s event id (every version of it).</summary>
    private const int AllocationTickEventId = 10;

    /// <summary>How many types a window reports. The step is one or two types, never forty.</summary>
    public const int TopTypes = 8;

    // One immutable map, replaced by compare-and-swap on BOTH sides: Record folds a sample in with
    // ImmutableInterlocked.AddOrUpdate, Drain takes the map with Interlocked.Exchange. A sample
    // racing the drain therefore either lands in the map Drain took, or fails its CAS and is
    // retried against the fresh one — it is never added to a map nobody will read again.
    private ImmutableDictionary<string, long> window = ImmutableDictionary.Create<string, long>(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // Runs from the base constructor for sources that already exist. That is safe here: the only
        // state an event touches is `window`, and C# runs instance field initializers BEFORE the
        // base constructor call, so it is assigned before this can be reached.
        if (eventSource.Name == RuntimeProviderName)
            EnableEvents(eventSource, EventLevel.Verbose, GcKeyword);
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId != AllocationTickEventId || eventData.Payload is not { } payload
            || eventData.PayloadNames is not { } names)
            return;

        string? typeName = null;
        long bytes = 0;
        for (var i = 0; i < names.Count && i < payload.Count; i++)
        {
            switch (names[i])
            {
                case "TypeName":
                    typeName = payload[i] as string;
                    break;
                case "AllocationAmount64":
                    bytes = Convert.ToInt64(payload[i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "AllocationAmount" when bytes == 0:
                    bytes = Convert.ToInt64(payload[i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }

        if (bytes <= 0)
            return;

        Record(string.IsNullOrEmpty(typeName) ? "(unnamed)" : typeName, bytes);
    }

    /// <summary>Adds one sample to the current window.</summary>
    /// <param name="typeName">The allocated type.</param>
    /// <param name="bytes">The sampled bytes.</param>
    internal void Record(string typeName, long bytes) =>
        ImmutableInterlocked.AddOrUpdate(ref window, typeName, bytes, (_, sum) => sum + bytes);

    /// <summary>
    /// The window since the previous drain, heaviest types first, and a fresh window started.
    /// </summary>
    /// <returns>What was sampled since the last call.</returns>
    public AllocationWindow Drain() => Summarize(TakeWindow());

    /// <summary>
    /// Takes the WHOLE window since the previous take — every type, not only the heaviest — and
    /// starts a fresh one. <see cref="Drain"/> is this plus the Top-<see cref="TopTypes"/> summary;
    /// the record/drain race is a property of this take alone, so it is measured here, where no
    /// ranking can hide a sample.
    /// </summary>
    /// <returns>Every type sampled since the last take, with its summed bytes.</returns>
    internal ImmutableDictionary<string, long> TakeWindow() =>
        Interlocked.Exchange(ref window, window.Clear());

    private static AllocationWindow Summarize(ImmutableDictionary<string, long> drained)
    {
        if (drained.IsEmpty)
            return AllocationWindow.Empty;

        var total = 0L;
        foreach (var entry in drained)
            total += entry.Value;

        return new AllocationWindow(
            total,
            drained
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(TopTypes)
                .Select(entry => new TypeAllocation(entry.Key, entry.Value))
                .ToImmutableList());
    }
}
