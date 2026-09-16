using System.Collections.Immutable;
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
    /// 🚨 The two halves of the contract live in two assemblies that may not reference each other:
    /// the record renders from MeshWeaver.Deployment.Contract (ZERO MeshWeaver references — it
    /// ships inside the published Aspire package) and the host reads from MeshWeaver.Mesh.Contract.
    /// The key is therefore spelled twice, and a rename on one side would silently stop the other
    /// from ever seeing the claim: rendered and never read is indistinguishable from not rendered.
    /// This holds the spelling to the one the reader uses.
    /// </summary>
    [Fact]
    public void TheAuthorityKeyIsSpelledTheWayTheHostReadsIt()
    {
        Assert.Equal("Modules:RequiredIsAuthoritative", DeploymentPortalConfig.RequiredIsAuthoritativeKey);
        Assert.StartsWith(DeploymentPortalConfig.ModulesSection + ":", DeploymentPortalConfig.RequiredIsAuthoritativeKey, StringComparison.Ordinal);
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
