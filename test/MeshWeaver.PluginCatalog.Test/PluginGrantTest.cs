using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the grant-matching rules that decide what a registered instance may pull. This is the
/// authorization decision of the plugin-registry surface, so every branch is spelled out: a
/// whole-source grant, a single-plugin grant, the source boundary, and the empty grant a freshly
/// registered instance carries.
/// </summary>
public class PluginGrantTest
{
    // The real shape as of 2026-08-06: our company instance holds the whole platform repo plus
    // exactly ONE plugin out of the reinsurance repo — the case that forces two grant levels.
    private static readonly PluginGrant MemexGrant = new()
    {
        InstanceId = "memex",
        Entries =
        [
            new PluginGrantEntry { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages },
            new PluginGrantEntry { Source = "Reinsurance", PackageId = "UWDeepfield" },
        ],
    };

    [Theory]
    [InlineData("Plugins", "Store")]
    [InlineData("Plugins", "Agent")]
    [InlineData("Plugins", "AnythingElseTheRepoGains")]
    public void WholeSourceGrant_AllowsEveryPackageInThatSource(string source, string package)
        => Assert.True(MemexAllows(source, package));

    [Fact]
    public void SinglePluginGrant_AllowsExactlyThatPlugin()
        => Assert.True(MemexAllows("Reinsurance", "UWDeepfield"));

    [Theory]
    [InlineData("ClaimsDeepfield")]
    [InlineData("Ifrs17")]
    [InlineData("Pricing")]
    [InlineData("SST")]
    public void SinglePluginGrant_DeniesTheRestOfTheSameRepo(string package)
        => Assert.False(MemexAllows("Reinsurance", package));

    [Fact]
    public void GrantDoesNotLeakAcrossSources()
    {
        // "UWDeepfield" is granted in Reinsurance — that must not make a same-named package in
        // another source readable. Grants are (source, package) pairs, not bare package names.
        Assert.False(MemexAllows("Education", "UWDeepfield"));
        // And a whole-source grant on Plugins says nothing about Education.
        Assert.False(MemexAllows("Education", "AgenticEngineering"));
    }

    [Fact]
    public void SourceNameMatchIsCaseInsensitive()
    {
        // Source names are operator-typed config values, not wire identifiers.
        Assert.True(MemexAllows("plugins", "Store"));
        Assert.True(MemexAllows("REINSURANCE", "UWDeepfield"));
    }

    [Fact]
    public void PackageIdMatchIsCaseSensitive()
        // The catalog compares ids with StringComparer.Ordinal; the grant must not be laxer, or a
        // near-miss id would authorize a package the catalog considers different.
        => Assert.False(MemexAllows("Reinsurance", "uwdeepfield"));

    [Fact]
    public void FreshlyRegisteredInstance_IsEntitledToNothing()
    {
        // The normal post-registration state: identity without entitlement. This is what makes
        // self-service registration safe — anyone may register, nobody self-grants.
        var fresh = new PluginGrant { InstanceId = "somebody-elses-portal" };
        Assert.False(fresh.Allows("Plugins", "Store"));
        Assert.False(fresh.Allows("Reinsurance", "UWDeepfield"));
        Assert.False(fresh.Allows("Education", "AgenticEngineering"));
    }

    [Fact]
    public void AtiozGrant_CarriesOnlyThePlatformRepo()
    {
        // The customer portal gets MeshWeaver.Plugins and nothing else — in particular none of the
        // reinsurance plugins and none of the paid course content.
        var atioz = new PluginGrant
        {
            InstanceId = "atioz",
            Entries = [new PluginGrantEntry { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages }],
        };
        Assert.True(atioz.Allows("Plugins", "Store"));
        Assert.False(atioz.Allows("Reinsurance", "UWDeepfield"));
        Assert.False(atioz.Allows("Education", "AgenticEngineering"));
    }

    [Fact]
    public void EntryRendersAsSourceSlashPackage()
    {
        Assert.Equal("Plugins/*", new PluginGrantEntry { Source = "Plugins" }.ToString());
        Assert.Equal("Reinsurance/UWDeepfield",
            new PluginGrantEntry { Source = "Reinsurance", PackageId = "UWDeepfield" }.ToString());
    }

    private static bool MemexAllows(string source, string package) => MemexGrant.Allows(source, package);
}
