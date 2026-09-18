using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>The deployment gate's denominator, read without re-opening every publication the store has
/// ever held (#4742).</b>
///
/// <para><b>What broke.</b>
/// <see cref="PublishedBundleCatalogue.EverSealedBundles(string?, ILogger?)"/> walks EVERY
/// framework-identity directory under <c>PreWarm:PrebuiltBundleRoot</c> — on AKS an Azure Files
/// share — listing each one's sources and opening each source's publication pointer and completion
/// sentinel. That walk is the first thing
/// <c>ReleaseAvailabilityService.IsUpdatable</c>/<c>SelectRollTarget</c> do, inside a verdict budget,
/// and the store is APPEND-ONLY: CD cuts roughly sixty sets a day and the framework identity moves
/// about as often as a set is cut, so the directory count only ever grows. A fixed budget over a
/// monotonically growing walk is a <i>when</i>, not an <i>if</i> — and the when arrived. Measured on
/// memex.meshweaver.cloud 2026-09-18 at 18:45:36Z and 19:16:36Z, on two freshly sealed candidates:
/// <c>the availability check did not answer within 60s</c> both times, which is a HOLD, so the public
/// instance sat 1,198 commits behind <c>main</c> while CD kept sealing.
/// <see href="../SelfUpdateFreeze">Why the Fleet Stopped Rolling Itself</see> predicted exactly this
/// and named the remedy: the denominator must not be re-read from the share on every tick.</para>
///
/// <para><b>The shape.</b> A SOURCE directory — <c>&lt;root&gt;/&lt;identity&gt;/&lt;source&gt;</c> —
/// is the smallest thing a publisher writes as a whole, so it is the unit this remembers: what it
/// declared, keyed by its full path and its <see cref="FileSystemInfo.LastWriteTimeUtc"/>. A read
/// still LISTS the root and each identity, so a new identity is always seen, a new source is always
/// seen and a pruned one always drops out; what it no longer does is re-open the publication pointer
/// and completion sentinel of a source whose directory has not changed. The listings are cheap
/// (one directory response each); the opens were the cost.</para>
///
/// <para>🚨 <b>Why the source directory's stamp is the RIGHT signal, and the identity's is not.</b>
/// The identity directory's stamp moves when a source is added or removed under it and NOT when a
/// source is republished in place — and a republication in place is routine, not exotic: a satellite
/// re-bakes into a platform identity that has not moved every day its own build runs. Keying on the
/// identity would therefore freeze a package OUT of the denominator for as long as that identity
/// stayed newest, which exempts it from the gate (#3461) — the one direction that must never happen.
/// The source directory's stamp moves on every publication path there is: in the generation layout a
/// republish creates <c>&lt;source&gt;/&lt;token&gt;/</c> and rewrites <c>_current</c>, both entries
/// of the source directory; in the flat layout it removes and rewrites <c>_complete</c> in the source
/// directory itself.</para>
///
/// <para>🚨 <b>And the argument closes in the direction that matters.</b> The stamp detects any change
/// to the source directory's ENTRY SET, and a republication that ADDS a package necessarily adds an
/// entry — a bundle file, or a whole generation directory. The only change it cannot see is a rewrite
/// of <c>_complete</c> in place with no entry added (writing an existing file does not move its
/// parent's stamp), and that can only re-word or SHRINK a declaration, which can only make the gate
/// HOLD. A generation directory is immutable by construction, so the publisher does not do this at
/// all.</para>
///
/// <para>🚨 <b>How fail-closed is preserved otherwise</b>, clause by clause, because a cache that
/// turned a hold into a roll would be far worse than the freeze it removes:</para>
/// <list type="bullet">
///   <item><b>A refusal is never remembered.</b> An unreadable root, an unfollowable publication
///     pointer, an enumeration fault — each returns <see cref="SealedBundleFloor.Unreadable"/> and
///     writes nothing, so one transient share fault cannot latch into a permanent verdict and the
///     very next tick re-reads from scratch. (The <c>PromiseCache</c> contract's reason, applied to a
///     read that repeats rather than one that happens once: a cached terminal is a permanent
///     fault.)</item>
///   <item><b>A source carrying no seal is not remembered either</b>, and this is what makes the
///     design safe on a file system whose directory stamps are COARSE. A flat-layout republish
///     unseals, rewrites and re-seals; if all of that lands inside one stamp granule the stamp does
///     not move, and a reading taken mid-way would be remembered as "declares nothing" — a package
///     silently exempted. Never remembering an unsealed reading closes that: such a source is
///     re-read every tick until it settles, which is the old cost for the handful that are actually
///     mid-publication.</item>
///   <item><b>Nothing about the CANDIDATE is cached.</b> The target release's own publication
///     (<c>ArtifactsForIdentity</c>, the marker, the surface, the module set) is read fresh on every
///     tick for every candidate. Only the denominator's history — the part #3441 made monotone by
///     design — is remembered.</item>
///   <item><b>Eviction happens only after a SUCCESSFUL, COMPLETE enumeration</b>, and only for the
///     root that enumeration covered. A refusal or a partial walk returns before the prune, so an
///     unreadable share can never evict a reading it merely failed to reach.</item>
/// </list>
///
/// <para><b>Never static.</b> Held as an instance field on a mesh-scoped singleton (registered by
/// <c>AddSelfUpdate</c>), so its lifetime is the mesh's and it neither bleeds across tests nor
/// survives mesh disposal — <see href="../NoStaticState">No Static State</see>.</para>
/// </summary>
public sealed class SealedBundleFloorCache
{
    /// <summary>One source directory as this process last read it.</summary>
    /// <param name="Root">The published root it was read under, so two roots in one process cannot
    /// evict each other's entries.</param>
    /// <param name="Stamp">The directory's write stamp when it was read — the invalidation signal.</param>
    /// <param name="Declared">What its seal declared.</param>
    private sealed record Observed(string Root, DateTime Stamp, ImmutableArray<string> Declared);

    /// <summary>🚨 An INSTANCE field on a mesh-scoped singleton, never static — and a
    /// <c>ConcurrentDictionary</c> because two availability reads can overlap (the gate and the roll
    /// selector each take one). Keyed by the source directory's full path.</summary>
    private readonly ConcurrentDictionary<string, Observed> observed = new(StringComparer.Ordinal);

    private long sourcesRead;
    private long sourcesRecalled;
    private long sourcesForgotten;

    /// <summary>How many source publications this cache has actually opened on the share,
    /// cumulatively. The claim of #4742 IS this number: it must grow with NEW or CHANGED sources and
    /// stand still over unchanged ones. Read by <c>SealedBundleFloorCacheTest</c> and reported in the
    /// read's own log line, which is the operator's evidence that the denominator is no longer
    /// re-opened end to end.</summary>
    public long SourcesRead => Interlocked.Read(ref sourcesRead);

    /// <summary>How many source publications this cache has answered from memory, cumulatively — the
    /// round trips against the share that the fix does not make.</summary>
    public long SourcesRecalled => Interlocked.Read(ref sourcesRecalled);

    /// <summary>How many remembered sources this cache has dropped because a complete enumeration no
    /// longer found them, cumulatively — what retention prunes, and the reason memory tracks the
    /// LIVE store rather than every publication ever seen.</summary>
    public long SourcesForgotten => Interlocked.Read(ref sourcesForgotten);

    /// <summary>How many source publications this cache is currently holding. Bounded by the store,
    /// not by the process's age.</summary>
    public int Remembered => observed.Count;

    /// <summary>
    /// Reactive form — the file-system leaf runs on the caller's I/O pool, never on a hub action
    /// block, exactly as <see cref="PublishedBundleCatalogue.Observe"/> does. Cold: the read happens
    /// on subscribe.
    /// </summary>
    /// <param name="pool">The file-system I/O pool.</param>
    /// <param name="publishedRoot">The published bundle root, or null when this deployment consumes
    /// no CI bakes.</param>
    /// <param name="logger">Diagnostics.</param>
    public IObservable<SealedBundleFloor> Observe(
        IIoPool pool, string? publishedRoot, ILogger? logger = null) =>
        pool.InvokeBlocking(_ => Read(publishedRoot, logger));

    /// <summary>
    /// The denominator, answering the same question as
    /// <see cref="PublishedBundleCatalogue.EverSealedBundles(string?, ILogger?)"/> and over the same
    /// contract — every failure becomes a <see cref="SealedBundleFloor.Unreadable"/> rather than an
    /// exception, because the caller's fail-safe answer to "could not read" is already HOLD.
    /// Synchronous: call it from an I/O pool leaf (<see cref="Observe"/>), never from a hub thread.
    /// </summary>
    /// <param name="publishedRoot">The published bundle root.</param>
    /// <param name="logger">Diagnostics.</param>
    public SealedBundleFloor Read(string? publishedRoot, ILogger? logger = null)
    {
        if (PublishedBundleCatalogue.RootRefusal(publishedRoot) is { } rootRefusal)
            return SealedBundleFloor.Unreadable(rootRefusal);

        var bundles = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var identities = 0;
        var read = 0;
        var recalled = 0;
        string rootKey;
        try
        {
            var root = new DirectoryInfo(publishedRoot!);
            rootKey = root.FullName;
            foreach (var identityDirectory in root
                         .EnumerateDirectories()
                         .OrderBy(d => d.FullName, StringComparer.Ordinal))
            {
                if (PublishedBundleCatalogue.IsReleaseMarkerDirectory(identityDirectory.FullName))
                    continue;

                var declaredHere = new List<string>();
                foreach (var sourceDirectory in identityDirectory
                             .EnumerateDirectories()
                             .OrderBy(d => d.FullName, StringComparer.Ordinal))
                {
                    visited.Add(sourceDirectory.FullName);
                    // The stamp rides the enumeration the loop already performed — one metadata read,
                    // against the pointer probe plus the sentinel open that a re-read costs.
                    var stamp = sourceDirectory.LastWriteTimeUtc;
                    if (observed.TryGetValue(sourceDirectory.FullName, out var known)
                        && known.Stamp == stamp
                        && string.Equals(known.Root, rootKey, StringComparison.Ordinal))
                    {
                        recalled++;
                        declaredHere.AddRange(known.Declared);
                        continue;
                    }

                    read++;
                    var declaration = PublishedBundleCatalogue.DeclaredBundlesOfSource(
                        sourceDirectory.FullName, logger);
                    // 🚨 NOT remembered. See the class remarks: a cached refusal is a permanent freeze.
                    if (declaration.Refusal is not null)
                        return SealedBundleFloor.Unreadable(declaration.Refusal);
                    if (declaration.Declared is null)
                        // No seal at all — mid-publication, or a bake that died before sealing. It
                        // contributes nothing AND is not remembered: the sentinel is written last, so
                        // remembering "declares nothing" would shrink the denominator until the
                        // directory happened to change again.
                        continue;

                    var declared = declaration.Declared.ToImmutableArray();
                    observed[sourceDirectory.FullName] = new Observed(rootKey, stamp, declared);
                    declaredHere.AddRange(declared);
                }

                if (declaredHere.Count == 0)
                    continue;
                identities++;
                bundles.AddRange(declaredHere);
            }
        }
        catch (Exception ex)
        {
            // An IO fault against the share is an availability incident, and it must be
            // distinguishable from an empty root — "could not look" is never "nothing here".
            logger?.LogWarning(ex,
                "ReleaseAvailability: could not read the published bundle root {Root} while "
                + "establishing which packages must carry a sealed bake", publishedRoot);
            return SealedBundleFloor.Unreadable(
                $"the published bundle root '{publishedRoot}' could not be read ({ex.Message})");
        }

        // 🚨 Reached ONLY on a complete, successful enumeration — every early return above is a
        // refusal, and a share we failed to read must never evict a reading it merely failed to
        // reach. Scoped to THIS root, and pair-exact: a concurrent read that has already stored a
        // fresher entry for the same key is not dropped by an older read's prune. No gate is needed
        // beyond that — an entry is pure cache, so the worst a lost race costs is one extra source
        // read next tick, which is the fail-closed direction.
        var forgotten = 0;
        foreach (var entry in observed)
        {
            if (string.Equals(entry.Value.Root, rootKey, StringComparison.Ordinal)
                && !visited.Contains(entry.Key)
                && observed.TryRemove(entry))
                forgotten++;
        }

        Interlocked.Add(ref sourcesRead, read);
        Interlocked.Add(ref sourcesRecalled, recalled);
        Interlocked.Add(ref sourcesForgotten, forgotten);
        var floor = new SealedBundleFloor(ReleaseArtifacts.Of(bundles).SealedBundles, identities);
        // Information, and deliberately: this line is the operator's evidence that the read #4742
        // froze the fleet on is bounded now. It costs one line per availability check — the poller is
        // event-driven with an hourly safety net, so single figures per hour per install. The bundle
        // count is the FLOOR's, after de-duplication, never the raw declaration count: the same id is
        // declared under every identity that sealed it, so the raw number is roughly ids × identities
        // and would read as a store far larger than the denominator actually is.
        logger?.LogInformation(
            "ReleaseAvailability: denominator over {Root} — {Read} source publications opened on the "
            + "share, {Recalled} answered from this process's earlier reading of them, {Forgotten} "
            + "forgotten as no longer present, {Identities} identities carrying a sealed publication, "
            + "{Bundles} distinct bundle ids",
            publishedRoot, read, recalled, forgotten, identities, floor.Bundles.Count);
        return floor;
    }
}
