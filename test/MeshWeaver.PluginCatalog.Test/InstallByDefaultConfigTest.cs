#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The CONFIGURATION half of the default install — the half that had no coverage, and the half
/// that broke.
///
/// <para>The install logic was well tested and behaved perfectly: matching is source-scoped and
/// fails closed, so a pattern naming a source the installation does not have installs nothing.
/// What nothing tested was whether the pattern and the source AGREE. A local registry served its
/// checkout under the source name <c>MWP-main</c> (derived from the directory it happened to sit
/// in) while <c>InstallByDefault</c> stayed <c>Plugins/*</c>. Result: a deployment that came up
/// healthy — pods Running, migration complete, HTTPS 200 — with not one plugin installed and not
/// one line of complaint. These are pure, no-I/O assertions, so they run anywhere CI runs.</para>
/// </summary>
public class InstallByDefaultConfigTest
{
    private static IReadOnlyList<PluginGrantEntry> Patterns(params string[] raw) =>
        raw.Select(PluginGrantEntry.TryParse).Where(e => e is not null).Select(e => e!).ToList();

    [Fact]
    public void PatternNamingAConfiguredSource_IsMatchable()
    {
        InstanceAutoRegistrationService
            .UnmatchablePatterns(Patterns("Plugins/*"), ["Plugins"])
            .Should().BeEmpty();
    }

    [Fact]
    public void TheRealRegression_PluginsPatternAgainstAWorktreeNamedSource_IsReported()
    {
        // Exactly the live failure: source named after the folder, pattern naming the repo.
        var unmatchable = InstanceAutoRegistrationService
            .UnmatchablePatterns(Patterns("Plugins/*"), ["MWP-main"]);

        unmatchable.Should().HaveCount(1);
        unmatchable[0].Source.Should().Be("Plugins");
    }

    [Fact]
    public void SourceNameMatchIsCaseInsensitive()
    {
        // Source names are operator-typed config values, so casing must not decide whether a
        // deployment installs its plugins.
        InstanceAutoRegistrationService
            .UnmatchablePatterns(Patterns("plugins/*"), ["Plugins"])
            .Should().BeEmpty();
    }

    [Fact]
    public void OnlyTheUnmatchablePatternIsReported_NotTheWholeSet()
    {
        // A partially-wrong config must name the wrong entry, not condemn the good ones —
        // otherwise the operator cannot see which one to fix.
        var unmatchable = InstanceAutoRegistrationService.UnmatchablePatterns(
            Patterns("Plugins/*", "Education/Course", "Typo/*"), ["Plugins", "Education"]);

        unmatchable.Should().HaveCount(1);
        unmatchable[0].Source.Should().Be("Typo");
    }

    [Fact]
    public void NoSourcesAtAll_MakesEveryPatternUnmatchable()
    {
        // An installation configured to install things but serving/consuming nothing is
        // misconfigured, not merely idle.
        InstanceAutoRegistrationService
            .UnmatchablePatterns(Patterns("Plugins/*"), [])
            .Should().HaveCount(1);
    }

    [Fact]
    public void NoPatterns_IsNotAnError()
    {
        // The platform default: install nothing unless asked. Must never be flagged.
        InstanceAutoRegistrationService
            .UnmatchablePatterns(Patterns(), ["Plugins"])
            .Should().BeEmpty();
    }
}
