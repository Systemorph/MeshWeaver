using System;
using System.Globalization;
using System.IO;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The assembly-cache retention sweep. The cache grows by one whole framework generation per deploy
/// (7817 DLLs / 93 generations / 3.2 GB on memex, 2026-08-12) on a share it does NOT own — the same
/// 16 GiB volume holds the DataProtection key ring — so something has to collect the generations
/// nothing runs any more.
///
/// <para>🚨 The property under test is almost entirely the NEGATIVE one. Deleting an assembly out
/// from under a process that is about to load it is fatal and irreversible; failing to delete one
/// costs a few tens of megabytes. So most of what follows pins what must SURVIVE — a generation some
/// live pod claims, the running framework, the recent ones, the young ones, and everything in the
/// tree the store did not write. <see cref="StaleClaim_DoesNotProtectForever"/> and
/// <see cref="TheMostRecentGenerations_AreKeptWithoutAnyClaim"/> pin the other direction, so the
/// sweep cannot degrade into a very expensive no-op.</para>
/// </summary>
public class AssemblyCacheRetentionTest : IDisposable
{
    // A fixed instant, so every age below is exact rather than racing the wall clock.
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private const string LiveTag = "aaaaaaaa";

    // The layout the AKS portal actually has: /data holds the assembly cache NEXT TO the
    // DataProtection key ring (Memex.Portal.Distributed/Program.cs). The sweep is rooted at the
    // cache, so the keys are outside it by construction — and that is asserted below, not assumed.
    private readonly string dataRoot = Path.Combine(
        Path.GetTempPath(), $"mw-assembly-cache-{Guid.NewGuid():N}");

    private string CacheRoot => Path.Combine(dataRoot, "assembly-cache");
    private string KeyRing => Path.Combine(dataRoot, "dataprotection-keys");

    public AssemblyCacheRetentionTest()
    {
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(KeyRing);
    }

    public void Dispose()
    {
        try { Directory.Delete(dataRoot, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ---- the deliverable ---------------------------------------------------------------------

    /// <summary>
    /// 🚨 THE property. A generation a DIFFERENT pod is still running is never collected, however
    /// old its files are and however far outside the recency window it falls. Nothing else in this
    /// class matters if this one does not hold: the assembly a live pod is about to load would be
    /// gone, and it would surface as a crashed activation on a pod nobody was touching.
    /// </summary>
    [Fact]
    public void GenerationClaimedByAnotherPod_IsNeverCollected()
    {
        var live = WriteAssembly(LiveTag, Now - TimeSpan.FromHours(1));
        var claimed = WriteAssembly("bbbbbbbb", Now - TimeSpan.FromDays(30));
        var abandoned = WriteAssembly("cccccccc", Now - TimeSpan.FromDays(30));

        // The other pod says so, through the same writer its heartbeat uses.
        AssemblyCacheGenerations.AssertClaim(CacheRoot, "bbbbbbbb", "memex-portal-77c58c4469-g2ngb");

        // Recency deliberately protects only the live generation here, so the claim is the ONLY
        // thing standing between bbbbbbbb and deletion.
        var result = AssemblyCacheGenerations.SweepCore(
            CacheRoot, LiveTag,
            AssemblyCacheRetention.ReportOnly with { Delete = true, KeepGenerations = 1 },
            Now);

        File.Exists(claimed).Should().BeTrue(
            "a generation a live pod claims must never be collected — that pod loads these bytes");
        File.Exists(live).Should().BeTrue("the running framework is never collected");
        File.Exists(abandoned).Should().BeFalse("nothing claims cccccccc and nothing else protects it");

        result.Plan.Should().NotBeNull();
        result.Plan!.Collectable.Select(g => g.Tag).Should().Equal("cccccccc");
        result.Plan.Protected.ContainsKey("bbbbbbbb").Should().BeTrue();
        result.Plan.Protected["bbbbbbbb"].Should().Contain("memex-portal-77c58c4469-g2ngb",
            "the log has to name WHO holds a generation, or an operator cannot tell a live claim "
            + "from a forgotten one");
    }

    /// <summary>
    /// The claim is written by a heartbeat and read by a sweep in another process, so the format has
    /// to round-trip through the real writer — a test that hand-rolled the file would pass while the
    /// two sides disagreed.
    /// </summary>
    [Fact]
    public void AClaimWrittenByTheHeartbeat_IsReadBackAsFresh()
    {
        AssemblyCacheGenerations.AssertClaim(CacheRoot, "bbbbbbbb", "pod-b");

        var claims = AssemblyCacheGenerations.ReadClaims(CacheRoot);

        claims.Count.Should().Be(1);
        claims[0].Tag.Should().Be("bbbbbbbb");
        claims[0].Holder.Should().Be("pod-b");
        claims[0].AtUtc.Should().NotBeNull();
        claims[0].AtUtc!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The instant lives in the file's CONTENT, not its metadata — SMB metadata caching can report a
    /// last-write time that is stale while the holder is alive, and a falsely-stale claim is the one
    /// misreading that deletes a live generation.
    /// </summary>
    [Fact]
    public void TheClaimInstantComesFromTheContent_NotTheFileTimestamp()
    {
        AssemblyCacheGenerations.AssertClaim(CacheRoot, "bbbbbbbb", "pod-b");
        var claimFile = Directory
            .EnumerateFiles(Path.Combine(
                CacheRoot, AssemblyCacheGenerations.ClaimsDirectoryName, "bbbbbbbb"))
            .Single();
        // Metadata says a year ago; the content still says now.
        File.SetLastWriteTimeUtc(claimFile, DateTime.UtcNow - TimeSpan.FromDays(365));

        AssemblyCacheGenerations.ReadClaims(CacheRoot)[0].AtUtc!.Value.Should()
            .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
                "a cached-stale timestamp must not be able to condemn a live generation");
    }

    /// <summary>
    /// A claim we cannot make sense of protects its generation. "I could not read the evidence" is
    /// never "there is no evidence" — not when the action licensed by the second is irreversible.
    /// </summary>
    [Fact]
    public void AnUnreadableClaim_Protects()
    {
        var claimed = WriteAssembly("bbbbbbbb", Now - TimeSpan.FromDays(30));
        var claimDirectory = Path.Combine(
            CacheRoot, AssemblyCacheGenerations.ClaimsDirectoryName, "bbbbbbbb");
        Directory.CreateDirectory(claimDirectory);
        File.WriteAllText(Path.Combine(claimDirectory, "pod-b"), "garbage not-a-timestamp");

        AssemblyCacheGenerations.SweepCore(
            CacheRoot, LiveTag,
            AssemblyCacheRetention.ReportOnly with
            {
                Delete = true,
                KeepGenerations = 0,
                MinimumAge = TimeSpan.Zero
            },
            Now);

        File.Exists(claimed).Should().BeTrue(
            "a claim that cannot be parsed is not a claim that is absent");
    }

    // ---- the other keep rules ----------------------------------------------------------------

    /// <summary>
    /// The running framework survives with every other rule switched off. Its own claim should
    /// already cover it; this makes a FAILED claim write unable to let a pod delete the assemblies it
    /// is itself loading.
    /// </summary>
    [Fact]
    public void TheRunningFramework_IsNeverCollected()
    {
        var live = WriteAssembly(LiveTag, Now - TimeSpan.FromDays(365));

        AssemblyCacheGenerations.SweepCore(
            CacheRoot, LiveTag,
            AssemblyCacheRetention.ReportOnly with
            {
                Delete = true,
                KeepGenerations = 0,
                MinimumAge = TimeSpan.Zero
            },
            Now);

        File.Exists(live).Should().BeTrue();
    }

    /// <summary>
    /// Recency keeps the newest generations even with nothing claiming them. That is what covers the
    /// rollout which FIRST introduces claim-writing: the outgoing pod still serves on the previous
    /// image and asserts nothing, and its generation is by construction the second-newest.
    /// </summary>
    [Fact]
    public void TheMostRecentGenerations_AreKeptWithoutAnyClaim()
    {
        string[] tags = ["aaaaaaaa", "bbbbbbbb", "cccccccc", "dddddddd", "eeeeeeee", "ffffffff"];
        var files = tags
            .Select((tag, i) => (tag, path: WriteAssembly(tag, Now - TimeSpan.FromDays(30 + i))))
            .ToList();

        var result = AssemblyCacheGenerations.SweepCore(
            CacheRoot, LiveTag,
            AssemblyCacheRetention.ReportOnly with { Delete = true, KeepGenerations = 3 },
            Now);

        // Newest first: aaaaaaaa (live), bbbbbbbb, cccccccc are the three kept by recency.
        result.Plan!.Collectable.Select(g => g.Tag).Should()
            .Equal("dddddddd", "eeeeeeee", "ffffffff");
        files.Where(f => f.tag is "dddddddd" or "eeeeeeee" or "ffffffff")
            .Should().OnlyContain(f => !File.Exists(f.path));
        files.Where(f => f.tag is "aaaaaaaa" or "bbbbbbbb" or "cccccccc")
            .Should().OnlyContain(f => File.Exists(f.path));
    }

    /// <summary>
    /// The age floor is the last backstop: whatever the claims and the recency window say, a
    /// generation something wrote recently is not collected.
    /// </summary>
    [Fact]
    public void AYoungGeneration_IsKept()
    {
        var young = WriteAssembly("bbbbbbbb", Now - TimeSpan.FromDays(1));

        AssemblyCacheGenerations.SweepCore(
            CacheRoot, LiveTag,
            AssemblyCacheRetention.ReportOnly with
            {
                Delete = true,
                KeepGenerations = 0,
                MinimumAge = TimeSpan.FromDays(7)
            },
            Now);

        File.Exists(young).Should().BeTrue();
    }

    /// <summary>A claim older than its TTL stops protecting, and the generation goes.</summary>
    [Fact]
    public void StaleClaim_DoesNotProtectForever()
    {
        var abandoned = WriteAssembly("bbbbbbbb", Now - TimeSpan.FromDays(30));
        var claimDirectory = Path.Combine(
            CacheRoot, AssemblyCacheGenerations.ClaimsDirectoryName, "bbbbbbbb");
        Directory.CreateDirectory(claimDirectory);
        File.WriteAllText(
            Path.Combine(claimDirectory, "pod-that-died"),
            "pod-that-died "
            + (Now - TimeSpan.FromDays(2)).ToString("O", CultureInfo.InvariantCulture));

        var result = AssemblyCacheGenerations.SweepCore(
            CacheRoot, LiveTag,
            AssemblyCacheRetention.ReportOnly with
            {
                Delete = true,
                KeepGenerations = 0,
                ClaimTtl = TimeSpan.FromHours(24)
            },
            Now);

        File.Exists(abandoned).Should().BeFalse();
        result.DeletedFiles.Should().Be(1);
    }

    // ---- the blast radius --------------------------------------------------------------------

    /// <summary>
    /// 🚨 The sweep deletes ONLY files whose names this store wrote, inside the per-NodeType
    /// directories under the cache root. The DataProtection key ring shares the volume — that
    /// coupling is what makes this issue urgent rather than untidy — so the guard is tested, not
    /// reasoned about.
    /// </summary>
    [Fact]
    public void NothingButThisStoresAssemblies_IsEverDeleted()
    {
        // Something genuinely collectable, so the assertions below prove the sweep RAN.
        var abandoned = WriteAssembly("cccccccc", Now - TimeSpan.FromDays(30));

        // 1. The key ring, a sibling of the cache root on the same share.
        var key = Path.Combine(KeyRing, "key-1a2b3c.xml");
        File.WriteAllText(key, "<key/>");
        // 2. The bake lease, at the cache root.
        var lease = Path.Combine(CacheRoot, ".bake-lease-aaaaaaa");
        File.WriteAllText(lease, "pod-a");
        // 3. A file inside a per-NodeType directory that this store did not write.
        var foreign = Path.Combine(CacheRoot, "Acme_Pricing", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "x");
        // 4. A pre-tag legacy assembly (v{version}-{hash}) — unattributable to any generation.
        var legacy = Path.Combine(CacheRoot, "Acme_Pricing", "v7-9f4455cd1122.dll");
        File.WriteAllText(legacy, "x");
        File.SetLastWriteTimeUtc(legacy, (Now - TimeSpan.FromDays(365)).UtcDateTime);

        var result = AssemblyCacheGenerations.SweepCore(
            CacheRoot, LiveTag,
            AssemblyCacheRetention.ReportOnly with
            {
                Delete = true,
                KeepGenerations = 0,
                MinimumAge = TimeSpan.Zero
            },
            Now);

        File.Exists(abandoned).Should().BeFalse("the sweep must actually have run");
        File.Exists(key).Should().BeTrue(
            "the DataProtection key ring is not even inside the cache root");
        File.Exists(lease).Should().BeTrue(
            "the bake lease lives at the root, which is never enumerated");
        File.Exists(foreign).Should().BeTrue(
            "a name this store did not write is never attributed, so never deleted");
        File.Exists(legacy).Should().BeTrue("an untagged legacy assembly belongs to no generation");
        result.Plan!.UnrecognisedFiles.Should().Be(2, "notes.txt and the legacy dll");
    }

    /// <summary>
    /// The shipped default measures and says what it WOULD do. A retention policy whose first run
    /// deletes on defaults nobody has checked against their own roll cadence is exactly the mistake
    /// here that cannot be undone.
    /// </summary>
    [Fact]
    public void ReportOnly_IsTheDefault_AndDeletesNothing()
    {
        AssemblyCacheRetention.ReportOnly.Delete.Should().BeFalse();

        var abandoned = WriteAssembly("cccccccc", Now - TimeSpan.FromDays(30));

        var result = AssemblyCacheGenerations.SweepCore(
            CacheRoot, LiveTag,
            AssemblyCacheRetention.ReportOnly with { KeepGenerations = 0 },
            Now);

        File.Exists(abandoned).Should().BeTrue();
        result.Deleted.Should().BeFalse();
        result.DeletedFiles.Should().Be(0);
        // Report-only still has to say precisely what arming it WOULD remove.
        result.Plan!.Collectable.Select(g => g.Tag).Should().Equal("cccccccc");
    }

    /// <summary>The TTL has to be many multiples of the beat, or a LATE claim reads as a GONE one.</summary>
    [Fact]
    public void ClaimTtl_IsWellBeyondTheRefreshInterval() =>
        AssemblyCacheRetention.ReportOnly.ClaimTtl.Should()
            .BeGreaterThan(AssemblyCacheRetention.ReportOnly.ClaimRefreshInterval * 8,
                "a handful of missed heartbeats under load must never license a deletion");

    // ---- filename attribution ----------------------------------------------------------------

    [Theory]
    [InlineData("v7-22825f59-9f4455cd1122.dll", "22825f59")]
    [InlineData("v7-22825F59-9F4455CD1122.pdb", "22825f59")]
    [InlineData("v1234567890-2f9763d7-abcdef012345.dll", "2f9763d7")]
    public void ATaggedAssembly_IsAttributedToItsGeneration(string fileName, string expected) =>
        AssemblyCacheGenerations.TagOf(fileName).Should().Be(expected);

    [Theory]
    [InlineData("v7-9f4455cd1122.dll")]           // pre-tag legacy: two segments
    [InlineData("v7-22825f59-abc-def.dll")]       // four segments
    [InlineData("keys.xml")]                      // not ours at all
    [InlineData(".bake-lease-22825f59")]          // the lease
    [InlineData("vX-22825f59-9f4455cd1122.dll")]  // no version
    [InlineData("v7-zzzzzzzz-9f4455cd1122.dll")]  // tag is not hex
    // 🚨 The WIDTHS are the deletion boundary, not decoration: the store always writes an 8-char
    // tag and a 12-char hash, so a foreign name that merely happens to be hex must not be
    // attributed — attribution is what makes a file deletable.
    [InlineData("v1-ab-cd.dll")]                  // hex, three segments, but nothing this store wrote
    [InlineData("v7-22825f5-9f4455cd1122.dll")]   // tag one char short
    [InlineData("v7-22825f590-9f4455cd1122.dll")] // tag one char long
    [InlineData("v7-22825f59-9f4455cd112.dll")]   // hash one char short
    [InlineData("v7-22825f59-9f4455cd11223.dll")] // hash one char long
    public void AnythingElse_IsAttributedToNothing(string fileName) =>
        AssemblyCacheGenerations.TagOf(fileName).Should().BeNull(
            "an unattributable name is one the sweep can never delete");

    // ---- helpers ------------------------------------------------------------------------------

    private string WriteAssembly(string tag, DateTimeOffset writtenAt, string typeDirectory = "Acme_Pricing")
    {
        var directory = Path.Combine(CacheRoot, typeDirectory);
        Directory.CreateDirectory(directory);
        var contentHash = Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(directory, $"v7-{tag}-{contentHash}.dll");
        File.WriteAllBytes(path, new byte[64]);
        File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
        return path;
    }
}
