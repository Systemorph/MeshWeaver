#pragma warning disable CS1591

using System.IO.Compression;
using System.Text.Json;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// MeshWeaver#7181 — a CONTENT-ONLY package (an <c>index.json</c> plus harness or plan files, no
/// NodeType source, no runtime module) reached the consumer as an archive with no assemblies, no
/// module section, no content list and no misses: byte-for-byte the empty archive of a producer
/// defect. <c>/health</c> → <c>bundle_adoption</c> therefore named every such package as a
/// <c>NoAssemblies</c> miss on two portals.
///
/// <para>The fix is a PRODUCER DECLARATION (<see cref="BundleReader.Manifest.ContentOnly"/>), read
/// off the wire. The falsification arms are the point: the benign reading is reachable only
/// through that statement, and the statement never outranks a producer miss or a declared
/// assembly that did not arrive.</para>
/// </summary>
public class ContentOnlyBundleIsNotAMissTest
{
    private static byte[] Archive(object manifest)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(
                archive.CreateEntry(NuGetPackageWriter.ManifestEntry).Open());
            entry.Write(JsonSerializer.Serialize(manifest));
        }
        return buffer.ToArray();
    }

    /// <summary>The registry's wire shape, read through the real reader — the field name on the
    /// wire is the contract, so it is pinned from JSON rather than from the record.</summary>
    [Fact]
    public void ADeclaredContentOnlyBundle_ReadOffTheWire_HasNothingToAdopt()
    {
        var (manifest, assemblies) = BundleReader.Read(Archive(new
        {
            plugin = "Harness",
            version = "1.0.0",
            frameworkMvid = "sframework",
            assemblies = Array.Empty<object>(),
            contentOnly = true,
        }));

        assemblies.Should().BeEmpty();
        manifest!.ContentOnly.Should().BeTrue("the wire field is `contentOnly`");
        BundleOffering.ClassifyEmpty(manifest).Should().Be(BundleAdoptionKind.NothingToAdopt,
            "the producer read the package tree and STATED there was nothing to compile");
        BundleOffering.OfferingOf(manifest).Should().Be("content only");
        new BundleAdoptionOutcome("Harness", BundleAdoptionKind.NothingToAdopt, "https://registry")
            .IsMiss.Should().BeFalse();
    }

    // ---- falsification arms ----

    /// <summary>The negative control: the same empty archive WITHOUT the statement is still the
    /// loud miss — the consumer never infers "content-only" from emptiness.</summary>
    [Fact]
    public void TheSameEmptyArchive_WithoutTheDeclaration_StaysAMiss()
    {
        var (manifest, _) = BundleReader.Read(Archive(new
        {
            plugin = "Harness",
            version = "1.0.0",
            frameworkMvid = "sframework",
            assemblies = Array.Empty<object>(),
        }));

        manifest!.ContentOnly.Should().BeNull("an older producer states nothing");
        BundleOffering.ClassifyEmpty(manifest).Should().Be(BundleAdoptionKind.NoAssemblies);
    }

    [Fact]
    public void AnExplicitFalse_StaysAMiss()
    {
        var manifest = new BundleReader.Manifest("Harness", "1.0.0", "sframework", Assemblies: [])
        {
            ContentOnly = false,
        };

        BundleOffering.ClassifyEmpty(manifest).Should().Be(BundleAdoptionKind.NoAssemblies);
    }

    /// <summary>A bundle that SHOULD carry assemblies: the producer recorded a NodeType it could
    /// not serve. No declaration outranks that — the bundle is short.</summary>
    [Fact]
    public void ProducerMisses_DominateTheContentOnlyDeclaration()
    {
        var manifest = new BundleReader.Manifest("Social", "1.2.0", "sframework", Assemblies: [],
            Misses: ["Social/Post: never compiled on this registry"])
        {
            ContentOnly = true,
        };

        BundleOffering.ClassifyEmpty(manifest).Should().Be(BundleAdoptionKind.NoAssemblies);
    }

    /// <summary>A torn bundle: assemblies DECLARED, none arrived. Still a miss whatever else it
    /// says about itself.</summary>
    [Fact]
    public void DeclaredAssembliesThatDidNotArrive_DominateTheContentOnlyDeclaration()
    {
        var manifest = new BundleReader.Manifest("Social", "1.2.0", "sframework",
            Assemblies: [new BundleReader.AssemblyRef("Social/Post", "Social/Post.dll")])
        {
            ContentOnly = true,
        };

        BundleOffering.ClassifyEmpty(manifest).Should().Be(BundleAdoptionKind.NoAssemblies);
    }

    // ---- the reason a NoAssemblies miss carries ----

    /// <summary>
    /// The NodeType misses a registry states now travel into the outcome's reason — before #7181
    /// only module misses did, so a package whose NodeTypes the registry never compiled rendered
    /// as a bare "the bundle carried no assemblies".
    /// </summary>
    [Fact]
    public void StatedReasons_NameTheNodeTypeMisses_AndTruncate()
    {
        var misses = Enumerable.Range(1, BundleOffering.StatedReasonsShown + 2)
            .Select(i => $"Social/Type{i}: never compiled on this registry")
            .ToArray();
        var manifest = new BundleReader.Manifest("Social", "1.2.0", "sframework", Assemblies: [],
            Misses: misses);

        var reason = BundleOffering.StatedReasons(manifest);

        reason.Should().StartWith(" — the registry says: ");
        reason.Should().Contain("Social/Type1: never compiled on this registry");
        reason.Should().NotContain($"Social/Type{BundleOffering.StatedReasonsShown + 1}:");
        reason.Should().EndWith("…(+2)");
    }

    [Fact]
    public void StatedReasons_AreEmpty_WhenTheProducerStatedNone()
    {
        BundleOffering.StatedReasons(null).Should().BeEmpty();
        BundleOffering.StatedReasons(
            new BundleReader.Manifest("Harness", "1.0.0", "sframework", Assemblies: []))
            .Should().BeEmpty();
    }
}
