using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>The deployment gate's denominator, read in O(identities this process has not seen) instead
/// of O(every identity ever sealed) (#4742).</b>
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
/// and named the remedy: the denominator must not be re-enumerated from the share on every tick.</para>
///
/// <para><b>The shape.</b> An identity directory is written by the CD wave that produced that
/// identity and is then finished with; what it declares does not change. So this remembers, per
/// identity directory, what it declared and the directory's <see cref="FileSystemInfo.LastWriteTimeUtc"/>
/// at the moment it was read. Each read still enumerates the root — one listing, so a NEW identity is
/// always seen and a PRUNED one always drops out — but descends only into the identities it has never
/// read, whose stamp has moved, or whose reading was not finished. Two signals, and they close
/// different windows:</para>
/// <list type="number">
///   <item><b>The identity directory's write stamp</b> catches a source being ADDED under an
///     identity already read — a satellite baking against a platform identity core sealed earlier,
///     which is the ordinary case, not an exotic one. Creating that subdirectory advances the
///     identity directory's own stamp, so this is a signal and not a hope about publication order.
///     The stamp rides the root listing the read already performs.</item>
///   <item><b><see cref="IdentityDeclaration.EverySourceSealed"/></b> catches the window the stamp
///     cannot see: the publisher creates the source directory (stamp moves) and writes its
///     completion sentinel LAST, INSIDE it (stamp does not move). A reading taken between the two
///     declares nothing for that source, and remembering it would freeze a package OUT of the
///     denominator. So an identity with an unsealed source, or with none at all, is re-read every
///     tick until it settles.</item>
/// </list>
///
/// <para>🚨 <b>How fail-closed is preserved</b>, clause by clause, because a cache that turns a hold
/// into a roll would be far worse than the freeze it removes:</para>
/// <list type="bullet">
///   <item><b>A refusal is never remembered.</b> An unreadable root, an unfollowable publication
///     pointer, an enumeration fault — each returns <see cref="SealedBundleFloor.Unreadable"/> and
///     writes nothing into the cache, so one transient share fault cannot latch into a permanent
///     verdict and the very next tick re-reads from scratch. (This is the
///     <c>PromiseCache</c> contract's reason, applied to a read that is repeated rather than
///     one-shot: a cached terminal is a permanent fault.)</item>
///   <item><b>The cached answer can only be LARGER, never smaller.</b> A remembered contribution
///     whose seal has since been removed keeps its packages in the denominator. That is the direction
///     #3441 chose deliberately — a bake that regresses to nothing must HOLD rather than exempt — so
///     the cache agrees with the monotone contract the uncached walk only approximates.</item>
///   <item><b>Nothing about the CANDIDATE is cached.</b> The target release's own publication
///     (<c>ArtifactsForIdentity</c>, the marker, the surface, the module set) is read fresh on every
///     tick for every candidate. Only the denominator's history — the part that is monotone by
///     design — is remembered.</item>
/// </list>
///
/// <para>🚨 <b>The one reading this does not refresh, stated rather than argued away.</b> A source
/// REPUBLISHED in place under an identity already settled — its publication pointer moved to a new
/// generation — changes nothing either signal can see, so its older declaration stands until the
/// process restarts. That can only matter for a package whose FIRST ever seal landed in such a
/// republication and which no later identity seals, since every subsequent CD wave writes a new
/// identity directory that is always read fresh. Closing it would cost one stat per SOURCE per tick,
/// which is the growth this type exists to remove, against a denominator whose whole contract
/// (#3441) is to be monotone and inclusive.</para>
///
/// <para><b>Never static.</b> Held as an instance field on a mesh-scoped singleton (registered by
/// <c>AddSelfUpdate</c>), so its lifetime is the mesh's and it neither bleeds across tests nor
/// survives mesh disposal — <see href="../NoStaticState">No Static State</see>.</para>
/// </summary>
public sealed class SealedBundleFloorCache
{
    /// <summary>One identity directory as this process last read it.</summary>
    /// <param name="Stamp">The directory's write stamp when it was read — the invalidation signal.</param>
    /// <param name="Declared">What its sealed sources declared.</param>
    private sealed record Observed(DateTime Stamp, ImmutableArray<string> Declared);

    /// <summary>🚨 An INSTANCE field on a mesh-scoped singleton, never static — and a
    /// <c>ConcurrentDictionary</c> because two availability reads can overlap (the gate and the roll
    /// selector each take one). Keyed by the identity directory's full path, so two roots in one
    /// process cannot collide.</summary>
    private readonly ConcurrentDictionary<string, Observed> observed = new(StringComparer.Ordinal);

    private long identitiesRead;
    private long identitiesRecalled;

    /// <summary>How many identity directories this cache has actually descended into, cumulatively.
    /// The claim of #4742 IS this number: it must grow with NEW identities and stand still over
    /// unchanged ones. Read by <c>SealedBundleFloorCacheTest</c> and reported in the read's own log
    /// line, which is the operator's evidence that the denominator is no longer re-walked.</summary>
    public long IdentitiesRead => Interlocked.Read(ref identitiesRead);

    /// <summary>How many identity directories this cache has answered from memory, cumulatively —
    /// the round trips against the share that the fix does not make.</summary>
    public long IdentitiesRecalled => Interlocked.Read(ref identitiesRecalled);

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
        var identities = 0;
        var read = 0;
        var recalled = 0;
        try
        {
            foreach (var identityDirectory in new DirectoryInfo(publishedRoot!)
                         .EnumerateDirectories()
                         .OrderBy(d => d.FullName, StringComparer.Ordinal))
            {
                if (PublishedBundleCatalogue.IsReleaseMarkerDirectory(identityDirectory.FullName))
                    continue;

                // The stamp rides the enumeration the loop already performed — one metadata read,
                // against the listing plus two file opens per source that a descent costs.
                var stamp = identityDirectory.LastWriteTimeUtc;
                if (observed.TryGetValue(identityDirectory.FullName, out var known)
                    && known.Stamp == stamp)
                {
                    recalled++;
                    if (!known.Declared.IsEmpty)
                    {
                        identities++;
                        bundles.AddRange(known.Declared);
                    }
                    continue;
                }

                read++;
                var declaration = PublishedBundleCatalogue.DeclaredBundlesUnderIdentity(
                    identityDirectory.FullName, logger);
                // 🚨 NOT remembered. See the class remarks: a cached refusal is a permanent freeze.
                if (declaration.Refusal is not null)
                    return SealedBundleFloor.Unreadable(declaration.Refusal);

                var declared = declaration.Declared.ToImmutableArray();
                // 🚨 Only a FINISHED reading is remembered. A publisher creates a source directory
                // and writes its sentinel LAST, and that write lands INSIDE the source directory —
                // it does not move the identity directory's own stamp — so a reading taken in that
                // window would be frozen declaring nothing, which SHRINKS the denominator and
                // exempts a package (#3461). An identity with an unsealed source (or none at all) is
                // therefore re-read every tick until it settles: the old cost, for the handful of
                // identities that are actually mid-publication.
                if (declaration.EverySourceSealed)
                    observed[identityDirectory.FullName] = new Observed(stamp, declared);
                if (!declared.IsEmpty)
                {
                    identities++;
                    bundles.AddRange(declared);
                }
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

        Interlocked.Add(ref identitiesRead, read);
        Interlocked.Add(ref identitiesRecalled, recalled);
        // Information, and deliberately: this line is the operator's evidence that the read #4742
        // froze the fleet on is bounded now. It costs one line per availability check — the poller is
        // event-driven with an hourly safety net, so single figures per hour per install.
        logger?.LogInformation(
            "ReleaseAvailability: denominator over {Root} — {Read} identity directories read from "
            + "the share, {Recalled} answered from this process's reading of them, {Identities} "
            + "carrying a sealed publication, {Bundles} bundle ids",
            publishedRoot, read, recalled, identities, bundles.Count);
        return new SealedBundleFloor(ReleaseArtifacts.Of(bundles).SealedBundles, identities);
    }
}
