#pragma warning disable CS1591

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MeshWeaver.Hosting;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A publication lives in a GENERATION directory, and every reader resolves the pointer
/// before it composes a single path (#3461).</b>
///
/// <para>The flat layout replaces a publication IN PLACE: the publisher deletes <c>_complete</c>,
/// uploads over the live files, and re-seals. Two publishers interleaved on one prefix therefore
/// both unseal, both upload, and the last to seal covers a directory holding SOME OF EACH one's
/// bytes — one seal, one generation, self-consistent to every consumer and wrong. #3496's
/// byte-level postcondition turns that from a silent mix into a loud refusal, but it is a
/// postcondition, not mutual exclusion.</para>
///
/// <para>The layout that removes it writes each publication into its OWN directory and moves a
/// one-line pointer, <c>_current</c>, last. Two publishers never write the same bytes, so a mix
/// becomes <b>unrepresentable</b> rather than merely detectable, and a reader that resolves the
/// pointer a moment early gets the previous generation — intact, sealed, still on the shelf.</para>
///
/// <para>🚨 <b>This is the READER half, and it lands FIRST</b>
/// (<see href="../SealedPublicationGenerations">Sealed Publication Generations</see>, phase 1). No
/// producer writes a pointer yet. These cases are what make that phase real rather than dead code:
/// they build the generation layout by hand and assert the portal serves it — including the arm
/// that would catch the one silent way to get this wrong, a reader that resolves the pointer and
/// then composes its file paths under the SOURCE directory anyway.</para>
/// </summary>
public class PublicationGenerationTest
{
    private const string Identity = "s3461generation00000000000000000";
    private const string Source = "plugins";
    private const string Gen1 = "Systemorph-MeshWeaver.Plugins-1111-1";
    private const string Gen2 = "Systemorph-MeshWeaver-2222-1";

    /// <summary>
    /// The whole point, in one case: the flat directory still holds a COMPLETE, SEALED, OLDER
    /// publication (that is exactly what phase 2 leaves behind for readers pinned to an older
    /// platform), the pointer names a generation, and every read — the seal's listing, the bundle
    /// bytes, the module set, the module bytes — must come from the GENERATION.
    ///
    /// <para>🚨 A reader that resolves the pointer for the LISTING and then composes bundle paths
    /// under the source directory passes a naive test and is wrong in the worst way: it serves the
    /// flat publication's bytes under the generation's ETag, which is the mix the generation
    /// exists to prevent, wearing a token that says it is not. So the fixture makes the two
    /// publications differ in BYTES under the SAME file names.</para>
    /// </summary>
    [Fact]
    public void ThePointedToGeneration_IsWhatIsRead_BytesIncluded()
    {
        using var root = new TempRoot();
        var source = root.Source(Identity, Source);

        // The flat publication — complete and sealed, and NOT what should be read.
        WriteBundle(Path.Combine(source, "Store.zip"), "flat-store");
        Seal(source, "Store.zip");
        WriteModules(source, ("ai.module.nupkg", "flat-ai"));

        // The generation — same names, different bytes, a different module set.
        var generation = Path.Combine(source, Gen1);
        Directory.CreateDirectory(generation);
        WriteBundle(Path.Combine(generation, "Store.zip"), "gen-store");
        WriteBundle(Path.Combine(generation, "Edu.zip"), "gen-edu");
        Seal(generation, "Store.zip", "Edu.zip");
        WriteModules(generation, ("ai.module.nupkg", "gen-ai"), ("maps.module.nupkg", "gen-maps"));

        Point(source, Gen1);

        Assert.Equal(generation, ShippedPrebuiltBundles.PublicationDirectoryOf(source));

        var reading = PublishedBundleCatalogue.SealedPublicationOf(source);
        Assert.Equal(generation, reading.Directory);
        Assert.Equal(["Store.zip", "Edu.zip"], reading.Bundles);
        // The flat seal listed ONE bundle; reading it would have answered a single-name list.
        Assert.Equal("gen-store", PluginOf(Path.Combine(reading.Directory!, "Store.zip")));

        var modules = PublishedBundleCatalogue.SealedModulesOf(source);
        Assert.Equal(generation, modules.Directory);
        Assert.Equal(["ai.module.nupkg", "maps.module.nupkg"], modules.Modules);
        Assert.Equal("gen-ai", PluginOf(Path.Combine(
            modules.Directory!, PublishedBundleCatalogue.ModulesDirectoryName, "ai.module.nupkg")));
    }

    /// <summary>
    /// No pointer is the FLAT layout, unchanged — this is what every publication on every share
    /// looks like today, and it must keep reading exactly as it did. The resolution is opt-in by
    /// the writer, not by the reader.
    /// </summary>
    [Fact]
    public void NoPointer_IsTheFlatLayout_Unchanged()
    {
        using var root = new TempRoot();
        var source = root.Source(Identity, Source);
        WriteBundle(Path.Combine(source, "Store.zip"), "flat-store");
        Seal(source, "Store.zip");

        Assert.Equal(source, ShippedPrebuiltBundles.PublicationDirectoryOf(source));
        var reading = PublishedBundleCatalogue.SealedPublicationOf(source);
        Assert.Equal(source, reading.Directory);
        Assert.Equal(["Store.zip"], reading.Bundles);
    }

    /// <summary>
    /// 🚨 A pointer must never be able to address bytes OUTSIDE its own source directory. Each of
    /// these is refused and falls back to the flat publication — never followed, and never an
    /// exception that would take a boot down. The escape fixture puts a real, sealed publication
    /// at the escape target, so "refused" is asserted as *those bytes were not served*, not merely
    /// as "no crash".
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../secrets")]
    [InlineData("..\\secrets")]
    [InlineData("sub/dir")]
    [InlineData("/etc")]
    public void APointerThatIsNotABareName_IsRefused(string pointer)
    {
        using var root = new TempRoot();
        var source = root.Source(Identity, Source);
        WriteBundle(Path.Combine(source, "Store.zip"), "flat-store");
        Seal(source, "Store.zip");

        // A complete, sealed publication a followed escape would reach.
        var elsewhere = root.Source(Identity, "secrets");
        WriteBundle(Path.Combine(elsewhere, "Secret.zip"), "secret");
        Seal(elsewhere, "Secret.zip");

        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.PublicationPointerFileName), pointer + "\n");

        Assert.Equal(source, ShippedPrebuiltBundles.PublicationDirectoryOf(source));
        var reading = PublishedBundleCatalogue.SealedPublicationOf(source);
        Assert.Equal(["Store.zip"], reading.Bundles);
        Assert.DoesNotContain("Secret.zip", reading.Bundles!);
    }

    /// <summary>
    /// A pointer naming a generation that is NOT on disk — a retention sweep that deleted what the
    /// pointer still names, or a pointer written before its directory — falls back to the flat
    /// publication rather than answering "torn". While the flat copy exists (phase 2) that is the
    /// previous behaviour exactly; once it does not, the source directory carries no sentinel and
    /// the reader answers "being republished right now", which every consumer already backs off
    /// on. Never a mix, in either phase.
    /// </summary>
    [Fact]
    public void APointerToAnAbsentGeneration_FallsBackToTheFlatPublication()
    {
        using var root = new TempRoot();
        var source = root.Source(Identity, Source);
        WriteBundle(Path.Combine(source, "Store.zip"), "flat-store");
        Seal(source, "Store.zip");
        Point(source, "Systemorph-MeshWeaver-swept-away-1");

        Assert.Equal(source, ShippedPrebuiltBundles.PublicationDirectoryOf(source));
        Assert.Equal(["Store.zip"], PublishedBundleCatalogue.SealedPublicationOf(source).Bundles);
    }

    /// <summary>
    /// And with NO flat copy behind it (phase 3), the same fallback lands on a directory with no
    /// sentinel — which is read as the transient republish window, the 503 every consumer already
    /// waits out. The point is that the degradation is to a WAIT, never to a partial read.
    /// </summary>
    [Fact]
    public void APointerToAnAbsentGeneration_WithNoFlatCopy_ReadsAsTheRepublishWindow()
    {
        using var root = new TempRoot();
        var source = root.Source(Identity, Source);
        Point(source, "Systemorph-MeshWeaver-swept-away-1");

        var reading = PublishedBundleCatalogue.SealedPublicationOf(source);
        Assert.Null(reading.Bundles);
        Assert.Contains("being republished right now", reading.TornReason);
    }

    /// <summary>
    /// 🚨 An empty or blank pointer is the ONE window this layout has: a pointer being replaced can
    /// be read short. It resolves to the flat directory — the generation that applied a moment
    /// ago — and never to a mix. That is the whole trade: ~90 seconds of an in-place replacement
    /// becomes one small file write, and the failure mode changes from "a sealed mix nobody can
    /// detect" to "read it again".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   \n  \n")]
    public void ABlankPointer_ResolvesToTheDirectoryItIsIn(string content)
    {
        using var root = new TempRoot();
        var source = root.Source(Identity, Source);
        WriteBundle(Path.Combine(source, "Store.zip"), "flat-store");
        Seal(source, "Store.zip");
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.PublicationPointerFileName), content);

        Assert.Equal(source, ShippedPrebuiltBundles.PublicationDirectoryOf(source));
        Assert.Equal(["Store.zip"], PublishedBundleCatalogue.SealedPublicationOf(source).Bundles);
    }

    /// <summary>
    /// RETENTION: an older generation stays on the shelf so a reader that resolved the pointer
    /// before the swap can still finish its N+1 read. It must be invisible to everyone else — the
    /// listing, the bytes and the generation token all come from the pointed-to one, and moving
    /// the pointer between the two is what a consumer's <c>If-Match</c> sees as a moved
    /// publication (#3401).
    /// </summary>
    [Fact]
    public void ARetainedOlderGeneration_IsNeverServed_AndMovingThePointerMovesTheGeneration()
    {
        using var root = new TempRoot();
        var source = root.Source(Identity, Source);

        var older = Path.Combine(source, Gen1);
        Directory.CreateDirectory(older);
        WriteBundle(Path.Combine(older, "Store.zip"), "older");
        Seal(older, "Store.zip");

        var newer = Path.Combine(source, Gen2);
        Directory.CreateDirectory(newer);
        WriteBundle(Path.Combine(newer, "Store.zip"), "newer");
        WriteBundle(Path.Combine(newer, "Edu.zip"), "newer-edu");
        Seal(newer, "Store.zip", "Edu.zip");

        Point(source, Gen1);
        var first = PublishedBundleCatalogue.SealedPublicationOf(source);
        Assert.Equal(["Store.zip"], first.Bundles);
        Assert.Equal("older", PluginOf(Path.Combine(first.Directory!, "Store.zip")));

        Point(source, Gen2);
        var second = PublishedBundleCatalogue.SealedPublicationOf(source);
        Assert.Equal(["Store.zip", "Edu.zip"], second.Bundles);
        Assert.Equal("newer", PluginOf(Path.Combine(second.Directory!, "Store.zip")));

        // The generation token is what a consumer pins; a moved pointer MUST move it, or the
        // 412 that stops an N+1 read spanning two publications never fires.
        Assert.NotEqual(first.Generation, second.Generation);

        // …and the older generation is still readable by whoever already resolved it. That is the
        // retention contract: a pointer swap must never delete what a reader is mid-way through.
        Assert.Equal("older", PluginOf(Path.Combine(older, "Store.zip")));
    }

    /// <summary>
    /// The BOOT SEEDER reads the same way. It walks <c>&lt;root&gt;/&lt;identity&gt;/</c> for
    /// source directories, and a generation directory is one level further down — so the seeder
    /// must resolve, and must not mistake a retained generation for a source of its own.
    /// </summary>
    [Fact]
    public void TheBootSeeder_SeesOnlyThePointedToGenerationsBundles()
    {
        using var root = new TempRoot();
        var source = root.Source(Identity, Source);
        WriteBundle(Path.Combine(source, "Store.zip"), "flat-store");
        Seal(source, "Store.zip");
        var generation = Path.Combine(source, Gen1);
        Directory.CreateDirectory(generation);
        WriteBundle(Path.Combine(generation, "Store.zip"), "gen-store");
        WriteBundle(Path.Combine(generation, "Edu.zip"), "gen-edu");
        Seal(generation, "Store.zip", "Edu.zip");
        Point(source, Gen1);

        // SealedBundlesOf is the rule the seeder and the registry share — "what may be served from
        // this directory" — so pinning it here pins both.
        var sealedNames = PublishedBundleCatalogue.SealedBundlesOf(source);
        Assert.Equal(["Store.zip", "Edu.zip"], sealedNames);
        var resolved = ShippedPrebuiltBundles.PublicationDirectoryOf(source);
        Assert.All(sealedNames!, n => Assert.True(File.Exists(Path.Combine(resolved, n))));
        Assert.Equal("gen-store", PluginOf(Path.Combine(resolved, "Store.zip")));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    private static void Seal(string directory, params string[] bundles) =>
        File.WriteAllText(
            Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName),
            string.Join("\n", bundles) + "\n");

    private static void Point(string sourceDirectory, string generation) =>
        File.WriteAllText(
            Path.Combine(sourceDirectory, ShippedPrebuiltBundles.PublicationPointerFileName),
            generation + "\n");

    private static void WriteModules(string publicationDirectory, params (string Name, string Plugin)[] modules)
    {
        var dir = Path.Combine(publicationDirectory, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(dir);
        foreach (var (name, plugin) in modules)
            WriteBundle(Path.Combine(dir, name), plugin);
        File.WriteAllText(
            Path.Combine(dir, PublishedBundleCatalogue.ModulesIndexFileName),
            string.Join("\n", modules.Select(m => m.Name)) + "\n");
    }

    /// <summary>A bundle whose MANIFEST names which fixture wrote it — so "which publication's
    /// bytes were served" is a fact read off the archive, never inferred from the file name.</summary>
    private static void WriteBundle(string path, string plugin)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("meshweaver/manifest.json").Open());
        w.Write($"{{\"plugin\":\"{plugin}\"}}");
    }

    private static string PluginOf(string bundlePath)
    {
        using var zip = ZipFile.OpenRead(bundlePath);
        using var r = new StreamReader(zip.GetEntry("meshweaver/manifest.json")!.Open());
        var json = r.ReadToEnd();
        return System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("plugin").GetString()!;
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mw-gen-" + Guid.NewGuid().ToString("N"));

        public string Source(string identity, string source)
        {
            var dir = System.IO.Path.Combine(Path, identity, source);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
