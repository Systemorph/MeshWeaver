using System;
using System.Globalization;

namespace MeshWeaver.Mesh.Diagnostics;

/// <summary>
/// A start-to-finish memory measurement for ONE named operation, so an existing log line can say
/// what that operation COST instead of leaving the growth to be inferred from a container metric
/// hours later.
///
/// <para>🚨 Why this exists. memex-cloud 2026-08-12: a portal pod went from 2.5 GB to 24.5 GB
/// (RSS 23.6 GB — real anonymous memory, not page cache) at 130–340 MB/min. Four candidate
/// explanations were eliminated one by one from OUTSIDE the process — the collectible-ALC lifecycle
/// (bounded, proven by NodeTypeRecompileAlcLeakTest), the LSP workspace cache (per NodeType, disposed
/// on version change), the batch bake (279 types for 2.5 GB), page cache (RSS says otherwise) —
/// because nothing inside the process ever reported what any single operation cost. Growth
/// correlated with GitSync re-imports and sync-stream churn, but correlation from a container gauge
/// cannot name a culprit.</para>
///
/// <para><b>Deliberately cheap, and it adds NO log volume.</b> <see cref="GC.GetTotalMemory(bool)"/>
/// is called with <c>forceFullCollection: false</c> — it never provokes a collection — and
/// <see cref="Environment.WorkingSet"/> is one read. Both are taken once per operation, and the
/// result is appended to log lines that already existed, because `Information` lines ship to Loki
/// and that is not free.</para>
///
/// <para><b>Read it as a hint, not an accountant.</b> The managed figure is un-collected heap at two
/// instants, so a GC between them can make a real allocation look free or a quiet operation look
/// negative. What it is good for is the shape: an operation that repeatedly costs tens or hundreds of
/// MB and never gives it back is the one to investigate, and that is exactly what was missing.</para>
/// </summary>
/// <param name="ManagedAtStart">Managed heap bytes when the operation began.</param>
/// <param name="WorkingSetAtStart">Process working set bytes when the operation began.</param>
public readonly record struct MemoryDelta(long ManagedAtStart, long WorkingSetAtStart)
{
    /// <summary>Takes the opening measurement. Cheap enough to call per operation, not per item.</summary>
    public static MemoryDelta Start() => new(GC.GetTotalMemory(false), Environment.WorkingSet);

    /// <summary>Managed-heap growth since <see cref="Start"/>, in bytes (can be negative).</summary>
    public long ManagedGrowth => GC.GetTotalMemory(false) - ManagedAtStart;

    /// <summary>Working-set growth since <see cref="Start"/>, in bytes (can be negative).</summary>
    public long WorkingSetGrowth => Environment.WorkingSet - WorkingSetAtStart;

    /// <summary>
    /// Renders both deltas for a log line, signed and in MB — e.g.
    /// <c>"managed +412 MB, working set +1183 MB"</c>. Signed on purpose: a NEGATIVE managed delta
    /// next to a POSITIVE working set is the signature of memory that left the managed heap and did
    /// not leave the process.
    /// </summary>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "managed {0}{1} MB, working set {2}{3} MB",
            ManagedGrowth < 0 ? "-" : "+", Math.Abs(ManagedGrowth) / (1024 * 1024),
            WorkingSetGrowth < 0 ? "-" : "+", Math.Abs(WorkingSetGrowth) / (1024 * 1024));
}
