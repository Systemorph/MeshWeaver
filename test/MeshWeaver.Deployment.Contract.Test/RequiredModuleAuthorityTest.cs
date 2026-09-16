using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// What a record can and cannot SAY about the modules an instance requires (#4476).
///
/// <para><c>Modules:Required</c> is an ARRAY, and configuration merges arrays BY INDEX: a later
/// provider replaces the entries it NAMES and leaves every other index of the earlier one standing.
/// An array can express "replace entry N" and can never express "these and only these" — so a
/// record's list, rendered on its own, is an OVERLAY on the image's list and not a statement of
/// what the instance requires. Two consequences, both measured on pearl.meshweaver.cloud on
/// 2026-09-16: a list SHORTER than the image's requires the image's tail it never named, and an
/// EMPTY list renders nothing at all, so the image's list stands in full.</para>
///
/// <para>The record therefore renders a SCALAR claim beside the entries —
/// <c>Modules:RequiredIsAuthoritative</c> — which no index merge can touch. This suite pins the
/// rendering; <c>ConfiguredModuleActivationTest</c> in MeshWeaver.Compiler.Pipeline.Test pins what
/// the reading does with it, over a real two-provider configuration.</para>
/// </summary>
public class RequiredModuleAuthorityTest
{
    /// <summary>
    /// The key's SHAPE, which is all this project can see: it references only
    /// MeshWeaver.Deployment.Contract, so comparing the constant here against a literal proves the
    /// renderer did not drift — and could never catch a rename of the READER's constant in
    /// MeshWeaver.Mesh.Contract.
    ///
    /// <para>🚨 That cross-assembly assertion is the one that matters (rendered-but-never-read is
    /// indistinguishable from not rendered) and it lives in
    /// <c>ConfiguredModuleActivationTest.TheRENDERERAndTheREADERSpellTheClaimTheSameWay</c>, in
    /// MeshWeaver.Compiler.Pipeline.Test, which sees BOTH assemblies. Do not "strengthen" this one
    /// by adding a second literal beside the first: a check that compares a constant with its own
    /// spelling cannot fail for the reason it exists.</para>
    /// </summary>
    [Fact]
    public void TheAuthorityKeyLivesUnderTheModulesSection()
    {
        Assert.Equal("Modules:RequiredIsAuthoritative", DeploymentPortalConfig.RequiredIsAuthoritativeKey);
        Assert.StartsWith(DeploymentPortalConfig.ModulesSection + ":", DeploymentPortalConfig.RequiredIsAuthoritativeKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The chart's <c>Modules__Required__N</c> block names every key LITERALLY, so it has a
    /// hand-written ceiling; a slot above it reaches no container in Kubernetes. That was merely
    /// wrong while the entries were a by-index overlay (the image's entry stood); under the
    /// authority claim it means the module is NOT REQUIRED AT ALL. So the number the contract
    /// reports problems against has to be the number the template actually renders — read from the
    /// template, not restated.
    /// </summary>
    [Fact]
    public void TheDeclaredCeilingIsTheOneTheChartRenders()
    {
        var root = RepoRoot();
        Assert.SkipWhen(root is null, "repository tree not reachable from the test bin — the chart is read from it");
        var chart = Path.Combine(root!, "deploy", "helm", "templates", "memex-portal", "config.yaml");
        Assert.True(File.Exists(chart), $"the portal ConfigMap template is not at {chart}");

        var rendered = Regex.Matches(File.ReadAllText(chart), @"^\s*Modules__Required__(\d+):", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .Order()
            .ToArray();

        Assert.True(rendered.Length > 0, "the chart renders no Modules__Required__N key at all");
        Assert.Equal(0, rendered[0]);
        Assert.Equal(Enumerable.Range(0, rendered[^1] + 1), rendered);
        Assert.Equal(DeploymentPortalConfig.MaxChartRenderedRequiredModuleSlot, rendered[^1]);
    }

    [Fact]
    public void ASlotAboveTheChartsCeiling_IsREPORTED_NotSilentlyDropped()
    {
        var over = DeploymentPortalConfig.MaxChartRenderedRequiredModuleSlot + 1;
        var record = new DeploymentContent()
            .WithRequiredModuleSlot(over, "MeshWeaver.Mcp")
            .WithRequiredModulesAuthoritative();

        var problem = Assert.Single(DeploymentPortalConfig.ChartModuleSlotProblems(record));
        Assert.Contains("MeshWeaver.Mcp.dll", problem, StringComparison.Ordinal);
        Assert.Contains(over.ToString(), problem, StringComparison.Ordinal);

        // A deliverable slot is not a problem — the check is about the ceiling, not about slots.
        Assert.Empty(DeploymentPortalConfig.ChartModuleSlotProblems(
            new DeploymentContent().WithRequiredModuleSlot(DeploymentPortalConfig.MaxChartRenderedRequiredModuleSlot, "MeshWeaver.Mcp")));
        Assert.Empty(DeploymentPortalConfig.ChartModuleSlotProblems(null));
    }

    [Fact]
    public void TheCeilingIsTheCHARTS_SoTheRouteNeutralSpecProblemsDoesNotCarryIt()
    {
        // 🚨 The Aspire route injects whatever PortalConfig emits, ceiling and all, so it delivers
        // slot 20 correctly. A route-neutral "why the spec cannot bring an instance up" answer that
        // named it would be FALSE for an Aspire run — which is why the check is its own, chart-named
        // surface a Helm renderer asks alongside SpecProblems rather than something folded into it.
        var over = DeploymentPortalConfig.MaxChartRenderedRequiredModuleSlot + 1;
        var record = new DeploymentContent()
            .WithRequiredModuleSlot(over, "MeshWeaver.Mcp")
            .WithRequiredModulesAuthoritative();

        Assert.Empty(DeploymentPortalConfig.SpecProblems(record.PluginRepos, record.PreInstall));

        // And the emitted key is really there on both routes — the slot is delivered, it is the
        // CHART that would not carry it.
        Assert.Equal("MeshWeaver.Mcp.dll", DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire(null))[$"Modules__Required__{over}"]);
        Assert.Equal("MeshWeaver.Mcp.dll", DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm)[$"Modules__Required__{over}"]);
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public void ARecordThatClaimsNothing_RendersNoClaim()
    {
        // Every record in the fleet, today. The entries stay a by-index overlay and the reading is
        // exactly what it was — a default that changed would have un-required the four modules the
        // image names past index 4 on every instance at once.
        var config = DeploymentPortalConfig.PortalConfig(
            new DeploymentContent().WithRequiredModules("MeshWeaver.Speech"), PortalConfigOptions.Helm);

        Assert.Equal("MeshWeaver.Speech.dll", config["Modules__Required__0"]);
        Assert.False(config.ContainsKey("Modules__RequiredIsAuthoritative"));
    }

    [Fact]
    public void ARecordThatClaimsTheCompleteSet_RendersTheClaimBesideTheEntries()
    {
        var config = DeploymentPortalConfig.PortalConfig(
            new DeploymentContent()
                .WithRequiredModules("MeshWeaver.Speech", "MeshWeaver.Mcp")
                .WithRequiredModulesAuthoritative(),
            PortalConfigOptions.Helm);

        Assert.Equal("MeshWeaver.Speech.dll", config["Modules__Required__0"]);
        Assert.Equal("MeshWeaver.Mcp.dll", config["Modules__Required__1"]);
        Assert.Equal("true", config["Modules__RequiredIsAuthoritative"]);
    }

    [Fact]
    public void AnEmptyListThatClaimsTheCompleteSet_RendersTheCLAIM_AndNoEntries()
    {
        // 🚨 THE case #4476 was filed on. With no claim this record renders NOTHING about modules,
        // and "nothing" is indistinguishable from "this record has no opinion" — so the image's
        // list stands and the instance requires everything it did before. The claim is the only
        // thing on the wire that can say "none", which is why it is a scalar and not an entry.
        var config = DeploymentPortalConfig.PortalConfig(
            new DeploymentContent().WithRequiredModulesAuthoritative(), PortalConfigOptions.Helm);

        Assert.Equal("true", config["Modules__RequiredIsAuthoritative"]);
        Assert.DoesNotContain(config.Keys, key => key.StartsWith("Modules__Required__", StringComparison.Ordinal));
    }

    [Fact]
    public void TheClaimIsNeverRenderedAsFalse()
    {
        // An explicit false WITHDRAWS the claim in the reader, so rendering one would let a record
        // that says nothing cancel a claim layered under it. A record that does not claim renders
        // no key at all.
        var config = DeploymentPortalConfig.PortalConfig(
            new DeploymentContent().WithRequiredModulesAuthoritative(false), PortalConfigOptions.Helm);

        Assert.False(config.ContainsKey("Modules__RequiredIsAuthoritative"));
    }

    [Fact]
    public void BothRenderersCarryTheClaim()
    {
        // The parity contract: a Kubernetes pod and an Aspire container receive the same keys for
        // the same record, so "require nothing" cannot mean one thing locally and another in the
        // cluster.
        var record = new DeploymentContent().WithRequiredModules("MeshWeaver.Speech").WithRequiredModulesAuthoritative();

        Assert.Equal("true", DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm)["Modules__RequiredIsAuthoritative"]);
        Assert.Equal("true", DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire(null))["Modules__RequiredIsAuthoritative"]);
    }

    [Fact]
    public void TheCatalogConfigFileCarriesTheClaimToo()
    {
        // The chart delivers the module policy to the operator through BootConfigurationEntries
        // (HOSTING_CATALOG_CONFIG) as well. An entry list delivered there WITHOUT the claim reads
        // as a by-index overlay — the two routes must not disagree about which it is.
        var claimed = DeploymentPortalConfig.BootConfigurationEntries(
            new DeploymentContent().WithRequiredModules("MeshWeaver.Speech").WithRequiredModulesAuthoritative());
        Assert.Contains("Modules:RequiredIsAuthoritative=true", claimed);

        var unclaimed = DeploymentPortalConfig.BootConfigurationEntries(
            new DeploymentContent().WithRequiredModules("MeshWeaver.Speech"));
        Assert.Contains("Modules:Required:0=MeshWeaver.Speech.dll", unclaimed);
        Assert.DoesNotContain(unclaimed, entry => entry.StartsWith("Modules:RequiredIsAuthoritative", StringComparison.Ordinal));

        Assert.DoesNotContain(
            DeploymentPortalConfig.BootConfigurationEntries(null),
            entry => entry.StartsWith("Modules:RequiredIsAuthoritative", StringComparison.Ordinal));
    }

    [Fact]
    public void BOTHRoutesRenderTheSameSlots_ExplicitOnesIncluded()
    {
        // 🚨 The catalog config file used to render the CONTIGUOUS list alone and drop every
        // explicit RequiredModuleSlots entry, so two renderers of ONE record described different
        // required sets. Harmless while both were read as a by-index overlay; with the claim beside
        // them the two routes would state two different COMPLETE sets, and the route that dropped
        // the slot would say MCP is not required at all.
        var record = new DeploymentContent()
            .WithRequiredModules("MeshWeaver.Speech")
            .WithRequiredModuleSlot(7, "MeshWeaver.Mcp")
            .WithRequiredModulesAuthoritative();

        var portal = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm)
            .Where(kv => kv.Key.StartsWith("Modules__Required__", StringComparison.Ordinal))
            .Select(kv => $"{kv.Key.Replace("__", ":")}={kv.Value}")
            .Order()
            .ToArray();
        var catalog = DeploymentPortalConfig.BootConfigurationEntries(record)
            .Where(entry => entry.StartsWith("Modules:Required:", StringComparison.Ordinal))
            .Order()
            .ToArray();

        Assert.Equal(["Modules:Required:0=MeshWeaver.Speech.dll", "Modules:Required:7=MeshWeaver.Mcp.dll"], catalog);
        Assert.Equal(portal, catalog);
    }

    [Fact]
    public void ExplicitSlotsRideTheClaim()
    {
        // memex-cloud's shape: the contiguous list plus MCP at an explicit slot. Both are the
        // record's own entries, so both are inside the set the claim covers.
        var config = DeploymentPortalConfig.PortalConfig(
            new DeploymentContent()
                .WithRequiredModules("MeshWeaver.Speech")
                .WithRequiredModuleSlot(7, "MeshWeaver.Mcp")
                .WithRequiredModulesAuthoritative(),
            PortalConfigOptions.Helm);

        Assert.Equal("MeshWeaver.Speech.dll", config["Modules__Required__0"]);
        Assert.Equal("MeshWeaver.Mcp.dll", config["Modules__Required__7"]);
        Assert.Equal("true", config["Modules__RequiredIsAuthoritative"]);
    }

    [Fact]
    public void TheClaimRoundTripsThroughTheRecordJson()
    {
        // The record reaches the portal as ONE JSON value (Deployment:Record) as well, and a claim
        // that survived the ConfigMap but not the record would make the two disagree about the
        // instance's required set.
        var record = new DeploymentContent { RequiredModulesAuthoritative = true, RequiredModules = ImmutableList.Create("MeshWeaver.Speech.dll") };
        var bound = DeploymentRecordJson.Read(DeploymentRecordJson.Write(record));

        Assert.NotNull(bound);
        Assert.True(bound!.RequiredModulesAuthoritative);
        Assert.Equal(record.RequiredModules, bound.RequiredModules);
    }
}
