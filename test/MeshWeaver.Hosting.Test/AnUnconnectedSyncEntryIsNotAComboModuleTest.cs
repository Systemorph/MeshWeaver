using System.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 MeshWeaver#4413: a space whose <c>_GitSync</c> names NO repository is not a combo module.
///
/// <para>The sync was created and never connected (no <c>repositoryUrl</c>), so nothing was ever
/// imported from git. The combo reader still listed the space as a module. The assembler then
/// refused it ("names no repository in either shape"), and that one refusal stopped the gate for
/// the whole root. The instance's verdict was NotVerifiable, with 0 of 80+ modules verified, on
/// every run.</para>
/// </summary>
public sealed class AnUnconnectedSyncEntryIsNotAComboModuleTest
{
    private static InstanceComboReader.SyncEntry Entry(
        string module, string? repositoryUrl, string id = GitHubSyncService.ConfigId) =>
        new(module, id, $"{module}/{id}", new GitHubSyncConfig
        {
            RepositoryUrl = repositoryUrl,
            Branch = "main",
        });

    /// <summary>The defect: an entry with no repository yielded a module the assembler refuses.</summary>
    [Fact]
    public void ASyncEntryWithoutARepository_IsNamedUnconnected_NotFoldedAsAModule()
    {
        var fold = InstanceComboReader.FoldSyncEntries([Entry("Hiring", repositoryUrl: null)]);

        fold.Coordinates.Should().NotContainKey("Hiring",
            "an entry that names no repository has imported nothing, so it has no coordinate");
        fold.Unconnected.Should().Equal(["Hiring"],
            "the excluded space is named, never silently dropped");
        InstanceComboReader.UnconnectedSyncCaveat("Hiring").Should().Contain("'Hiring'");
    }

    /// <summary>A blank URL is the same as a missing one.</summary>
    [Fact]
    public void ABlankRepositoryUrl_IsUnconnected()
    {
        var fold = InstanceComboReader.FoldSyncEntries([Entry("Hiring", repositoryUrl: "  ")]);

        fold.Coordinates.Should().BeEmpty();
        fold.Unconnected.Should().Equal(["Hiring"]);
    }

    /// <summary>NEGATIVE CONTROL: a connected entry is still a coordinate and is not called unconnected.</summary>
    [Fact]
    public void AConnectedSyncEntry_StaysACoordinate()
    {
        const string Repo = "https://github.com/Systemorph/ComboFixture";
        var fold = InstanceComboReader.FoldSyncEntries(
            [Entry("Widget", Repo), Entry("Hiring", repositoryUrl: null)]);

        fold.Coordinates.Keys.Should().Equal(["Widget"]);
        fold.Coordinates["Widget"].RepositoryUrl.Should().Be(Repo);
        fold.Coordinates["Widget"].ConfigPath.Should().Be($"Widget/{GitHubSyncService.ConfigId}");
        fold.Unconnected.Should().Equal(["Hiring"]);
    }

    /// <summary>If the primary entry is unconnected, an additional source that names a repository
    /// still makes the space a module, and the coordinate comes from that source.</summary>
    [Fact]
    public void AnUnconnectedPrimary_BesideAConnectedSource_UsesTheSource()
    {
        const string Repo = "https://github.com/Systemorph/ComboFixture";
        var fold = InstanceComboReader.FoldSyncEntries(
            [Entry("Space", repositoryUrl: null), Entry("Space", Repo, id: "_GitSync/docs")]);

        fold.Coordinates["Space"].RepositoryUrl.Should().Be(Repo);
        fold.Unconnected.Should().BeEmpty();
    }

    /// <summary>The primary entry still wins when both entries are connected.</summary>
    [Fact]
    public void TwoConnectedEntries_ThePrimaryWins()
    {
        var fold = InstanceComboReader.FoldSyncEntries(
            [Entry("Space", "https://github.com/o/secondary", id: "_GitSync/docs"),
             Entry("Space", "https://github.com/o/primary")]);

        fold.Coordinates["Space"].RepositoryUrl.Should().Be("https://github.com/o/primary");
    }
}
