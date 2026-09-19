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
/// 🚨 <b>The deployment gate's denominator may not be re-opened from the share on every tick
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
/// <see cref="ARememberedSourceIsNotOpenedAgain_MeasuredAgainstTheReadingThatDoes"/> runs the OLD
/// reading and the new one over the same sabotaged fixture in one test, so it cannot pass because
/// the fixture happened to be empty: the open-every-time reading collapses to nothing while the
/// remembered one still answers.</para>
///
/// <para>🚨 <b>And fail-closed is the other half of the subject</b>, because a cache that served a
/// stale "available" would be far worse than the freeze it removes. Each clause has its own test: a
/// refusal is never remembered; a source ADDED under an identity already read is picked up; a source
/// REPUBLISHED IN PLACE that adds a package is picked up (the case an identity-level stamp cannot
/// see, and the reason the signal lives on the source directory); a pruned identity is forgotten
/// while a FAILED read evicts nothing; and the one direction the cache may err in is a LARGER
/// denominator, which can only hold.</para>
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

    /// <summary>The source every generated identity seals it under.</summary>
    private const string PlatformSource = "meshweaver-content";

    // ── the fix: O(new or changed sources), not O(every publication ever) ───────────────────────

    [Fact]
    public void ASourceAlreadyReadIsNotOpenedAgain_AndANewOneAlwaysIs()
    {
        var root = RootWithIdentities(12);
        var cache = new SealedBundleFloorCache();

        var first = cache.Read(root);

        // The whole store on a cold cache — this is the reading that used to happen every tick.
        Assert.Equal(12, first.Identities);
        Assert.Equal(12L, cache.SourcesRead);
        Assert.Equal(0L, cache.SourcesRecalled);

        var second = cache.Read(root);

        // 🚨 THE CLAIM: not one publication re-opened, and the same answer. The old reading has no
        // such property by construction — it opens every pointer and every sentinel, every time.
        Assert.Equal(12L, cache.SourcesRead);
        Assert.Equal(12L, cache.SourcesRecalled);
        Assert.Equal(first.Identities, second.Identities);
        Assert.True(first.Bundles.SetEquals(second.Bundles));

        // A NEW identity — what CD produces about sixty times a day — costs exactly itself.
        SealIdentity(root, "s4742new0000000000000000000000000", "plugins", ["Store"]);
        var third = cache.Read(root);

        Assert.Equal(13L, cache.SourcesRead);
        Assert.Equal(13, third.Identities);
        Assert.Contains("Store", third.Bundles);
    }

    [Fact]
    public void ARememberedSourceIsNotOpenedAgain_MeasuredAgainstTheReadingThatDoes()
    {
        var root = RootWithIdentities(6);
        var cache = new SealedBundleFloorCache();

        var warm = cache.Read(root);
        Assert.Equal(6, warm.Identities);
        Assert.Contains(Platform, warm.Bundles);

        // Empty every seal IN PLACE. Rewriting an existing file changes no directory ENTRY, so no
        // source directory's write stamp moves — which is precisely the one class of change the
        // remembered reading is documented not to follow, and the one that can only ever SHRINK a
        // declaration (the direction that holds, never exempts).
        foreach (var sentinel in Directory.EnumerateFiles(
                     root,
                     ShippedPrebuiltBundles.CompletionSentinelFileName,
                     SearchOption.AllDirectories))
            File.WriteAllText(sentinel, string.Empty);

        // ── the NEGATIVE CONTROL, executed ──────────────────────────────────────────────────────
        // The reading that opens every sentinel on every tick now sees nothing at all. If this line
        // ever stops failing that way, the assertions below are measuring nothing.
        var opened = PublishedBundleCatalogue.EverSealedBundles(root);
        Assert.Null(opened.Refusal);
        Assert.Equal(0, opened.Identities);
        Assert.Empty(opened.Bundles);

        // ── the fix ─────────────────────────────────────────────────────────────────────────────
        var readBefore = cache.SourcesRead;
        var again = cache.Read(root);

        Assert.Equal(readBefore, cache.SourcesRead);
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
        // Core's CD seals <identity>/meshweaver-content, and a satellite repo bakes
        // <identity>/education against that SAME platform identity later. Missing the second one
        // would shrink the denominator, which is the one direction that EXEMPTS a package (#3461).
        var root = RootWithIdentities(3);
        var cache = new SealedBundleFloorCache();

        Assert.DoesNotContain("ThinkInStreams", cache.Read(root).Bundles);

        Seal(FirstIdentityOf(root), "education", ["ThinkInStreams"]);

        var after = cache.Read(root);
        Assert.Contains("ThinkInStreams", after.Bundles);
        Assert.Equal(3, after.Identities);
    }

    [Fact]
    public void ARepublishedSourceThatAddsAPackageIsPickedUp()
    {
        // 🚨 THE CASE AN IDENTITY-LEVEL STAMP CANNOT SEE, and the reason the signal lives on the
        // SOURCE directory. A satellite re-baking into a platform identity that has not moved is a
        // routine daily event: the same source directory is republished with a longer package list,
        // and the IDENTITY directory's own stamp does not move at all. Keying on it would freeze the
        // new package out of the denominator for as long as that identity stayed newest, which
        // exempts it from the gate. This test fails against that design and passes against this one.
        var root = RootWithIdentities(3);
        var cache = new SealedBundleFloorCache();

        Assert.DoesNotContain("Northwind", cache.Read(root).Bundles);

        var identity = FirstIdentityOf(root);
        var identityStampBefore = Directory.GetLastWriteTimeUtc(identity);
        Seal(identity, PlatformSource, [Platform, "Northwind"]);

        // The identity directory is untouched by a republication of a source it already held — said
        // out loud, because it is the whole premise of this test rather than a detail of it.
        Assert.Equal(identityStampBefore, Directory.GetLastWriteTimeUtc(identity));

        var after = cache.Read(root);
        Assert.Contains("Northwind", after.Bundles);
        Assert.Contains(Platform, after.Bundles);
        Assert.Equal(3, after.Identities);
    }

    [Fact]
    public void APrunedIdentityIsForgotten_AndAFailedReadEvictsNothing()
    {
        var root = RootWithIdentities(4);
        var cache = new SealedBundleFloorCache();

        Assert.Equal(4, cache.Read(root).Identities);
        Assert.Equal(4, cache.Remembered);
        Assert.Equal(0L, cache.SourcesForgotten);

        // Retention removes an identity. Its publication must leave memory too — otherwise every
        // publication this process has ever seen stays rooted for the life of the mesh.
        Directory.Delete(FirstIdentityOf(root), recursive: true);

        Assert.Equal(3, cache.Read(root).Identities);
        Assert.Equal(3, cache.Remembered);
        Assert.Equal(1L, cache.SourcesForgotten);

        // 🚨 And the other half: a read that FAILED must evict nothing. A share we could not finish
        // reading is not evidence that anything left it — treating it as such would be the
        // fail-open direction wearing tidying's clothes.
        var broken = Path.Combine(root, "s4742pointer00000000000000000000", "plugins");
        Directory.CreateDirectory(broken);
        File.WriteAllText(
            Path.Combine(broken, ShippedPrebuiltBundles.PublicationPointerFileName),
            "nested/elsewhere\n");

        Assert.NotNull(cache.Read(root).Refusal);
        Assert.Equal(3, cache.Remembered);
        Assert.Equal(1L, cache.SourcesForgotten);
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

    [Fact]
    public void TwoRootsInOneProcessDoNotEvictEachOther()
    {
        // Entries carry the root they were read under, so a complete enumeration of one root can
        // never prune another's — the cache is an instance, but an instance can still serve two
        // mounts (a test host, a registry answering for more than one store).
        var first = RootWithIdentities(3);
        var second = RootWithIdentities(2);
        var cache = new SealedBundleFloorCache();

        cache.Read(first);
        cache.Read(second);
        Assert.Equal(5, cache.Remembered);

        cache.Read(first);
        Assert.Equal(5, cache.Remembered);
        Assert.Equal(0L, cache.SourcesForgotten);
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

    /// <summary>The lowest-ordinal framework-identity directory — the release markers are not one.</summary>
    private static string FirstIdentityOf(string root) =>
        Directory.EnumerateDirectories(root)
            .Where(d => !string.Equals(
                Path.GetFileName(d),
                PublishedBundleCatalogue.ReleaseMarkerDirectoryName,
                StringComparison.Ordinal))
            .OrderBy(d => d, StringComparer.Ordinal)
            .First();

    /// <summary>A published root carrying <paramref name="count"/> distinct framework identities,
    /// each with one sealed source — the shape the share accumulates, one identity per set.</summary>
    private string RootWithIdentities(int count)
    {
        var root = Track(Path.Combine(Path.GetTempPath(), "mw-4742-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName));
        for (var i = 0; i < count; i++)
            SealIdentity(root, $"s4742{i:D27}", PlatformSource, [Platform]);
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
