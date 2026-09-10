using System;
using System.IO;
using System.Linq;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#3876, the boot seeder's half. The publisher UNSEALS a publication before it replaces
/// it and re-seals LAST, and retention unseals before it removes an identity — so a
/// <c>_complete</c> observed by <c>File.Exists</c> can be gone by the time the read opens it.
///
/// <para>The catalogue readers and the four HTTP routes were fixed in #3877. This reader was not:
/// it kept <c>File.Exists</c> followed by an unguarded <c>File.ReadAllLines</c>, so the same race
/// threw <see cref="FileNotFoundException"/> (or <see cref="DirectoryNotFoundException"/>, which is
/// what a removed parent throws) out of <c>CompletePublishedBundlesOf</c> and into
/// <c>SeedBundles</c>' outer <c>Catch</c> — which abandons the WHOLE identity's adoption pass. One
/// source being replaced during a boot therefore made every OTHER sealed source on that identity
/// recompile as well.</para>
///
/// <para>An absent seal is a condition this loop already answers correctly: skip THAT source,
/// loudly, and seed the rest. That is what these tests pin — together with the fact that a seal
/// which is PRESENT but unreadable is still a fault that surfaces, never a "torn publication".</para>
/// </summary>
public class SeedingSurvivesASealRemovedMidReadTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-seed-seal-" + Guid.NewGuid().ToString("N"));

    private const string Identity = "s72c27afab89c100727e79e0559c59e32";

    private string IdentityDirectory => Path.Combine(root, Identity);

    /// <summary>A sealed source directory: the bundle on disk, and a sentinel listing it.</summary>
    private string SealedSource(string name, string bundle)
    {
        var dir = Path.Combine(IdentityDirectory, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, bundle), "zip");
        File.WriteAllText(
            Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), bundle + "\n");
        return dir;
    }

    /// <summary>
    /// A read that makes the seal vanish AT the open, for one source only — the only way to
    /// exercise the race deterministically. Even a reader that reinstates <c>File.Exists</c>
    /// observes a present seal before this operation removes it.
    /// </summary>
    private static Func<string, string[]> RemovingAtOpen(string sourceDirectory, bool wholeDirectory)
        => path =>
        {
            if (!path.StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return File.ReadAllLines(path);
            Assert.True(File.Exists(path), "the seal is present when the read begins");
            if (wholeDirectory)
                Directory.Delete(sourceDirectory, recursive: true);
            else
                File.Delete(path);
            return File.ReadAllLines(path);
        };

    [Theory]
    [InlineData(false)] // the seal alone is removed  -> FileNotFoundException before the fix
    [InlineData(true)]  // its directory goes with it -> DirectoryNotFoundException before the fix
    public void ASealRemovedWhileSeedingReads_SkipsOnlyThatSource(bool wholeDirectory)
    {
        // "alpha" sorts first, so the seeder reads the vanishing seal BEFORE the intact one: the
        // surviving bundle proves the pass continued rather than merely having finished early.
        var alpha = SealedSource("alpha", "Alpha.zip");
        var zulu = SealedSource("zulu", "Zulu.zip");

        var seeded = ShippedPrebuiltBundles.CompletePublishedBundlesOf(
            IdentityDirectory, logger: null, RemovingAtOpen(alpha, wholeDirectory));

        seeded.Should().Equal([Path.Combine(zulu, "Zulu.zip")],
            "a publication whose seal vanished mid-read is skipped, and every other sealed source "
            + "under the same identity is still seeded");
    }

    [Fact]
    public void AnIntactIdentity_SeedsEverySealedSource()
    {
        // The positive control for the two cases above: without it, a reader that skipped
        // everything would satisfy nothing but still look like a fix.
        var alpha = SealedSource("alpha", "Alpha.zip");
        var zulu = SealedSource("zulu", "Zulu.zip");

        ShippedPrebuiltBundles.CompletePublishedBundlesOf(IdentityDirectory, logger: null)
            .Should().Equal([Path.Combine(alpha, "Alpha.zip"), Path.Combine(zulu, "Zulu.zip")]);
    }

    [Fact]
    public void APresentButUnreadableSeal_IsNotTreatedAsAbsent()
    {
        // 🚨 Only ABSENCE is classified. A locked or denied seal is a fault, and a fault that wore
        // the "publication is torn, carry on" costume would let a permanently broken share seed
        // nothing while every log line said the publisher was mid-replace.
        SealedSource("alpha", "Alpha.zip");

        var thrown = Assert.Throws<IOException>(() => ShippedPrebuiltBundles.CompletePublishedBundlesOf(
            IdentityDirectory, logger: null, _ => throw new IOException("the share is unreachable")));

        thrown.Message.Should().Be("the share is unreachable");
    }

    [Fact]
    public void TheSeederAndTheCatalogue_ReadTheSealThroughTheSameOperation()
    {
        // The two copies of this classification is how the seeder kept the racing File.Exists after
        // #3877 fixed the catalogue's. Pin that there is one.
        var directory = Path.Combine(root, "one");
        Directory.CreateDirectory(directory);
        var seal = Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName);
        File.WriteAllText(seal, "Store.zip\n");

        ShippedPrebuiltBundles.ReadSealLines(seal).Should().Equal(["Store.zip"]);
        ShippedPrebuiltBundles.ReadSealLines(seal, path =>
        {
            File.Delete(path);
            return File.ReadAllLines(path);
        }).Should().BeNull("an absent-at-the-open seal reads as no seal, not as a fault");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
