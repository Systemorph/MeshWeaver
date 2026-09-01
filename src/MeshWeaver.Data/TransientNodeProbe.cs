namespace MeshWeaver.Data;

/// <summary>
/// Marker declaring that a hub exists ONLY to be interrogated for type information and will be
/// disposed in the same breath — set via <c>AsTransientNodeProbe()</c> (MeshWeaver.Graph), read
/// wherever long-lived machinery would otherwise be installed on it.
///
/// <para>It lives HERE, not beside the extension that sets it, because
/// <see cref="DataContext.InitializeDataSources"/> must read it: a probe's data context is
/// <b>configured but its sources are never started</b>. Configuration (<see cref="DataContext.Initialize"/>,
/// inside <c>Build</c>) is what fills the type registry the probe reads; starting the sources is
/// what eagerly opens each one's synchronization stream — a <c>sync/</c> sub-hub apiece — on the
/// hub's init turn, machinery a hub that lives for microseconds has no use for. That init turn
/// RACES the probe's immediate dispose: usually creation wins, occasionally dispose wins and
/// <c>HostedHubsCollection</c> logs its "Rejecting hosted hub creation … during disposal" warning.
/// <c>ProbeHubCostTest</c> pins that warning as a teardown fault, and it flaked CI red twice on
/// 2026-08-22 (main <c>1b0b834</c> and PR #2062) — switching continuous delivery off both times,
/// since CD builds nothing while main's required check is red.</para>
///
/// <para>Streams are still created LAZILY when a request actually needs one — the guard removes
/// only the eager start nobody consumes.</para>
///
/// <para>🚨 <b>A probe hub HAS NO MESH NODE, and the read path enforces that.</b> The instance
/// configuration a probe applies is content written for a REAL per-node hub, where the hub's
/// address IS its mesh path — so a loader deriving a path from <c>Hub.Address</c> and reading it is
/// ordinary, correct code that collapses onto the probe's own synthetic address here. Posted to the
/// probe, such a read parks behind the <c>DataContextInit</c> / <c>MeshNodeInit</c> gates that the
/// probe's own data-context initialization opens — a CYCLE whose only exit was the read's full
/// budget, ending on a <c>CancellationTokenSource</c> timer thread (Systemorph/MeshWeaver#2468).
/// <c>MeshNodeStreamExtensions.GetMeshNodeOutcome</c> therefore answers a probe's read of its OWN
/// address <c>Absent</c> immediately — the truthful answer, since there is no node there and never
/// will be. Reads of any REAL path from a probe are untouched.</para>
///
/// <para>🚨 There are TWO own-node read seams, and both are guarded (see
/// <see cref="TransientProbeAddresses"/>). The other is the process-wide
/// <c>IMeshNodeStreamCache.GetStream(path, options)</c> — the one in-mesh NodeType content
/// actually uses, because it is the only own-node read that answers before the hub's init gates
/// open. Unguarded it evaluated the caller's permissions on the synthetic address and threw
/// "lacks Read permission on '$model-probe/…'", or routed and died on "No node found at
/// '$model-probe/…'"; either faulted the virtual data source whose provider issued it
/// (Systemorph/MeshWeaver#2894). <c>MeshNodeStreamCache.GetStreamRaw</c> answers a probe address
/// with an EMPTY stream — the stream-shaped twin of <c>Absent</c>.</para>
/// </summary>
/// <param name="StartDataSources">Whether the probe still STARTS its data sources on the init
/// turn. Default TRUE — the pre-existing behaviour, which probes that snapshot actual data (the
/// node-type MODEL probe reads instance content and schema through its sources) depend on:
/// skipping the start universally starved their <c>Initialized</c> tasks and six probe tests went
/// red (NodeTypeModelProbeTest, StaleStampRootBindingTest, IncrementalUpdateTest). Only a probe
/// that reads NOTHING BUT THE TYPE REGISTRY — the schema validation/lookup probes, whose eager
/// sync/ stream raced its own dispose into the CD-killing ProbeHubCostTest flake — opts out.</param>
public sealed record TransientNodeProbe(bool StartDataSources = true);

/// <summary>
/// The synthetic hub addresses a transient node probe is created under, and the one predicate
/// that recognises them.
///
/// <para>🚨 <b>These addresses can never carry a mesh node.</b> They are minted with a fresh
/// <see cref="System.Guid"/> for a hub that lives for microseconds and is disposed in the same
/// breath (<see cref="TransientNodeProbe"/>), so "is there a node at this path?" has ONE answer,
/// for every reader, forever: no. Every read seam that can be handed such a path must therefore
/// answer it directly rather than route it — see
/// <c>MeshNodeStreamExtensions.GetMeshNodeOutcome</c> (answers <c>Absent</c>) and
/// <c>MeshNodeStreamCache.GetStreamRaw</c> (answers an empty stream).</para>
///
/// <para>The constants live here, next to the marker that states the contract, because EVERY
/// producer (<c>NodeTypeDataModelAreas.ProbeInstanceModel</c>, the schema probe in
/// <c>MeshDataSource</c>, and the schema validation / lookup probes in <c>MeshOperations</c>)
/// and every consuming guard have to agree on the literal. A guard whose prefix drifts from its
/// producer's is a guard that silently stops firing — so the producers mint their address FROM
/// these constants, which is what keeps <see cref="IsProbeAddress"/> exhaustive.</para>
/// </summary>
public static class TransientProbeAddresses
{
    /// <summary>Prefix of the model probe's address — <c>NodeTypeDataModelAreas.ProbeInstanceModel</c>.</summary>
    public const string ModelProbePrefix = "$model-probe/";

    /// <summary>Prefix of the schema probe's address — the schema lookup in <c>MeshDataSource</c>.</summary>
    public const string SchemaProbePrefix = "$schema-probe/";

    /// <summary>Prefix of the content-validation probe's address — <c>MeshOperations</c>.</summary>
    public const string SchemaValidationProbePrefix = "_schema_validation/";

    /// <summary>Prefix of the schema-lookup probe's address — <c>MeshOperations</c>.</summary>
    public const string SchemaLookupProbePrefix = "_schema_lookup/";

    /// <summary>
    /// True when <paramref name="path"/> is a transient probe hub's own synthetic address — a
    /// path that is not, and can never become, a mesh node.
    /// </summary>
    /// <param name="path">The mesh path a reader was handed.</param>
    /// <returns><c>true</c> for a probe address, <c>false</c> for every real path.</returns>
    public static bool IsProbeAddress(string? path)
        => path is not null
           && (path.StartsWith(ModelProbePrefix, StringComparison.Ordinal)
               || path.StartsWith(SchemaProbePrefix, StringComparison.Ordinal)
               || path.StartsWith(SchemaValidationProbePrefix, StringComparison.Ordinal)
               || path.StartsWith(SchemaLookupProbePrefix, StringComparison.Ordinal));
}
