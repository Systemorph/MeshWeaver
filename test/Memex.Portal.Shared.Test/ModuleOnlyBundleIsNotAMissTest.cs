using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A bundle that carries no NodeType assemblies means two OPPOSITE things, and until #3768 both
/// were <see cref="BundleAdoptionKind.NoAssemblies"/> — a miss, rendered on <c>/health</c> as
/// "content the registry was meant to serve is compiled here instead".
///
/// <para>Measured 2026-09-12 on memex.systemorph.com: <c>25 adoption attempt(s), 25 adopted,
/// 13 MISS(es)</c> — and all thirteen (<c>AI</c>, <c>Anthropic</c>, <c>AppleIntelligence</c>,
/// <c>Maps</c>, <c>AzureBlob</c>, <c>AzureFoundry</c>, <c>Chat</c>, <c>Mcp</c>,
/// <c>Notifications</c>, <c>OpenAI</c>, …) were module packages whose modules had landed
/// correctly through the separate <c>ModuleLandingService</c> path. The portal read Degraded
/// permanently with nothing wrong with it, on the one instrument the delivery issues are triaged
/// with.</para>
///
/// <para>🚨 The falsification arms are the point of this file: the benign reading is taken ONLY
/// from a positive declaration. An empty archive that declares nothing stays a miss.</para>
/// </summary>
public class ModuleOnlyBundleIsNotAMissTest
{
    private static BundleReader.Manifest Manifest(
        BundleReader.ModuleRef? module = null,
        IReadOnlyList<string>? content = null,
        IReadOnlyList<string>? misses = null) =>
        new("AI", "1.7.1", "s3e3c80238a740a8cb895a72b0bfb9cd6",
            Assemblies: [], Module: module, Architecture: "linux-x64",
            Misses: misses, Content: content);

    [Fact]
    public void AModuleOnlyBundle_HasNothingToAdopt_AndIsNotAMiss()
    {
        var manifest = Manifest(module: new BundleReader.ModuleRef("MeshWeaver.AI", ["MeshWeaver.AI.dll"]));

        BundleOffering.ClassifyEmpty(manifest).Should().Be(BundleAdoptionKind.NothingToAdopt,
            "a package that DECLARES a module has no NodeTypes to offer and is complete as delivered");
        BundleOffering.OfferingOf(manifest).Should().Be("module 'MeshWeaver.AI'");

        new BundleAdoptionOutcome("AI", BundleAdoptionKind.NothingToAdopt, "https://registry")
            .IsMiss.Should().BeFalse("nothing was meant to be served, so nothing compiles here instead");
    }

    [Fact]
    public void AContentOnlyBundle_HasNothingToAdopt()
    {
        BundleOffering.ClassifyEmpty(Manifest(content: ["Doc/Readme.md"]))
            .Should().Be(BundleAdoptionKind.NothingToAdopt);
    }

    // ---- falsification arms: the benign reading must NOT be reachable by absence ----

    [Fact]
    public void AnEmptyArchiveThatDeclaresNothing_STAYS_AMiss()
    {
        BundleOffering.ClassifyEmpty(Manifest()).Should().Be(BundleAdoptionKind.NoAssemblies,
            "a producer that shipped an empty archive is a real defect — inferring 'nothing was "
            + "expected' from emptiness is exactly what would silence it");

        new BundleAdoptionOutcome("AI", BundleAdoptionKind.NoAssemblies, "https://registry")
            .IsMiss.Should().BeTrue();
    }

    [Fact]
    public void UnresolvedProducerMisses_DominateEveryDeclaration_AndStayAMiss()
    {
        var shortBundle = Manifest(
            module: new BundleReader.ModuleRef("MeshWeaver.AI", ["MeshWeaver.AI.dll"]),
            content: ["Doc/Readme.md"],
            misses: ["AI/Agent: no artifact for this lane"]);

        BundleOffering.ClassifyEmpty(shortBundle).Should().Be(BundleAdoptionKind.NoAssemblies,
            "the bake could not resolve a type it was asked for — the bundle is SHORT whatever "
            + "else it declares, and that is the miss #1751 exists to keep countable");
    }

    [Fact]
    public void AnUnreadableManifest_StaysAMiss()
    {
        BundleOffering.ClassifyEmpty(null).Should().Be(BundleAdoptionKind.NoAssemblies);
    }

    // ---- the rendered sentence keeps its denominator ----

    [Fact]
    public void TheLedgerNamesHowManyHadNothingToOffer_SoTheRestIsReadable()
    {
        var ledger = new BundleAdoptionLedger();
        ledger.Record(new BundleAdoptionOutcome(
            "Store", BundleAdoptionKind.Adopted, "https://registry", Adopted: 3, Offered: 3));
        ledger.Record(new BundleAdoptionOutcome(
            "AI", BundleAdoptionKind.NothingToAdopt, "https://registry"));
        ledger.Record(new BundleAdoptionOutcome(
            "Maps", BundleAdoptionKind.NothingToAdopt, "https://registry"));

        ledger.Misses.Should().BeEmpty("two module packages and one fully adopted package is a clean lane");

        var line = ledger.Describe();
        line.Should().Contain("3 adoption attempt(s)");
        line.Should().Contain("no misses");
        line.Should().Contain("2 carried no NodeTypes to adopt",
            "'3 attempts, 3 adopted, no misses' would invite the reader to conclude three packages "
            + "were served — the split is what makes the number readable");
    }

    [Fact]
    public void ARealMissIsStillLoud_AlongsideThePackagesThatOfferedNothing()
    {
        var ledger = new BundleAdoptionLedger();
        ledger.Record(new BundleAdoptionOutcome("AI", BundleAdoptionKind.NothingToAdopt, "https://registry"));
        ledger.Record(new BundleAdoptionOutcome(
            "SocialMedia", BundleAdoptionKind.FrameworkDeclined, "https://registry",
            Reason: "built against framework s72c27af, live framework is s414bfb2"));

        ledger.Misses.Should().ContainSingle().Which.PluginId.Should().Be("SocialMedia");
        ledger.Describe().Should().Contain("1 MISS(es)").And.Contain("FrameworkDeclined");
    }
}
