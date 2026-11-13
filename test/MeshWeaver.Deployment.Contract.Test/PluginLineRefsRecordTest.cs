using System.Collections.Immutable;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// A registry source's per-line refs, on the record (<see cref="PluginRepoMount.LineRefs"/>, policy
/// <c>major-lines-run-separately</c>, Doc/Architecture/MajorLinesRunSeparately §4b): the registry
/// serves the repository at <c>main3</c> to a v3 caller and at <c>ref</c> to everyone else.
///
/// <para>Pinned here: the line refs render NAME-keyed on both routes (the boot/Aspire entries and the
/// Helm portal config), so the chart's own <c>Sources:N</c> list and these keys cannot fall out of
/// step; a mount that declares none renders exactly what it rendered before; a consumer mount, a
/// non-numeric line and a blank ref are refused by name; and the field round-trips through the
/// record's own JSON.</para>
/// </summary>
public class PluginLineRefsRecordTest
{
    private static readonly PluginRepoMount Plugins = new()
    {
        Name = "Plugins", Url = "https://github.com/Systemorph/MeshWeaver.Plugins", Ref = "main", IsRegistrySource = true,
        LineRefs = ImmutableSortedDictionary<string, string>.Empty.Add("3", " main3 "),
    };

    private static readonly PluginRepoMount Education = new()
    {
        Name = "Education", Url = "https://github.com/Systemorph/MeshWeaver.Education", Ref = "main", IsRegistrySource = true,
    };

    [Fact]
    public void ADeclaredLineRef_RendersNameKeyed_OnTheBootRoute()
    {
        var entries = DeploymentPortalConfig.ConfigurationEntries([Education, Plugins], null);

        Assert.Contains("PluginCatalog:LineRefs:Plugins:3=main3", entries);
        Assert.DoesNotContain(entries, e => e.StartsWith("PluginCatalog:LineRefs:Education", StringComparison.Ordinal));
        // The configured ref is untouched — it is what every other line reads.
        Assert.Contains("PluginCatalog:Sources:1:Ref=main", entries);
    }

    [Fact]
    public void ADeclaredLineRef_RendersOnTheHelmPortalConfig_AndTheAspireRoute_Identically()
    {
        var record = new DeploymentContent { PluginRepos = [Plugins, Education] };

        var helm = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm);
        var aspire = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire(null));

        Assert.Equal("main3", helm["PluginCatalog__LineRefs__Plugins__3"]);
        Assert.Equal("main3", aspire["PluginCatalog__LineRefs__Plugins__3"]);
    }

    [Fact]
    public void Control_AMountWithoutLineRefs_RendersExactlyWhatItRenderedBefore()
    {
        Assert.Equal(
            [
                "PluginCatalog:Sources:0:Name=Education",
                "PluginCatalog:Sources:0:RepoPath=https://github.com/Systemorph/MeshWeaver.Education",
                "PluginCatalog:Sources:0:Ref=main",
            ],
            DeploymentPortalConfig.ConfigurationEntries([Education], null));
        var helm = DeploymentPortalConfig.PortalConfig(new DeploymentContent { PluginRepos = [Education] });
        Assert.DoesNotContain(helm.Keys, k => k.StartsWith("PluginCatalog__LineRefs", StringComparison.Ordinal));
    }

    [Fact]
    public void RefForLine_IsTheLinesRef_ElseTheConfiguredRef()
    {
        Assert.Equal("main3", Plugins.RefForLine("3"));
        Assert.Equal("main", Plugins.RefForLine("4"));
        Assert.Equal("main", Plugins.RefForLine(null));
        Assert.Equal("main", Education.RefForLine("3"));
    }

    [Theory]
    [InlineData(false, "3", "main3", "is not a registry source")]
    [InlineData(true, "v3", "main3", "a line is a platform major")]
    [InlineData(true, "3", " ", "names line 3 with no ref")]
    public void AnUnusableLineRef_IsRefusedByName(bool registrySource, string line, string lineRef, string expected)
    {
        var mount = new PluginRepoMount
        {
            Name = "Plugins", Url = "https://github.com/Systemorph/MeshWeaver.Plugins", IsRegistrySource = registrySource,
            LineRefs = ImmutableSortedDictionary<string, string>.Empty.Add(line, lineRef),
        };

        var (usable, problems) = DeploymentPortalConfig.Validate([mount]);

        Assert.Empty(usable);
        Assert.Contains(expected, Assert.Single(problems));
    }

    /// <summary>
    /// Two keys naming ONE line — <c>"3"</c>, <c>" 3 "</c>, <c>"03"</c> — are a named problem from
    /// <see cref="DeploymentPortalConfig.Validate"/>, never an exception, and <see cref="PluginRepoMount.NormalizedLineRefs"/>
    /// stays total on such a record.
    /// </summary>
    [Theory]
    [InlineData("3", " 3 ")]
    [InlineData("3", "03")]
    [InlineData(" 3 ", "03")]
    public void TwoKeysNamingOneLine_AreANamedProblem_NotAnException(string first, string second)
    {
        var mount = Plugins with
        {
            LineRefs = ImmutableSortedDictionary<string, string>.Empty.Add(first, "main3").Add(second, "other"),
        };

        var normalized = mount.NormalizedLineRefs;
        var (usable, problems) = DeploymentPortalConfig.Validate([mount]);

        Assert.Equal("3", Assert.Single(normalized).Key);
        Assert.Empty(usable);
        Assert.Contains("names line 3 twice in lineRefs", Assert.Single(problems));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("03")]
    [InlineData(" 3 ")]
    public void RefForLine_CanonicalisesTheLine(string line)
        => Assert.Equal("main3", Plugins.RefForLine(line));

    [Fact]
    public void TwoSourcesKeyingTheirLineRefsAlike_TheSecondIsRefused()
    {
        var twin = Plugins with { Name = "Plugins!", Url = "https://github.com/Systemorph/Other" };

        var (usable, problems) = DeploymentPortalConfig.Validate([Plugins, twin]);

        Assert.Equal("Plugins", Assert.Single(usable).Name);
        Assert.Contains("keys its lineRefs as 'Plugins'", Assert.Single(problems));
    }

    [Fact]
    public void LineRefs_RoundTripThroughTheRecordsJson()
    {
        var record = new DeploymentContent { PluginRepos = [Plugins] };

        var read = System.Text.Json.JsonSerializer.Deserialize<DeploymentContent>(
            DeploymentRecordJson.Write(record), DeploymentRecordJson.Options);

        Assert.NotNull(read);
        Assert.Equal("main3", Assert.Single(read.PluginRepos).NormalizedLineRefs["3"]);
        Assert.Contains("\"lineRefs\":{\"3\":\" main3 \"}", DeploymentRecordJson.Write(record));
    }
}
