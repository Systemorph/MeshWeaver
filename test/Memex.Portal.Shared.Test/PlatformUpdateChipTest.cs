using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Memex.Portal.Shared.SelfUpdate;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the header build chip: what it names, what it says, and what a click does.
///
/// <para>The behaviour worth protecting is that the chip renders in EVERY state. It is the answer to
/// "which instance am I on?", which is the question ahead of starting a thread round — and it can
/// only confirm a refresh landed if the running build was already on screen before it. A chip that
/// showed up only when an update was pending would announce availability and never arrival.</para>
/// </summary>
public class PlatformUpdateChipTest
{
    // Localizer echoes the key, so assertions pin WHICH key each state reads.
    private static string Echo(string key) => key;

    /// <summary>A fixed deployment moment: 13:35 UTC → 15:35 in Zurich (CEST, UTC+2).</summary>
    private static readonly DateTimeOffset Deployed = new(2026, 8, 18, 13, 35, 0, TimeSpan.Zero);

    private static PlatformUpdateChip Describe(PlatformUpdateStatus status) =>
        PlatformUpdateChip.Describe(
            status, "3.0.0-rc4.ci.4180", "memex-portal-7d9c-abcde", Deployed, "Europe/Zurich", Echo);

    /// <summary>
    /// The header shows a SHORT build id. A local build's full version carries the whole commit
    /// sha — <c>3.0.0-rc4.ci.0+8278244204d7e3d0cc95b1461c825383cf0875a9</c>, 48 characters of
    /// mostly hash — which is unreadable in a top bar sitting between two icon buttons. The
    /// component's own CSS already conceded the point by hiding the text entirely below 720px.
    ///
    /// <para>The sha is SHORTENED rather than dropped: on a local build every version is
    /// <c>ci.0</c>, so the sha is the only part that distinguishes one build from the next. The
    /// full string stays on the tooltip and on the About page.</para>
    /// </summary>
    [Theory]
    [InlineData("3.0.0-rc4.ci.0+8278244204d7e3d0cc95b1461c825383cf0875a9", "3.0.0-rc4.ci.0+8278244")]
    [InlineData("3.0.0-rc4.ci.4180+76380ec7503eca69d2d1d20ab8870360fc88acd6", "3.0.0-rc4.ci.4180+76380ec")]
    public void ShortVersion_keeps_the_build_and_abbreviates_the_sha(string full, string expected)
        => PlatformUpdateChip.ShortVersion(full).Should().Be(expected);

    /// <summary>A version with no build-metadata sha is already short — it must pass through whole.</summary>
    [Theory]
    [InlineData("3.0.0-rc4.ci.4180")]
    [InlineData("3.0.0")]
    [InlineData("")]
    public void ShortVersion_leaves_a_version_without_a_sha_alone(string full)
        => PlatformUpdateChip.ShortVersion(full).Should().Be(full);

    /// <summary>
    ///🚨 The header carries WHEN, never WHICH BUILD. A version string — even shortened — is an
    /// identifier an ordinary reader cannot act on, and it sat in the busiest strip of the portal.
    /// "Last deployed 08-18 15:35" answers the question people actually bring to it ("is this
    /// current?"), and anyone who needs the exact build has it on the About page, one click away,
    /// and on the tooltip without even that.
    /// </summary>
    [Fact]
    public void DisplayText_says_when_it_was_deployed_and_names_no_version()
    {
        const string full = "3.0.0-rc4.ci.0+8278244204d7e3d0cc95b1461c825383cf0875a9";
        var chip = PlatformUpdateChip.Describe(
            new PlatformUpdateStatus(PlatformUpdateAvailability.UpToDate, null),
            full, "memex-portal-7d9c-abcde", Deployed, "Europe/Zurich", Echo);

        chip.DisplayText.Should().Be("about.lastDeployed 08-18 15:35");
        chip.DisplayText.Should().NotContain("3.0.0", "the version left the header on purpose");
        chip.DisplayText.Should().NotContain("8278244", "the sha left with it");
        chip.Tooltip.Should().Contain(full, "the full build id must remain one hover away");
    }

    /// <summary>
    /// Even with an update pending the header stays on the deployment time. The pending BUILD is
    /// carried by the glyph (an up-arrow, coloured) and named in full on the tooltip — a version
    /// string in the bar would be the same unreadable identifier, just a newer one.
    /// </summary>
    [Fact]
    public void DisplayText_stays_a_deployment_time_when_an_update_is_pending()
    {
        var chip = PlatformUpdateChip.Describe(
            new PlatformUpdateStatus(PlatformUpdateAvailability.UpdateAvailable, "3.0.0-rc4.ci.4191"),
            "3.0.0-rc4.ci.4180", "memex-portal-7d9c-abcde", Deployed, "Europe/Zurich", Echo);

        chip.DisplayText.Should().Be("about.lastDeployed 08-18 15:35");
        chip.DisplayText.Should().NotContain("4191", "the pending version belongs on the tooltip, not the bar");
        chip.IsUpdate.Should().BeTrue("the glyph is what says an update is waiting");
        chip.Tooltip.Should().Contain("3.0.0-rc4.ci.4191");
    }

    /// <summary>
    /// An unknown deployment time leaves the header text empty — the glyph still renders, so the
    /// button is never blank-but-clickable. Nothing is invented to fill the slot.
    /// </summary>
    [Fact]
    public void DisplayText_is_null_when_the_deployment_time_is_unknown()
    {
        var chip = PlatformUpdateChip.Describe(
            new PlatformUpdateStatus(PlatformUpdateAvailability.UpToDate, null),
            "3.0.0-rc4.ci.4180", "memex-portal-7d9c-abcde", default, "Europe/Zurich", Echo);

        chip.DisplayText.Should().BeNull();
    }

    /// <summary>
    /// The header also carries WHEN this build started serving — the half a version cannot express.
    /// Compact by necessity (it shares a top bar with two icon buttons), and in the VIEWER's zone: a
    /// bare UTC clock is wrong by an hour for a Zurich reader in summer, silently.
    ///
    /// <para>🚨 NUMERIC, never a month NAME. <c>dd MMM</c> under <c>InvariantCulture</c> renders
    /// "18 Aug" — an English abbreviation hard-coded into a string a German viewer reads, which is
    /// the i18n rule broken in the one place it is hardest to notice. Numeric needs no catalog and
    /// no culture, and it matches the <c>yyyy-MM-dd HH:mm</c> the About page already prints, minus
    /// the year the header has no room for.</para>
    /// </summary>
    [Fact]
    public void ShortStartedAt_is_compact_and_in_the_viewers_zone()
    {
        var utc = new DateTimeOffset(2026, 8, 18, 13, 35, 0, TimeSpan.Zero);

        var text = PlatformUpdateChip.ShortStartedAt(utc, "Europe/Zurich");

        text.Should().Be("08-18 15:35", "Zurich is UTC+2 in August, and the bar has no room for a full stamp");
    }

    /// <summary>
    /// No month NAME in any language: the header must read identically for an English and a German
    /// viewer, because it is formatted, not translated.
    /// </summary>
    [Fact]
    public void ShortStartedAt_names_no_month_so_it_reads_the_same_in_every_language()
    {
        var text = PlatformUpdateChip.ShortStartedAt(
            new DateTimeOffset(2026, 8, 18, 13, 35, 0, TimeSpan.Zero), "Europe/Zurich")!;

        // Digits, separators and a colon only — a letter here could only be a month name, which is
        // English text smuggled into a localized UI.
        text.Should().MatchRegex(@"^\d{2}-\d{2} \d{2}:\d{2}$");
        text.Any(char.IsLetter).Should().BeFalse(
            "a month abbreviation would read 'Aug' to a German viewer as much as an English one");
    }

    /// <summary>Unknown start time renders nothing rather than an invented or epoch value.</summary>
    [Fact]
    public void ShortStartedAt_is_null_when_unknown()
        => PlatformUpdateChip.ShortStartedAt(default, "Europe/Zurich").Should().BeNull();

    [Fact]
    public void UpToDate_still_names_the_running_build_and_instance()
    {
        var chip = Describe(new PlatformUpdateStatus(PlatformUpdateAvailability.UpToDate, null));

        chip.DisplayText.Should().Be("about.lastDeployed 08-18 15:35");
        chip.IsUpdate.Should().BeFalse();
        chip.Action.Should().Be(PlatformUpdateChipAction.OpenAbout,
            "there is no newer build to reload onto, so the chip is a link to the full build identity");
        chip.Tooltip.Should().Contain("3.0.0-rc4.ci.4180").And.Contain("memex-portal-7d9c-abcde",
            "two replicas can run the same build, so the version alone cannot answer 'did my session move?'");
    }

    [Fact]
    public void Unknown_is_not_silent_it_still_shows_the_running_build()
    {
        // Unknown means nothing is polling — so no VERDICT may be claimed. The running build is a
        // fact regardless, and it is the whole point of the chip.
        var chip = Describe(PlatformUpdateStatus.Unknown);

        chip.DisplayText.Should().Be("about.lastDeployed 08-18 15:35");
        chip.IsUpdate.Should().BeFalse();
        chip.Tooltip.Should().Contain("3.0.0-rc4.ci.4180",
            "the running build is still a fact — it moved to the tooltip, it did not disappear");
        chip.Tooltip.Should().NotContain("about.upToDate",
            "an install with nothing polling must not be told it is up to date");
    }

    [Fact]
    public void UpdateAvailable_names_the_pending_build_and_offers_the_refresh()
    {
        var chip = Describe(
            new PlatformUpdateStatus(PlatformUpdateAvailability.UpdateAvailable, "3.0.0-rc4.ci.4191"));

        chip.IsUpdate.Should().BeTrue();
        chip.Action.Should().Be(PlatformUpdateChipAction.Refresh);
        chip.Tooltip.Should().Contain("about.updateAvailable").And.Contain("ui.updateRefreshHint");
        chip.Tooltip.Should().Contain("3.0.0-rc4.ci.4180",
            "the running build stays on the tooltip — it is what the new one will be compared against");
    }

    /// <summary>
    /// 🚨 The state that must not collapse into the one beside it. A hold is loud (it reads
    /// differently from an available update, per PlatformUpdateAvailability.UpdateHeld) but NOT
    /// actionable from the header: refreshing cannot clear it, and offering the button would teach
    /// the user to click at a problem they cannot fix from here.
    /// </summary>
    [Fact]
    public void UpdateHeld_says_so_but_offers_no_refresh()
    {
        var chip = Describe(
            new PlatformUpdateStatus(PlatformUpdateAvailability.UpdateHeld, "3.0.0-rc4.ci.4191"));

        chip.IsUpdate.Should().BeTrue("a held update is still a pending one — it must not look quiet");
        chip.Tooltip.Should().Contain("about.updateHeld").And.Contain("3.0.0-rc4.ci.4191");
        chip.Tooltip.Should().NotContain("about.updateAvailable",
            "a deployment that has REFUSED a build must not read like one that is about to take it");
        chip.Action.Should().Be(PlatformUpdateChipAction.OpenAbout,
            "a refresh cannot clear a hold");
    }

    [Fact]
    public void Every_localization_key_the_chip_reads_exists_in_the_catalog()
    {
        // A key missing from the catalog fails nowhere at compile time — it renders raw to the user.
        string[] used =
        [
            "about.version", "about.instance", "about.updateAvailable", "about.updateHeld",
            "ui.updateRefreshHint"
        ];

        var missing = used.Where(k => !Catalog("en").ContainsKey(k)).ToList();
        missing.Should().BeEmpty($"the chip renders these keys raw when absent: {string.Join(", ", missing)}");

        // German too: the chip is header chrome on every page, so a gap ships to every German user.
        var untranslated = used.Where(k => !Catalog("de").ContainsKey(k)).ToList();
        untranslated.Should().BeEmpty($"missing German: {string.Join(", ", untranslated)}");
    }

    private static Dictionary<string, string> Catalog(string locale)
    {
        var path = Path.Combine(FindRepoRoot(),
            "src", "MeshWeaver.Messaging.Hub", "Localization", $"strings.{locale}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
