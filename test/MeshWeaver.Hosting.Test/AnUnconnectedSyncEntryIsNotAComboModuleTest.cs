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

    /// <summary>
    /// REGRESSION (review on #7906): <c>GitHubSyncService.SaveConfig</c> can clear the URL after an
    /// import and keep the sync history. Such a space DID import from git, so it stays in the
    /// combo, and the assembler refuses it by name. It is not silently relabelled as authored in
    /// the mesh.
    /// </summary>
    [Fact]
    public void AUrlClearedAfterAnImport_StaysACoordinate_NotUnconnected()
    {
        var cleared = new InstanceComboReader.SyncEntry("Space", GitHubSyncService.ConfigId,
            $"Space/{GitHubSyncService.ConfigId}", new GitHubSyncConfig
            {
                RepositoryUrl = null,
                LastSyncCommitSha = new string('a', 40),
                LastSyncedAt = System.DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            });

        var fold = InstanceComboReader.FoldSyncEntries([cleared]);

        fold.Coordinates.Should().ContainKey("Space",
            "an entry with sync history imported content, so a blank URL does not make it mesh-authored");
        fold.Coordinates["Space"].LastSyncCommitSha.Should().Be(new string('a', 40));
        fold.Unconnected.Should().BeEmpty();
    }

    /// <summary>A recorded attempt alone counts as history: an attempt can import part of the content.</summary>
    [Fact]
    public void ARecordedAttemptAlone_CountsAsHistory()
    {
        InstanceComboReader.HasSyncHistory(new GitHubSyncConfig
        {
            LastSyncAttemptAt = System.DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
        }).Should().BeTrue();
        InstanceComboReader.HasSyncHistory(new GitHubSyncConfig()).Should().BeFalse(
            "NEGATIVE CONTROL: an entry with no field set has no history");
    }

    /// <summary>A historical entry with no URL never displaces a connected source.</summary>
    [Fact]
    public void AHistoricalPrimaryWithoutUrl_DoesNotDisplaceAConnectedSource()
    {
        const string Repo = "https://github.com/Systemorph/ComboFixture";
        var historical = new InstanceComboReader.SyncEntry("Space", GitHubSyncService.ConfigId,
            $"Space/{GitHubSyncService.ConfigId}", new GitHubSyncConfig
            {
                RepositoryUrl = null,
                LastSyncCommitSha = new string('a', 40),
            });

        var fold = InstanceComboReader.FoldSyncEntries(
            [historical, Entry("Space", Repo, id: "_GitSync/docs")]);

        fold.Coordinates["Space"].RepositoryUrl.Should().Be(Repo);
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
