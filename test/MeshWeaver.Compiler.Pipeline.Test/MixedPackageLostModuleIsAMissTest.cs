#pragma warning disable CS1591

using System.IO.Compression;
using System.Text.Json;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// MeshWeaver#7401 — a MIXED package (NodeTypes AND a runtime module) whose module the registry
/// declared but could not serve. Its NodeType assemblies arrive, so the adoption never reaches the
/// empty-bundle branch that names the <c>module '…': not served — …</c> miss; the miss was filtered
/// out of the NodeType coverage check and then dropped, and the outcome read as a clean
/// <see cref="BundleAdoptionKind.Adopted"/>. <c>/health</c> → <c>bundle_adoption</c> could not see
/// that the module was absent.
///
/// <para>The manifests are read off the WIRE through the real reader, because the miss text is the
/// contract between the registry that writes it and the consumer that classifies it.</para>
/// </summary>
public class MixedPackageLostModuleIsAMissTest
{
    private const string Registry = "https://registry.example.com";

    private const string ModuleNotServed =
        "module 'MeshWeaver.Sample': " + BundleReader.ModuleNotServedReason
        + "the registry holds no retained generation at version 2.4.0";

    private static BundleReader.Manifest ReadManifest(object manifest)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(
                archive.CreateEntry(NuGetPackageWriter.ManifestEntry).Open());
            entry.Write(JsonSerializer.Serialize(manifest));
        }
        return BundleReader.Read(buffer.ToArray()).Manifest!;
    }

    [Fact]
    public void AMixedPackageWhoseModuleWasNotServed_IsAMissNamingTheModule()
    {
        var manifest = ReadManifest(new
        {
            plugin = "Sample",
            version = "2.4.0",
            frameworkMvid = "sframework",
            misses = new[] { ModuleNotServed },
        });

        // Three NodeType assemblies arrived and were all seeded.
        var outcome = BundleOffering.AdoptedOutcome("Sample", Registry, 3, 3, manifest);

        outcome.Kind.Should().Be(BundleAdoptionKind.Adopted,
            "the NodeTypes that arrived WERE adopted — the kind stays truthful");
        outcome.IsMiss.Should().BeTrue(
            "the bundle arrived without the package's runtime module, which is exactly what bundle_adoption exists to show");
        outcome.Describe().Should().Contain("adopted 3/3")
            .And.Contain("module 'MeshWeaver.Sample'")
            .And.Contain("NOT IN THIS BUNDLE")
            .And.NotContain("ABSENT",
                "a module not served in this bundle says nothing about whether an earlier landed generation runs here");

        var ledger = new BundleAdoptionLedger();
        ledger.Record(outcome);
        ledger.Misses.Should().ContainSingle();
        ledger.Describe().Should().Contain("1 MISS(es)").And.Contain("MeshWeaver.Sample");
    }

    [Fact]
    public void OnlyModuleNotServedMisses_AreCarried_NodeTypeAndDivergenceMissesAreNot()
    {
        var manifest = ReadManifest(new
        {
            plugin = "Sample",
            version = "2.4.0",
            frameworkMvid = "sframework",
            misses = new[]
            {
                "Sample/Type/Widget: never compiled on the registry",
                "module 'MeshWeaver.Sample': divergent bytes for the shelved generation",
                ModuleNotServed,
            },
        });

        BundleOffering.ModulesNotServed(manifest).Should().Equal(ModuleNotServed);
    }

    // ---- negative controls ----

    [Fact]
    public void AMixedPackageWhoseModuleWasServed_IsNotAMiss()
    {
        var manifest = ReadManifest(new
        {
            plugin = "Sample",
            version = "2.4.0",
            frameworkMvid = "sframework",
            module = new { assemblyName = "MeshWeaver.Sample" },
        });

        var outcome = BundleOffering.AdoptedOutcome("Sample", Registry, 3, 3, manifest);

        outcome.IsMiss.Should().BeFalse();
        outcome.Describe().Should().Be("Sample: adopted 3/3");
    }

    [Fact]
    public void AContentOnlyPackage_StaysNotAMiss()
    {
        var manifest = ReadManifest(new
        {
            plugin = "Harness",
            version = "1.0.0",
            frameworkMvid = "sframework",
            assemblies = Array.Empty<object>(),
            contentOnly = true,
        });

        BundleOffering.ModulesNotServed(manifest).Should().BeEmpty();
        BundleOffering.ClassifyEmpty(manifest).Should().Be(BundleAdoptionKind.NothingToAdopt);
    }
}
