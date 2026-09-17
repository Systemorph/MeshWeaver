#pragma warning disable CS1591
using System.IO;
using System.Linq;
using MeshWeaver.Hosting;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// What the per-NodeType bundle inventory reads, and what it REFUSES to read — the half
/// <c>SealedPublicationIndex</c> does not answer (MeshWeaver#3845 hole 4), over real archives on a
/// real temporary shelf.
///
/// <para>🚨 The reading must agree with the ADOPTER, not merely with the file system: a bundle the
/// seeding pass declines must never satisfy <see cref="PrebuiltBundleInventory.Carries"/>, or a hold
/// releases onto bytes that will never land (review on #4595). <c>SeedBundles</c> declines a whole
/// archive whose manifest names another framework identity — or names none — before it considers a
/// single assembly, so this reading does the same.</para>
/// </summary>
public class PrebuiltBundleInventoryTest
{
    private const string Identity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b";
    private const string Other = "sffffffffffffffffffffffffffffffff";
    private const string TypePath = "Plugins/Widget";
    private const string Fingerprint = "af1df481730e24ea";

    /// <summary>A real archive, written by the real writer, stamped for <paramref name="identity"/>.
    /// Its assembly bytes are this test assembly — a real PE image, as the adoption tests use.</summary>
    private static void WriteBundle(string directory, string fileName, string? identity, string? fingerprint)
    {
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, fileName));
        BundleWriter.Write(
            file, plugin: "Widget", version: "1.0.0",
            frameworkMvid: identity ?? "",
            assemblies:
            [
                new BundleWriter.AssemblyEntry(
                    TypePath,
                    () => File.OpenRead(typeof(PrebuiltBundleInventoryTest).Assembly.Location))
                {
                    SourceFingerprint = fingerprint,
                },
            ]);
    }

    private static string Shelf(out string identityDirectory)
    {
        var root = Directory.CreateTempSubdirectory("mw-inventory-test").FullName;
        identityDirectory = Path.Combine(root, Identity, "plugins");
        return root;
    }

    private static void Seal(string publicationDirectory)
        => File.WriteAllLines(
            Path.Combine(publicationDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName),
            Directory.EnumerateFiles(publicationDirectory, "*.zip").Select(Path.GetFileName)!);

    [Fact]
    public void ABundleForThisIdentity_IsCarried()
    {
        var root = Shelf(out var publication);
        WriteBundle(publication, "widget.zip", Identity, Fingerprint);
        Seal(publication);

        var inventory = PrebuiltBundleInventory.Read(imageDirectory: null, root, Identity);

        inventory.IsUsable.Should().BeTrue();
        inventory.Bundles.Should().Be(1);
        inventory.Carries(TypePath, Fingerprint).Should().BeTrue();
        inventory.Names(TypePath).Should().BeTrue();
    }

    [Fact]
    public void ABundleStampedForANOTHERIdentity_IsNotCarried()
    {
        var root = Shelf(out var publication);
        WriteBundle(publication, "widget.zip", Other, Fingerprint);
        Seal(publication);

        var inventory = PrebuiltBundleInventory.Read(imageDirectory: null, root, Identity);

        inventory.Carries(TypePath, Fingerprint).Should().BeFalse(
            "the seeding pass declines a whole archive stamped for another identity before it looks "
            + "at an assembly — a reading that folded it in would release a hold onto bytes that can "
            + "never adopt here");
        inventory.Names(TypePath).Should().BeFalse("and it is not even named, for the same reason");
        inventory.Bundles.Should().Be(0, "the denominator counts what was READ, not what was on disk");
    }

    [Fact]
    public void ALegacyBundleWithNoRecordedFingerprint_NamesTheTypeButSatisfiesNoHold()
    {
        var root = Shelf(out var publication);
        WriteBundle(publication, "widget.zip", Identity, fingerprint: null);
        Seal(publication);

        var inventory = PrebuiltBundleInventory.Read(imageDirectory: null, root, Identity);

        inventory.Names(TypePath).Should().BeTrue("a bundle DOES name this type");
        inventory.Carries(TypePath, Fingerprint).Should().BeFalse(
            "it records no fingerprint, so it can never prove that it was built from these sources");
        inventory.FingerprintsOf(TypePath).Should().Equal(PrebuiltBundleInventory.Unrecorded);
    }

    [Fact]
    public void AnUnsealedPublication_ContributesNothing()
    {
        var root = Shelf(out var publication);
        WriteBundle(publication, "widget.zip", Identity, Fingerprint);
        // No completion sentinel: the publication is mid-replace or died before the seal.

        var inventory = PrebuiltBundleInventory.Read(imageDirectory: null, root, Identity);

        inventory.Carries(TypePath, Fingerprint).Should().BeFalse(
            "the bundle list comes from the seal's own lines, so a torn publication contributes "
            + "nothing rather than half of itself");
    }

    [Fact]
    public void NoShelfAtAll_ReadsAsNotConfigured()
        => PrebuiltBundleInventory.Read(imageDirectory: null, publishedRoot: null, Identity)
            .Outcome.Should().Be(SealedReadOutcome.NotConfigured,
                "a deployment that consumes no bundles is a statement about the deployment, never an "
                + "empty shelf");

    [Fact]
    public void NoIdentity_ReadsAsNotConfigured()
    {
        var root = Shelf(out var publication);
        WriteBundle(publication, "widget.zip", Identity, Fingerprint);
        Seal(publication);

        PrebuiltBundleInventory.Read(imageDirectory: null, root, identity: null)
            .Outcome.Should().Be(SealedReadOutcome.NotConfigured,
                "every reading is scoped to ONE identity — the shelf is read as the adopter reads it");
    }
}
