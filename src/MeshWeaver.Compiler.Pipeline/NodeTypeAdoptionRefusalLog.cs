using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>The ONE place an adopt-time framework-identity refusal (<see cref="NodeTypeBuildIdentity"/>,
/// #3472) is written to the log</b> — Systemorph/MeshWeaver#5066.
///
/// <para><b>What was wrong.</b> Every load path the #3472 gate guards logged its refusal at
/// <c>Error</c> on EVERY probe, with a recovery sentence that began "Nothing further is required:
/// this is the state the compile watcher already heals." During an unconverged roll — two
/// replica generations on two framework identities, one re-stamping the shared record, the other
/// refusing it — the schema probe alone wrote 507 such lines in 60 minutes on one pod, every one
/// naming the same two identities. The gate was right every time; the REPORT was wrong twice
/// over, and it is what put an expected roll transition into the incident pipeline.</para>
///
/// <list type="number">
///   <item><b>One level for two populations whose required responses are opposite.</b> Where this
///     process CAN compile locally, the type's own hub rebuilds the build against the live framework
///     and restamps the record — the refusal is the ordinary state after any platform roll, and it
///     is logged at <see cref="LogLevel.Warning"/>: worth seeing, never an incident. On a
///     <c>Modules:RequirePrebuilt</c> mesh no local compile is possible, so NOTHING on this process
///     heals it; only a rebake and republish for this framework identity does. That refusal is
///     actionable and stays at <see cref="LogLevel.Error"/>. The predicate is
///     <see cref="PrebuiltAssemblySeeder.RequirePrebuilt(IServiceProvider?)"/> — the same one the
///     compile watcher's adopt-only gate reads, so the log and the behaviour cannot disagree about
///     whether a compile will happen.</item>
///   <item><b>Once per (site, NodeType, record framework identity, healability mode), never once
///     per probe.</b> A reloaded <c>Modules:RequirePrebuilt</c> changes the level, so it re-reports. The
///     500th identical line carries nothing the first did not. A NEW record identity (the next
///     roll, or a different generation re-stamping) is a new fact and is reported again; a record
///     whose assembly version climbs under the SAME identity (the #5066 series: v481 → v1338, all
///     <c>s7e280d1</c>) is the same fact and is not. Repeats go to <see cref="LogLevel.Debug"/>.</item>
/// </list>
///
/// <para>🚨 <b>What this does NOT watch.</b> A roll that never converges — two generations
/// re-stamping one record indefinitely — is not detectable from a single refusal, and after this
/// change it is no longer announced by a line flood either. Its instrument is the live record
/// census (<c>bake-report</c>'s <c>LIVE RECORD CENSUS</c>, #4632), which counts records stamped by
/// another generation AFTER this replica booted and degrades on exactly that set, and
/// <c>Ops/Status/&lt;id&gt;</c>'s <c>converged</c>/<c>generations</c>.</para>
///
/// <para>Mesh-scoped singleton (registered in <c>AddGraph</c>) with an instance map only — NO
/// static state, so a test mesh and the next one never share what was "already reported". The map
/// is bounded by sites × NodeTypes × framework identities seen by this process.</para>
/// </summary>
public sealed class NodeTypeAdoptionRefusalLog(IServiceProvider services)
{
    private readonly ConcurrentDictionary<(string Site, string NodeTypePath, string RecordIdentity, bool CanCompileLocally), byte> reported = new();

    /// <summary>
    /// Whether a refusal on this mesh is healed here by a local compile — the inverse of
    /// <see cref="PrebuiltAssemblySeeder.RequirePrebuilt(IServiceProvider?)"/>, read at report time
    /// so a reloaded configuration is honoured.
    /// </summary>
    public bool CanCompileLocally => !PrebuiltAssemblySeeder.RequirePrebuilt(services);

    /// <summary>
    /// Logs the refusal of <paramref name="definition"/>'s build at the level its healability
    /// warrants, the FIRST time this (site, type, record identity) is seen; later calls log at
    /// Debug only.
    /// </summary>
    /// <param name="logger">The call site's logger (its category names the site in the log).</param>
    /// <param name="site">A short, stable name of the load path refusing — part of the de-duplication
    /// key, because each site states a different consequence.</param>
    /// <param name="nodeTypePath">The NodeType whose build is refused.</param>
    /// <param name="definition">Its record, as read.</param>
    /// <param name="consequence">One English sentence: what THIS site does instead.</param>
    /// <returns><see langword="true"/> when the refusal was reported at its full level, i.e. this
    /// was the first sighting.</returns>
    public bool Report(
        ILogger? logger, string site, string nodeTypePath, NodeTypeDefinition definition, string consequence)
    {
        // The healability mode is part of the key: a reloaded Modules:RequirePrebuilt changes
        // what the refusal MEANS (and its level), so it is a new fact and is reported again.
        var canCompileLocally = CanCompileLocally;
        var key = (site, nodeTypePath, definition.CompiledFrameworkVersion ?? string.Empty, canCompileLocally);
        if (!reported.TryAdd(key, 0))
        {
            logger?.LogDebug(
                "[AdoptionRefused] {Site} {NodeTypePath}: still refused (record identity {RecordIdentity}, "
                + "assembly {Assembly}) — reported once already; not repeated (#5066)",
                site, nodeTypePath, definition.CompiledFrameworkVersion ?? "(none)",
                definition.LatestAssemblyPath ?? "(none)");
            return false;
        }

        logger?.Log(
            NodeTypeBuildIdentity.RefusalLogLevel(canCompileLocally),
            "[AdoptionRefused] {Site}: {Summary} {Consequence} {Recovery} Reported once per NodeType "
            + "and record framework identity at this site (#5066).",
            site,
            NodeTypeBuildIdentity.RefusalSummary(nodeTypePath, definition),
            consequence,
            NodeTypeBuildIdentity.RecoveryVerbFor(canCompileLocally));
        return true;
    }
}
