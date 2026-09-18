#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The deployment gate's denominator may not be re-enumerated from the share on every tick
/// (#4742).</b>
///
/// <para><c>PublishedBundleCatalogue.EverSealedBundles</c> walks EVERY framework-identity directory
/// the published root has ever held, listing each one's sources and opening each source's pointer
/// and completion sentinel — and it is the first thing the availability gate and the roll selector
/// do, inside a 60 s verdict budget, against an Azure Files share. The store is append-only and CD
/// cuts roughly sixty sets a day, each moving the framework identity, so the walk's cost only grows.
/// Measured on memex.meshweaver.cloud 2026-09-18 at 18:45:36Z and 19:16:36Z, on two freshly sealed
/// candidates: <c>the availability check did not answer within 60s</c> both times. That is a HOLD, so
/// the public instance stayed on a six-day-old build while CD kept sealing, with eight Store
/// NodeTypes parked at <c>compilationStatus: Error</c> as a result.</para>
///
/// <para><b>The control is EXECUTED, not asserted.</b>
/// <see cref="ARememberedIdentityIsNotWalkedAgain_MeasuredAgainstTheReadingThatDoes"/> runs the OLD
/// reading and the new one over the same sabotaged fixture in one test, so it cannot pass because
/// the fixture happened to be empty: the walk-every-time reading collapses to nothing while the
/// cached one still answers.</para>
///
/// <para>🚨 <b>And fail-closed is the other half of the subject.</b> A cache that served a stale
/// "available" would be far worse than the freeze it removes, so the tests below pin each clause:
/// a refusal is never remembered, a source added under an already-read identity IS picked up, and
/// the one direction the cache may err in is a LARGER denominator, which can only hold.</para>
/// </summary>
public class SealedBundleFloorCacheTest : IDisposable
{
    /// <summary>Every published root this test built, removed on teardown — a leaked temp tree
    /// bloats the CI agent and is one more way for two runs to interfere.</summary>
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>The bundle every generated identity seals, so a floor can be recognised.</summary>
    private const string Platform = "Documentation";

    // ── the fix: O(identities not read before), not O(every identity ever) ──────────────────────

    [Fact]
    public void AnIdentityAlreadyReadIsNotWalkedAgain_AndANewOneAlwaysIs()
    {
        var root = RootWithIdentities(12);
        var cache = new SealedBundleFloorCache();

        var first = cache.Read(root);

        // The whole store on a cold cache — this is the reading that used to happen every tick.
        Assert.Equal(12, first.Identities);
        Assert.Equal(12L, cache.IdentitiesRead);
        Assert.Equal(0L, cache.IdentitiesRecalled);

        var second = cache.Read(root);

        // 🚨 THE CLAIM: not one identity directory descended into, and the same answer. The old
        // reading has no such property by construction — it opens every sentinel, every time.
        Assert.Equal(12L, cache.IdentitiesRead);
        Assert.Equal(12L, cache.IdentitiesRecalled);
        Assert.Equal(first.Identities, second.Identities);
        Assert.True(first.Bundles.SetEquals(second.Bundles));

        // A NEW identity — what CD produces about sixty times a day — costs exactly itself.
        SealIdentity(root, "s4742new0000000000000000000000000", "plugins", ["Store"]);
        var third = cache.Read(root);

        Assert.Equal(13L, cache.IdentitiesRead);
        Assert.Equal(13, third.Identities);
        Assert.Contains("Store", third.Bundles);
    }

    [Fact]
    public void ARememberedIdentityIsNotWalkedAgain_MeasuredAgainstTheReadingThatDoes()
    {
        var root = RootWithIdentities(6);
        var cache = new SealedBundleFloorCache();

        var warm = cache.Read(root);
        Assert.Equal(6, warm.Identities);
        Assert.Contains(Platform, warm.Bundles);

        // Remove every seal. The sentinel lives INSIDE <identity>/<source>, so deleting it moves
        // that source directory's write stamp and never the identity directory's — which is the
        // one signal the cache validates against.
        foreach (var sentinel in Directory.EnumerateFiles(
                     root,
                     ShippedPrebuiltBundles.CompletionSentinelFileName,
                     SearchOption.AllDirectories))
            File.Delete(sentinel);

        // ── the NEGATIVE CONTROL, executed ──────────────────────────────────────────────────────
        // The reading that walks the share on every tick now sees nothing at all. If this line ever
        // stops failing that way, the test below is measuring nothing.
        var walked = PublishedBundleCatalogue.EverSealedBundles(root);
        Assert.Null(walked.Refusal);
        Assert.Equal(0, walked.Identities);
        Assert.Empty(walked.Bundles);

        // ── the fix ─────────────────────────────────────────────────────────────────────────────
        var readBefore = cache.IdentitiesRead;
        var again = cache.Read(root);

        Assert.Equal(readBefore, cache.IdentitiesRead);
        Assert.Equal(warm.Identities, again.Identities);
        Assert.True(warm.Bundles.SetEquals(again.Bundles));
    }

    [Fact]
    public void TheCachedFloorIsTheSameFloorTheFullWalkReads()
    {
        var root = RootWithIdentities(4);
        SealIdentity(root, "s4742extra0000000000000000000000", "education", ["ThinkInStreams"]);

        var walked = PublishedBundleCatalogue.EverSealedBundles(root);
        var cached = new SealedBundleFloorCache().Read(root);

        Assert.Equal(walked.Identities, cached.Identities);
        Assert.True(walked.Bundles.SetEquals(cached.Bundles));
        Assert.Equal(walked.Refusal, cached.Refusal);
        Assert.Equal(walked.ServesBakes, cached.ServesBakes);
    }

    // ── fail-closed ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASourceSealedUnderAnIdentityAlreadyReadIsPickedUp()
    {
        // The real shape: core's CD seals <identity>/plugins, and a satellite repo bakes
        // <identity>/education against that SAME platform identity later. Missing the second one
        // would shrink the denominator, which is the one direction that EXEMPTS a package (#3461).
        var root = RootWithIdentities(3);
        var cache = new SealedBundleFloorCache();

        Assert.DoesNotContain("ThinkInStreams", cache.Read(root).Bundles);

        var identity = Directory.EnumerateDirectories(root)
            .Where(d => !PathIsReleaseMarkers(d))
            .OrderBy(d => d, StringComparer.Ordinal)
            .First();
        var before = Directory.GetLastWriteTimeUtc(identity);
        Seal(identity, "education", ["ThinkInStreams"]);

        // 🚨 The design's invalidation signal, asserted rather than assumed: adding a source
        // directory advances the identity directory's own write stamp. On a file system that did
        // not, the cache would silently under-report — this is the line that would say so.
        Assert.NotEqual(before, Directory.GetLastWriteTimeUtc(identity));

        var after = cache.Read(root);
        Assert.Contains("ThinkInStreams", after.Bundles);
        Assert.Equal(3, after.Identities);
    }

    [Fact]
    public void AnUnfollowablePointerRefuses_AndTheRefusalIsNeverRemembered()
    {
        var root = RootWithIdentities(2);
        var cache = new SealedBundleFloorCache();
        Assert.Null(cache.Read(root).Refusal);

        // A publication pointer this reader will not follow, with no sealed publication behind it:
        // what that source declares could not be read, so the whole floor refuses.
        var identity = Path.Combine(root, "s4742pointer00000000000000000000");
        var source = Path.Combine(identity, "plugins");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.PublicationPointerFileName),
            "nested/elsewhere\n");

        var refused = cache.Read(root);
        Assert.NotNull(refused.Refusal);
        Assert.False(refused.ServesBakes);

        // 🚨 The refusal must not latch. A cached terminal turns one transient share fault into a
        // permanent freeze — the exact failure PromiseCache exists to prevent (#1369), here on a
        // read that repeats rather than one that happens once.
        File.Delete(Path.Combine(source, ShippedPrebuiltBundles.PublicationPointerFileName));
        Seal(identity, "plugins", ["Store"]);

        var recovered = cache.Read(root);
        Assert.Null(recovered.Refusal);
        Assert.Contains("Store", recovered.Bundles);
        Assert.Equal(3, recovered.Identities);
    }

    [Fact]
    public void AnAbsentRootRefuses_AndIsNotAnEmptyFloor()
    {
        var root = Track(Path.Combine(Path.GetTempPath(), "mw-4742-" + Guid.NewGuid().ToString("N")));
        var cache = new SealedBundleFloorCache();

        // A deployment that declares it consumes CI bakes and whose storage is not there is a
        // mis-mount, never evidence that nothing is published. Cannot determine ≠ clear to proceed.
        var missing = cache.Read(root);
        Assert.NotNull(missing.Refusal);
        Assert.False(missing.ServesBakes);
        Assert.Empty(missing.Bundles);

        SealIdentity(root, "s4742mounted00000000000000000000", "plugins", ["Store"]);

        var mounted = cache.Read(root);
        Assert.Null(mounted.Refusal);
        Assert.True(mounted.ServesBakes);
        Assert.Contains("Store", mounted.Bundles);
    }

    [Fact]
    public void ARootThatIsReadableAndHoldsNothingIsNotARefusal()
    {
        // The one stated applicability exemption, which the cache must keep telling apart from
        // "I could not look": ZERO identities, no refusal.
        var root = Track(Path.Combine(Path.GetTempPath(), "mw-4742-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName));

        var floor = new SealedBundleFloorCache().Read(root);

        Assert.Null(floor.Refusal);
        Assert.Equal(0, floor.Identities);
        Assert.False(floor.ServesBakes);
    }

    // ── the secondary half: the budget is a configuration key, with the same fail-closed default ─

    [Fact]
    public void TheAnswerBudgetIsConfigurable_AndItsDefaultIsTheOneThatShipped()
    {
        Assert.Equal(
            SelfUpdateOptions.DefaultAvailabilityAnswerBudget,
            new SelfUpdateOptions().AvailabilityAnswerBudget);

        const string Declared = "00:03:00";
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{SelfUpdateOptions.SectionName}:AvailabilityAnswerBudget"] = Declared,
            })
            .Build()
            .GetSection(SelfUpdateOptions.SectionName)
            .Get<SelfUpdateOptions>();

        Assert.NotNull(options);
        Assert.Equal(TimeSpan.Parse(Declared), options!.AvailabilityAnswerBudget);
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    private string Track(string root)
    {
        roots.Add(root);
        return root;
    }

    private static bool PathIsReleaseMarkers(string directory) =>
        string.Equals(
            Path.GetFileName(directory),
            PublishedBundleCatalogue.ReleaseMarkerDirectoryName,
            StringComparison.Ordinal);

    /// <summary>A published root carrying <paramref name="count"/> distinct framework identities,
    /// each with one sealed source — the shape the share accumulates, one identity per set.</summary>
    private string RootWithIdentities(int count)
    {
        var root = Track(Path.Combine(Path.GetTempPath(), "mw-4742-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName));
        for (var i = 0; i < count; i++)
            SealIdentity(root, $"s4742{i:D27}", "meshweaver-content", [Platform]);
        return root;
    }

    private static void SealIdentity(
        string root, string identity, string source, IEnumerable<string> bundles) =>
        Seal(Path.Combine(root, identity), source, bundles);

    /// <summary>A SEALED source publication: the bundle files, then the sentinel that lists them,
    /// written strictly last — the publisher's own order, which is what makes the sentinel the
    /// atomic "this publication is whole" fact.</summary>
    private static void Seal(string identityDirectory, string source, IEnumerable<string> bundles)
    {
        var directory = Path.Combine(identityDirectory, source);
        Directory.CreateDirectory(directory);
        var names = new List<string>();
        foreach (var bundle in bundles)
        {
            File.WriteAllText(Path.Combine(directory, bundle + ".zip"), bundle);
            names.Add(bundle + ".zip");
        }
        File.WriteAllText(
            Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName),
            string.Join('\n', names.Order(StringComparer.Ordinal)) + "\n");
    }
}
