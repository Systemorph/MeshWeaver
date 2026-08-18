using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the About tab's installed-plugin inventory: the manifest→row mapping the grid binds
/// (name fallback, shortened module hash, no "null" cells), and that every localization key the
/// tab reads actually exists in the shipped catalog — a typo between <c>host.Localize("…")</c>
/// and <c>strings.en.json</c> renders the raw key to every user and no compiler catches it.
/// </summary>
public class AboutSettingsTabTest
{
    // Localizer echoes the key, so assertions pin WHICH key the line reads.
    private static string Echo(string key) => key;

    /// <summary>
    /// The About page states WHEN this build started serving, in the viewer's own zone.
    ///
    /// <para>A version answers "which build" and never "since when". Those are different questions,
    /// and the second is the one asked whenever somebody needs to know whether a portal actually
    /// rolled forward — a re-deploy of the same image leaves the version identical, so only a
    /// timestamp distinguishes it.</para>
    ///
    /// <para>🚨 Rendered in the VIEWER's zone, never as bare UTC. The stored value is UTC by
    /// contract; a viewer in Zurich reading "13:35" for a 13:35 UTC deploy would be an hour wrong
    /// in summer, and silently so.</para>
    /// </summary>
    [Fact]
    public void StartedAt_is_rendered_in_the_viewers_zone_not_utc()
    {
        // 13:35 UTC on a summer date → 15:35 in Zurich (CEST, UTC+2).
        var utc = new DateTimeOffset(2026, 8, 18, 13, 35, 0, TimeSpan.Zero);

        var markdown = AboutSettingsTab.StartedAtMarkdown(utc, "Europe/Zurich", Echo);

        markdown.Should().NotBeNull();
        markdown!.Should().Contain("15:35", "Zurich is UTC+2 in August — the viewer's clock, not the server's");
        markdown.Should().NotContain("13:35", "a bare UTC time is exactly the thing that misleads");
        markdown.Should().Contain("about.lastDeployed", "the label must come from the localization catalog");
    }

    /// <summary>
    /// An unavailable start time renders NOTHING rather than a fabricated one. The fallback for a
    /// host that refuses process introspection is <c>default</c>, and printing 01/01/0001 — or
    /// worse, "now" — would read as a fresh deploy on every render.
    /// </summary>
    [Fact]
    public void StartedAt_renders_nothing_when_the_start_time_is_unknown()
        => AboutSettingsTab.StartedAtMarkdown(default, "Europe/Zurich", Echo).Should().BeNull();

    [Fact]
    public void ToRows_maps_manifests_to_display_rows()
    {
        var rows = AboutSettingsTab.ToRows(new[]
        {
            new PackageManifest
            {
                Id = "edu", Name = "Education", Version = "1.0.6",
                ModuleVersion = "9148498b9487955bdeadbeef", Kind = PackageKind.Content, TargetPartition = "Edu"
            },
            // Name missing → the id is the display name; nullable fields render EMPTY, never "null".
            new PackageManifest { Id = "bare-plugin" }
        });

        rows.Should().HaveCount(2);
        rows[0].Name.Should().Be("Education");
        rows[0].Version.Should().Be("1.0.6");
        rows[0].Module.Should().Be("9148498b9487", "the module hash is shortened to 12 chars for display");
        rows[0].Kind.Should().Be("Content");
        rows[0].Partition.Should().Be("Edu");

        rows[1].Name.Should().Be("bare-plugin");
        rows[1].Version.Should().Be("");
        rows[1].Module.Should().Be("");
        rows[1].Partition.Should().Be("");
    }

    /// <summary>
    /// The update line renders for both verdicts and stays SILENT on <c>Unknown</c> — an install with
    /// no self-update wiring (or with checks switched off) must not be told it is "up to date" on the
    /// strength of a check that never ran.
    /// </summary>
    [Fact]
    public void UpdateStatusMarkdown_renders_both_verdicts_and_nothing_when_unknown()
    {
        // Localizer echoes the key, so the assertions pin WHICH key each verdict reads.
        static string Echo(string key) => key;

        var available = AboutSettingsTab.UpdateStatusMarkdown(
            new PlatformUpdateStatus(PlatformUpdateAvailability.UpdateAvailable, "3.0.0-ci.2400"), Echo);
        available.Should().Contain("about.updateAvailable").And.Contain("3.0.0-ci.2400");

        var current = AboutSettingsTab.UpdateStatusMarkdown(
            new PlatformUpdateStatus(PlatformUpdateAvailability.UpToDate, null), Echo);
        current.Should().Contain("about.upToDate");
        // The verdict is a two-state answer — "up to date" must never name a version.
        current.Should().NotContain("3.0.0");

        AboutSettingsTab.UpdateStatusMarkdown(PlatformUpdateStatus.Unknown, Echo).Should().BeNull();
    }

    [Fact]
    public void Every_localization_key_the_about_tab_reads_exists_in_the_catalog()
    {
        // The keys BuildContent / BuildInfoMarkdown / PluginsGrid resolve. A key missing from the
        // catalog does not fail anywhere at compile time — it just renders as the raw key.
        string[] used =
        [
            "ui.aboutMeshWeaver", "about.intro", "about.version", "about.buildCommit",
            "about.buildCommitMissing", "about.repository", "about.runtime",
            "about.updateStatus", "about.upToDate", "about.updateAvailable",
            "about.plugins.title", "about.plugins.none",
            "about.plugins.column.name", "about.plugins.column.version", "about.plugins.column.module",
            "about.plugins.column.kind", "about.plugins.column.partition"
        ];

        var catalogPath = Path.Combine(FindRepoRoot(),
            "src", "MeshWeaver.Messaging.Hub", "Localization", "strings.en.json");
        var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(catalogPath))!;

        var missing = used.Where(k => !catalog.ContainsKey(k)).ToList();
        missing.Should().BeEmpty($"the About tab renders these keys raw when absent: {string.Join(", ", missing)}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
